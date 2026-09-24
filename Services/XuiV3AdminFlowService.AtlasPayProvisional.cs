using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// AtlasPay-specific two-stage provisional approval controls for the super-admin payment-status flow.
/// </summary>
public partial class XuiV3AdminFlowService
{
    /// <summary>Builds the first-stage control for a freshly verified pending AtlasPay wallet charge.</summary>
    /// <param name="paymentId">Positive internal users.db AtlasPay payment id.</param>
    /// <returns>An inline keyboard containing only the provisional-review action.</returns>
    /// <remarks>The callback carries only the local id; amount, customer and provider state are always reloaded.</remarks>
    private static InlineKeyboardMarkup BuildProvisionalAtlasPayStartKeyboard(int paymentId)
        => new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    "⚠️ تایید موقت شارژ",
                    AtlasPayProvisionalStartCallbackPrefix + paymentId)
            }
        });

    /// <summary>Handles start, confirmation, and cancellation callbacks for AtlasPay provisional wallet credit.</summary>
    /// <param name="botClient">Owned-bot Telegram client serving the authenticated super-admin.</param>
    /// <param name="callbackQuery">Callback containing only a local AtlasPay payment id.</param>
    /// <param name="mainMenu">Super-admin reply keyboard restored after the final decision.</param>
    /// <param name="cancellationToken">Cancellation token for provider, wallet, ledger, users.db, and Telegram work.</param>
    /// <returns><c>true</c> because the router calls this method only for AtlasPay provisional prefixes.</returns>
    /// <remarks>
    /// Every stage rechecks configured-super-admin authorization. The final stage performs a fresh official AtlasPay
    /// verification while holding the payment reconciliation gate. Direct tenant orders, terminal/mismatched responses,
    /// accepted underpayments and financial ambiguity can never be provisionally overridden.
    /// </remarks>
    private async Task<bool> TryHandleAtlasPayProvisionalCallbackAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        ReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        if (!IsConfiguredSuperAdmin(callbackQuery.From?.Id ?? 0))
        {
            await AnswerCallbackSafelyAsync(
                botClient, callbackQuery, "اجازه انجام این عملیات را ندارید.", true, cancellationToken);
            return true;
        }

        var data = callbackQuery.Data ?? string.Empty;
        var prefix = data.StartsWith(AtlasPayProvisionalConfirmCallbackPrefix, StringComparison.Ordinal)
            ? AtlasPayProvisionalConfirmCallbackPrefix
            : data.StartsWith(AtlasPayProvisionalCancelCallbackPrefix, StringComparison.Ordinal)
                ? AtlasPayProvisionalCancelCallbackPrefix
                : AtlasPayProvisionalStartCallbackPrefix;
        if (!int.TryParse(data[prefix.Length..], out var paymentId) || paymentId <= 0)
        {
            await AnswerCallbackSafelyAsync(
                botClient, callbackQuery, "شناسه پرداخت AtlasPay معتبر نیست.", true, cancellationToken);
            return true;
        }

        var payment = await _workflow.ReadAsync(async db =>
            await db.AtlasPayPaymentInfos.FindAsync(new object[] { paymentId }, cancellationToken));
        if (payment == null)
        {
            await AnswerCallbackSafelyAsync(
                botClient, callbackQuery, "پرداخت AtlasPay پیدا نشد.", true, cancellationToken);
            return true;
        }

        if (prefix == AtlasPayProvisionalCancelCallbackPrefix)
        {
            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                "تایید موقت AtlasPay لغو شد. هیچ تغییری در کیف پول انجام نشد.",
                null,
                cancellationToken);
            await AnswerCallbackSafelyAsync(botClient, callbackQuery, "لغو شد.", false, cancellationToken);
            return true;
        }

        if (prefix == AtlasPayProvisionalStartCallbackPrefix)
        {
            if (!AtlasPaySettlementService.CanApplyProvisionalCredit(payment))
            {
                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    BuildAtlasPayPaymentInfo(payment, null) +
                    "\n\nاین پرداخت برای تایید موقت مجاز نیست.",
                    null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(
                    botClient, callbackQuery, "این پرداخت قابل تایید موقت نیست.", true, cancellationToken);
                return true;
            }

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        "✅ تایید نهایی موقت",
                        AtlasPayProvisionalConfirmCallbackPrefix + payment.Id),
                    InlineKeyboardButton.WithCallbackData(
                        "انصراف",
                        AtlasPayProvisionalCancelCallbackPrefix + payment.Id)
                }
            });
            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                "⚠️ <b>تایید موقت شارژ AtlasPay</b>\n\n" +
                BuildAtlasPayPaymentInfo(payment, null) +
                "\n\nدر مرحله نهایی AtlasPay دوباره به‌صورت رسمی استعلام می‌شود. " +
                "اگر همچنان پرداخت تایید نشده باشد، فقط مبلغ پایه ذخیره‌شده یک‌بار به کیف پول اضافه می‌شود. " +
                "تایید رسمی بعدی کیف پول، ledger یا referral را دوباره افزایش نمی‌دهد.\n\nآیا ادامه می‌دهید؟",
                keyboard,
                cancellationToken);
            await AnswerCallbackSafelyAsync(
                botClient, callbackQuery, "برای تایید نهایی، دکمه سبز را بزنید.", false, cancellationToken);
            return true;
        }

        await ConfirmProvisionalAtlasPayAsync(
            botClient, callbackQuery, payment, mainMenu, cancellationToken);
        return true;
    }

    /// <summary>Performs the fresh provider verification and final AtlasPay provisional-credit decision.</summary>
    /// <param name="botClient">Owned-bot Telegram client used to edit the protected admin message.</param>
    /// <param name="callbackQuery">Final callback from a configured super-admin.</param>
    /// <param name="payment">AtlasPay row selected by internal users.db id.</param>
    /// <param name="mainMenu">Super-admin reply keyboard restored after processing.</param>
    /// <param name="cancellationToken">Cancellation token for inquiry, settlement, audit, and Telegram work.</param>
    /// <returns>A task completing after official settlement, provisional credit, duplicate detection, or safe rejection.</returns>
    private async Task ConfirmProvisionalAtlasPayAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        AtlasPayPaymentInfo payment,
        ReplyMarkup mainMenu,
        CancellationToken cancellationToken)
    {
        try
        {
            var settlement = await _atlasPayReconciliation.ReconcileAndApplyProvisionalAsync(
                payment.Id,
                callbackQuery.From.Id,
                payment.ChatId == 0 ? null : payment.ChatId,
                "admin-provisional-confirm-refresh",
                cancellationToken);
            await _workflow.ReloadAsync(payment, cancellationToken);

            if (AtlasPayStatuses.IsSuccess(payment.ProviderStatus) &&
                !payment.IsProvisionallyApproved)
            {
                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    "AtlasPay در بررسی نهایی پرداخت را رسماً تایید کرد؛ مسیر رسمی اجرا شد.\n\n" +
                    BuildAtlasPayPaymentInfo(payment, settlement),
                    null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(
                    botClient, callbackQuery, "پرداخت رسمی تایید و تسویه شد.", false, cancellationToken);
                return;
            }

            if (settlement.Status == NowPaymentsSettlementStatus.Applied &&
                payment.IsProvisionallyApproved)
            {
                try
                {
                    var actor = await GetActivityActorAsync(callbackQuery.From.Id);
                    await _activityLog.LogBotActionAsync(
                        "atlaspay_provisional_wallet_approved",
                        actor,
                        true,
                        new Dictionary<string, object>
                        {
                            ["paymentId"] = payment.Id,
                            ["providerOrderId"] = payment.ProviderOrderId ?? 0,
                            ["trackingCode"] = payment.TrackingCode ?? string.Empty,
                            ["paymentStatus"] = payment.ProviderStatus ?? string.Empty,
                            ["settlementStatus"] = settlement.Status.ToString(),
                            ["amountToman"] = payment.BaseAmountToman,
                            ["approvedByTelegramUserId"] = callbackQuery.From.Id
                        },
                        cancellationToken);
                }
                catch (Exception activityException)
                {
                    _logger.LogWarning(
                        activityException,
                        "AtlasPay provisional activity audit failed after durable credit. paymentId={PaymentId}, approvedBy={ApprovedBy}",
                        payment.Id,
                        callbackQuery.From.Id);
                }

                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    "✅ شارژ موقت AtlasPay ثبت شد.\n\n" +
                    BuildAtlasPayPaymentInfo(payment, settlement),
                    null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(
                    botClient, callbackQuery, "شارژ موقت ثبت شد.", false, cancellationToken);
                return;
            }

            if (payment.IsAddedToBalance)
            {
                await EditProvisionalMessageAsync(
                    botClient,
                    callbackQuery,
                    (payment.IsProvisionallyApproved
                        ? "شارژ موقت AtlasPay قبلاً ثبت شده و دوباره اعمال نشد.\n\n"
                        : "این پرداخت قبلاً به کیف پول اضافه شده و دوباره اعمال نشد.\n\n") +
                    BuildAtlasPayPaymentInfo(payment, settlement),
                    null,
                    cancellationToken);
                await AnswerCallbackSafelyAsync(
                    botClient, callbackQuery, "قبلاً اعمال شده است.", false, cancellationToken);
                return;
            }

            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                BuildAtlasPayPaymentInfo(payment, settlement) +
                "\n\nتایید موقت انجام نشد؛ پاسخ تازه provider یا وضعیت محلی اجازه این عملیات را نمی‌دهد.",
                null,
                cancellationToken);
            await AnswerCallbackSafelyAsync(
                botClient, callbackQuery, "تایید موقت مجاز نیست.", true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "AtlasPay provisional wallet confirmation failed. paymentId={PaymentId}, providerOrderId={ProviderOrderId}, approvedBy={ApprovedBy}",
                payment.Id,
                payment.ProviderOrderId,
                callbackQuery.From?.Id);
            await EditProvisionalMessageAsync(
                botClient,
                callbackQuery,
                "تایید موقت AtlasPay انجام نشد. استعلام نهایی یا عملیات مالی ناموفق بود.",
                null,
                cancellationToken);
            await AnswerCallbackSafelyAsync(
                botClient, callbackQuery, "تایید موقت انجام نشد.", true, cancellationToken);
        }
        finally
        {
            if (callbackQuery.Message?.Chat.Id is long chatId && chatId != 0)
            {
                try
                {
                    await botClient.SendMessage(
                        chatId,
                        "منوی اصلی",
                        replyMarkup: mainMenu,
                        cancellationToken: cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not restore the super-admin menu after AtlasPay provisional decision. chatId={ChatId}",
                        chatId);
                }
            }
        }
    }
}
