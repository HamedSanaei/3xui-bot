using Adminbot.Domain;
using Adminbot.Utils;
using System.Runtime.ExceptionServices;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Implements owned-colleague daily test grants and receipt-backed purchase of the same test.</summary>
public partial class XuiV3BotFlowService
{
    /// <summary>Bot/user state waiting for explicit approval of a persisted whole-toman test quote.</summary>
    private const string TrialStepConfirmPaid = "trial-confirm-paid";
    /// <summary>Caption for a grant/amount-bound inline paid-test approval, never an authorizing text command.</summary>
    private const string ConfirmPaidTrialText = "✅ تأیید خرید تست";

    /// <summary>Routes a private owned-bot test approval/cancellation bound to its durable actor, bot and displayed amount.</summary>
    /// <param name="botClient">Required current bot transport; callbacks are acknowledged through the existing bounded helper.</param>
    /// <param name="callback">Required Telegram callback with grant/amount-only data; account credentials are never encoded.</param>
    /// <param name="profile">Detached sender snapshot; the persisted global profile is reloaded before a new debit.</param>
    /// <param name="user">Nullable current conversation; funded recovery is independent of its transient fields.</param>
    /// <param name="mainReplyMarkup">Nullable owned main keyboard.</param>
    /// <param name="token">Cancellation of callback delivery, reads and panel execution.</param>
    /// <returns>True after handling or rejecting this test callback; no tenant state or unrelated conversation is changed.</returns>
    /// <remarks>Original private chat must equal the actor. An old preview carries its old whole-toman amount, so queued callbacks cannot approve an undisplayed rate change. The immutable grant remains reachable after /start or wallet navigation.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels delivery or panel work.</exception>
    /// <example><code>await HandleColleagueTrialCallbackAsync(client, callback, profile, state, keyboard, token);</code></example>
    private async Task<bool> HandleColleagueTrialCallbackAsync(ITelegramBotClient botClient, CallbackQuery callback,
        CredUser profile, User user, ReplyMarkup mainReplyMarkup, CancellationToken token)
    {
        await AnswerCallbackSafelyAsync(botClient, callback.Id, token);
        var parts = callback.Data?.Split(':');
        var cancel = parts?.Length == 3 && parts[0] == "x3" && parts[1] == "ctc";
        var confirm = parts?.Length == 4 && parts[0] == "x3" && parts[1] == "ct";
        var price = 0L;
        if ((!cancel && !confirm) || !Guid.TryParseExact(parts[2], "N", out _) ||
            (confirm && (!long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out price) || price <= 0)) ||
            !string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) ||
            callback.From?.Id != profile.TelegramUserId || callback.Message?.Chat.Id != profile.TelegramUserId ||
            callback.Message.Chat.Type != ChatType.Private)
            return true;
        var grant = await _colleagueTrialQuotaStore.FindAsync(parts[2], profile.TelegramUserId,
            BotContextAccessor.CurrentBotId, token);
        if (grant?.State != ColleagueTrialGrantState.Denied) return true;
        var currentProfile = await _credentialsDbContext.GetUserStatusWithId(profile.TelegramUserId);
        if (currentProfile == null || currentProfile.IsBlocked) return true;
        user ??= new User { Id = currentProfile.TelegramUserId };
        if (cancel)
        {
            var debit = await _credentialsDbContext.GetWalletOperationAsync($"colleague-paid-trial:{grant.Id}:debit", token);
            if (debit != null)
                await SendUnresolvedTrialAsync(botClient, callback.Message.Chat.Id,
                    BuildPaidTrialInlineKeyboard(grant.Id, checked(-debit.AmountToman)), true, token);
            else
            {
                await _colleagueTrialQuotaStore.ClearPaidQuoteAsync(grant.Id, currentProfile.TelegramUserId, grant.BotId, token);
                await ClearMatchingPaidTrialStateAsync(user, grant.Id);
                await botClient.SendMessage(callback.Message.Chat.Id, "فرایند دریافت اکانت تست لغو شد.",
                    replyMarkup: mainReplyMarkup, cancellationToken: token);
            }
            return true;
        }
        await ConfirmPaidColleagueTrialAsync(botClient, callback.Message, currentProfile, user, grant, price,
            mainReplyMarkup, token);
        return true;
    }

    /// <summary>Routes active owned colleagues through service selection, daily admission and paid confirmation.</summary>
    /// <param name="botClient">Required transport belonging to the active owned bot.</param>
    /// <param name="message">Required original Telegram sender/chat/message identity and selected reply action.</param>
    /// <param name="profile">Detached freshly read global sender profile; new grants/debits require active unblocked colleague status.</param>
    /// <param name="user">Current bot/user conversation, or null before its first state write.</param>
    /// <param name="mainReplyMarkup">Nullable owned main keyboard for terminal responses.</param>
    /// <param name="token">Cancellation of incoming execution and external requests, not post-attempt local settlement.</param>
    /// <returns>True after consuming this owned trial action; no tenant conversation or ordinary cooldown is changed.</returns>
    /// <remarks>
    /// Colleagues use their active role instead of the ordinary customer's phone/cooldown gate. The allowance is
    /// global across owned bots/types, but paid quote and confirmation belong to the original bot and sender.
    /// Restored unpaid quote reminders check global sale permission; already-debited reminders remain reachable.
    /// </remarks>
    /// <example><code>await HandleOwnedColleagueTrialAsync(client, message, currentProfile, state, keyboard, token);</code></example>
    private async Task<bool> HandleOwnedColleagueTrialAsync(ITelegramBotClient botClient, Message message,
        CredUser profile, User user, ReplyMarkup mainReplyMarkup, CancellationToken token)
    {
        user ??= new User { Id = profile.TelegramUserId };
        var text = message.Text.Trim();
        var isStart = text.Contains("اکانت تست", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("اکانت رایگان", StringComparison.OrdinalIgnoreCase);
        if (user.Flow == TrialFlowName && user.LastStep == TrialStepConfirmPaid && !isStart)
        {
            if (IsCancel(text))
            {
                // A committed purchase cannot be discarded as an unfunded conversation cancellation.
                var debit = await _credentialsDbContext.GetWalletOperationAsync(
                    $"colleague-paid-trial:{user.PurchaseSessionId}:debit", token);
                if (debit == null)
                {
                    if (!string.IsNullOrWhiteSpace(user.PurchaseSessionId))
                        await _colleagueTrialQuotaStore.ClearPaidQuoteAsync(user.PurchaseSessionId,
                            profile.TelegramUserId, BotContextAccessor.CurrentBotId, token);
                    await _state.ClearUserStatus(user);
                    await botClient.SendMessage(message.Chat.Id, "فرایند دریافت اکانت تست لغو شد.",
                        replyMarkup: mainReplyMarkup, cancellationToken: token);
                }
                else
                    await SendUnresolvedTrialAsync(botClient, message.Chat.Id, mainReplyMarkup, true, token);
                return true;
            }
            var pending = await _colleagueTrialQuotaStore.FindAsync(user.PurchaseSessionId, profile.TelegramUserId,
                BotContextAccessor.CurrentBotId, token);
            if (pending != null &&
                await _credentialsDbContext.GetWalletOperationAsync($"colleague-paid-trial:{pending.Id}:debit", token) == null &&
                await RejectClosedServiceOperationAsync(
                    botClient, message.Chat.Id, FindService(pending.ServiceKey), ServiceSalesOperation.Sale, token))
                return true;
            await botClient.SendMessage(message.Chat.Id, "برای خرید تست، دکمه تأیید پیش‌فاکتور را بزنید یا انصراف دهید.",
                replyMarkup: pending?.PaidQuoteToman is > 0
                    ? BuildPaidTrialInlineKeyboard(pending.Id, pending.PaidQuoteToman.Value) : mainReplyMarkup,
                cancellationToken: token);
            return true;
        }

        if (!profile.IsColleague || profile.IsBlocked)
        {
            await _state.ClearUserStatus(user);
            await botClient.SendMessage(message.Chat.Id, "دریافت تست روزانه فقط برای همکار فعال در ربات اصلی در دسترس است.",
                replyMarkup: mainReplyMarkup, cancellationToken: token);
            return true;
        }

        if (isStart || user.Flow != TrialFlowName)
        {
            await _state.ClearUserStatus(user);
            await _state.SaveUserStatus(new User
            {
                Id = profile.TelegramUserId, Flow = TrialFlowName, LastStep = TrialStepSelectService
            });
            await botClient.SendMessage(message.Chat.Id,
                $"نوع اکانت تست را انتخاب کنید:\nروزانه تا {_colleagueDailyFreeTrialLimit} تست رایگان بین همه ربات‌های اصلی؛ پس از آن همین تست با تعرفه همکار قابل خرید است.",
                replyMarkup: BuildTrialServiceReplyKeyboard(), cancellationToken: token);
            return true;
        }

        var serviceKey = TryGetTrialServiceKey(text);
        if (user.LastStep != TrialStepSelectService || string.IsNullOrWhiteSpace(serviceKey))
        {
            await botClient.SendMessage(message.Chat.Id, "یکی از نوع‌های تست نمایش‌داده‌شده را انتخاب کنید.",
                replyMarkup: BuildTrialServiceReplyKeyboard(), cancellationToken: token);
            return true;
        }
        var grant = await _colleagueTrialQuotaStore.ReserveAsync(profile.TelegramUserId,
            BotContextAccessor.CurrentBotId, serviceKey,
            $"trial:{BotContextAccessor.CurrentBotId}:{message.Chat.Id}:{message.MessageId}",
            _colleagueDailyFreeTrialLimit, DateTime.UtcNow, token);
        if (grant.State == ColleagueTrialGrantState.Denied)
            await OfferPaidColleagueTrialAsync(botClient, message.Chat.Id, profile, user, grant, token);
        else
            await CompleteFreeColleagueTrialAsync(botClient, message, profile, user, grant, mainReplyMarkup, token);
        return true;
    }

    /// <summary>Creates or safely reads back one daily free grant, settling quota before any account notification.</summary>
    /// <param name="botClient">Required owned-bot transport.</param>
    /// <param name="message">Original message; its grant identity is already durably reserved.</param>
    /// <param name="profile">Authorized detached active colleague profile.</param>
    /// <param name="user">Required current bot/user conversation to clear after a terminal outcome.</param>
    /// <param name="grant">Required immutable free receipt for this actor, bot and test service.</param>
    /// <param name="mainReplyMarkup">Nullable main keyboard after delivery or failure.</param>
    /// <param name="token">Cancellation of progress, panel requests and delivery.</param>
    /// <returns>A task completing after delivery or an explicit unresolved/terminal notice.</returns>
    /// <remarks>Only the persisted executor winner may settle missing/Reserved creation evidence. Losing/restarted executions are GET-only and never release another execution's slot. A failed progress or result send cannot grant a second POST. The successful typed trial audit follows the Telegram-only global logging preference.</remarks>
    /// <exception cref="OperationCanceledException">The caller cancels; local quota settlement still runs after a winning attempt.</exception>
    /// <example><code>await CompleteFreeColleagueTrialAsync(client, message, profile, state, grant, keyboard, token);</code></example>
    private async Task CompleteFreeColleagueTrialAsync(ITelegramBotClient botClient, Message message, CredUser profile,
        User user, ColleagueTrialGrant grant, ReplyMarkup mainReplyMarkup, CancellationToken token)
    {
        if (grant.State == ColleagueTrialGrantState.Released)
        {
            await botClient.SendMessage(message.Chat.Id, "این درخواست تست ناموفق بسته شده است؛ برای درخواست جدید نوع تست را دوباره انتخاب کنید.",
                replyMarkup: BuildTrialServiceReplyKeyboard(), cancellationToken: token);
            return;
        }
        var winner = await _colleagueTrialQuotaStore.TryStartFreeCreationAsync(grant.Id, profile.TelegramUserId,
            grant.BotId, DateTime.UtcNow, token);
        if (!winner)
        {
            var operation = await _trialCreationOperations.FindAsync(grant.OperationKey, token);
            if (operation == null || operation.Outcome == XuiV3CreationOutcome.Reserved)
            {
                await SendUnresolvedTrialAsync(botClient, message.Chat.Id, mainReplyMarkup, false, token);
                return;
            }
        }
        using var timing = XuiOperationTiming.Start();
        XuiV3AccountCreationResult creation;
        try
        {
            if (winner)
                await botClient.SendMessage(message.Chat.Id, "در حال ساخت اکانت تست، لطفاً چند لحظه صبر کنید...",
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: token);
            creation = await _purchaseService.CreateTrialAccountAsync(profile, BuildConfiguredPanelServerInfo(),
                grant.ServiceKey, 1, TrialTrafficBytes(grant.ServiceKey), TrialDays,
                $"{grant.ServiceKey}-colleague-daily-free", grant.OperationKey, 0, token);
        }
        finally
        {
            // Free capacity is decided from panel proof, never from the following Telegram notification.
            grant = await _colleagueTrialQuotaStore.SettleAsync(grant.OperationKey, DateTime.UtcNow, CancellationToken.None);
        }
        if (!creation.Success || grant.State != ColleagueTrialGrantState.Consumed)
        {
            if (grant.State == ColleagueTrialGrantState.Uncertain)
                await SendUnresolvedTrialAsync(botClient, message.Chat.Id, mainReplyMarkup, false, token);
            else
                await botClient.SendMessage(message.Chat.Id,
                    "ساخت تست انجام نشد و سهمیه رایگان این درخواست آزاد شد.\n" + XuiV3UserSafeError.ForAccountCreation(creation.Message),
                    replyMarkup: mainReplyMarkup, cancellationToken: token);
            await _state.ClearUserStatus(user);
            return;
        }
        LogXuiOperationOutcome("ساخت اکانت تست روزانه همکار", "موفق", profile, timing,
            accountEmail: creation.Email, source: grant.ServiceKey, requestedCount: 1, successfulCount: 1,
            isTrialAccount: true);
        await _state.ClearUserStatus(user);
        await SendTrialAccountAsync(botClient, message.Chat.Id, creation, mainReplyMarkup, token);
    }

    /// <summary>Persists and displays a positive live colleague price for the identical test after free admission is denied.</summary>
    /// <param name="botClient">Required active owned-bot transport.</param>
    /// <param name="chatId">Original Telegram chat, never an arbitrary delivery destination.</param>
    /// <param name="profile">Detached active global colleague profile.</param>
    /// <param name="user">Required current bot/user state; only the matching paid session is updated, never unrelated navigation.</param>
    /// <param name="grant">Denied exact actor/bot receipt; an already funded/started quote must not be repriced.</param>
    /// <param name="token">Cancellation of quote persistence and preview delivery.</param>
    /// <returns>A task completing after the price and approval keyboard are delivered; no wallet or panel mutation occurs.</returns>
    /// <remarks>
    /// Charges actual byte volume plus three daily fees using ordinary colleague rates. National 100 MiB never
    /// silently becomes a 1 GiB paid account. A changed price requires another explicit confirmation. The global
    /// sale switch must permit this category before a new unfunded quote is persisted; free allowances are unchanged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The live test catalog cannot produce a valid positive price.</exception>
    /// <example><code>await OfferPaidColleagueTrialAsync(client, message.Chat.Id, profile, state, deniedGrant, token);</code></example>
    private async Task OfferPaidColleagueTrialAsync(ITelegramBotClient botClient, ChatId chatId, CredUser profile,
        User user, ColleagueTrialGrant grant, CancellationToken token)
    {
        var service = FindService(grant.ServiceKey);
        if (await RejectClosedServiceOperationAsync(
                botClient, chatId, service, ServiceSalesOperation.Sale, token))
            return;

        var bytes = TrialTrafficBytes(grant.ServiceKey);
        var price = _purchaseService.ResolveColleagueTrialPriceToman(grant.ServiceKey, bytes, TrialDays);
        grant = await _colleagueTrialQuotaStore.SetPaidQuoteAsync(grant.Id, profile.TelegramUserId, grant.BotId,
            price, DateTime.UtcNow, token);
        if (grant == null)
        {
            await SendUnresolvedTrialAsync(botClient, chatId, null, true, token);
            return;
        }
        if (user.Flow == TrialFlowName && (user.LastStep == TrialStepSelectService || user.PurchaseSessionId == grant.Id))
        {
            user.LastStep = TrialStepConfirmPaid;
            user.SelectedCountry = grant.ServiceKey;
            user.PurchaseSessionId = grant.Id;
            await _state.SaveUserStatus(user);
        }
        await botClient.SendMessage(chatId,
            "سهمیه تست رایگان برای این درخواست در دسترس نیست؛ همین تست را می‌توانید با تعرفه عادی همکار بخرید.\n\n" +
            $"سرویس: {service.DisplayName}\nحجم: {XuiV3PurchaseService.FormatTrafficSize(bytes, 1)}\nمدت: {TrialDays} روز\n" +
            $"💰 قیمت همکار: {price.FormatCurrency()}\n\nپس از تأیید، مبلغ از کیف پول ربات کسر می‌شود.",
            replyMarkup: BuildPaidTrialInlineKeyboard(grant.Id, price), cancellationToken: token);
    }

    /// <summary>Settles one confirmed paid test using immutable sufficient-balance receipts and a sole durable creation executor.</summary>
    /// <param name="botClient">Required transport for the originating owned bot.</param>
    /// <param name="message">Original private-chat preview message; this actor is the sole delivery recipient.</param>
    /// <param name="profile">Fresh detached global profile; a new debit requires active unblocked colleague status.</param>
    /// <param name="user">Current bot/user state; unrelated or cleared navigation cannot discard this durable purchase.</param>
    /// <param name="grant">Exact actor/bot Denied grant loaded from the inline callback, never a client-supplied entity.</param>
    /// <param name="displayedPriceToman">Positive whole-toman amount encoded by this successfully displayed preview; a new debit must match it.</param>
    /// <param name="mainReplyMarkup">Nullable main keyboard after a terminal paid result.</param>
    /// <param name="token">Cancellation of incoming execution and external requests; financial settlement uses an independent local token.</param>
    /// <returns>A task completing after delivery, refund, insufficient-funds prompt or explicit unresolved notice.</returns>
    /// <remarks>
    /// credentials.db atomically commits the global toman debit and unique receipt; users.db appends a receipt-linked
    /// ledger separately. Repeated confirmations/restarts reuse both keys. Only the claim winner may POST or classify
    /// absent/Reserved evidence after its invocation stops. Definitive non-creation permits exactly one equal refund;
    /// ambiguous POST keeps the debit for reconciliation, never a blind refund or new POST. Applied creation survives
    /// notification failure. A prepaid retry honors the original receipt price even if rates or the current role change.
    /// A new sufficient-balance debit also requires live global sale permission immediately before the debit.
    /// Once that receipt commits, recovery ignores later category closure: creation/read-back/refund follows the
    /// same original receipt without a second charge, blind refund or replay. Renewal permission is never consulted.
    /// No tenant owner balance, partner profit or payment-provider invoice is created.
    /// The successful typed paid-trial audit follows the global Telegram channel preference without disabling
    /// financial backup intent, wallet receipts or local diagnostics.
    /// Applied paid trials queue optional website mirroring as a local durable draft; the sync worker performs
    /// website owner enrichment and sending. The paid receipt and ledger are not deferred or recalculated.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels; any winning attempt is classified and safely refunded or held first.</exception>
    /// <exception cref="InvalidOperationException">Persisted actor/amount/creation identities conflict, or live pricing is invalid.</exception>
    /// <example><code>await ConfirmPaidColleagueTrialAsync(client, previewMessage, profile, state, grant, displayedPrice, keyboard, token);</code></example>
    private async Task ConfirmPaidColleagueTrialAsync(ITelegramBotClient botClient, Message message, CredUser profile,
        User user, ColleagueTrialGrant grant, long displayedPriceToman, ReplyMarkup mainReplyMarkup, CancellationToken token)
    {
        if (grant?.State != ColleagueTrialGrantState.Denied || grant.PaidQuoteToman is not > 0)
        {
            await ClearMatchingPaidTrialStateAsync(user, grant.Id);
            await botClient.SendMessage(message.Chat.Id, "پیش‌فاکتور تست معتبر نیست؛ دریافت تست را دوباره شروع کنید.",
                replyMarkup: mainReplyMarkup, cancellationToken: token);
            return;
        }
        var paidKey = $"colleague-paid-trial:{grant.Id}";
        var debitKey = paidKey + ":debit";
        var debit = await _credentialsDbContext.GetWalletOperationAsync(debitKey, token);
        if (debit == null)
        {
            if (!profile.IsColleague || profile.IsBlocked)
            {
                await ClearMatchingPaidTrialStateAsync(user, grant.Id);
                await botClient.SendMessage(message.Chat.Id, "خرید تست با تعرفه همکار فقط برای همکار فعال در دسترس است.",
                    replyMarkup: mainReplyMarkup, cancellationToken: token);
                return;
            }
            var livePrice = _purchaseService.ResolveColleagueTrialPriceToman(grant.ServiceKey,
                TrialTrafficBytes(grant.ServiceKey), TrialDays);
            if (livePrice != grant.PaidQuoteToman.Value || displayedPriceToman != grant.PaidQuoteToman.Value)
            {
                await OfferPaidColleagueTrialAsync(botClient, message.Chat.Id, profile, user, grant, token);
                return;
            }
            // Only unfunded admission is gated. A committed debit below is a settlement obligation even after closure.
            if (await RejectClosedServiceOperationAsync(
                    botClient, message.Chat.Id, FindService(grant.ServiceKey), ServiceSalesOperation.Sale, token))
                return;

            debit = await _credentialsDbContext.TryDebitWalletIfSufficientAsync(profile.TelegramUserId,
                livePrice, debitKey, grant.BotId, token);
            if (debit == null)
            {
                await botClient.SendMessage(message.Chat.Id,
                    $"موجودی کیف پول برای خرید این تست کافی نیست. مبلغ مورد نیاز: {livePrice.FormatCurrency()}\nپس از شارژ، همین خرید را دوباره تأیید کنید.",
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData(TelegramBotService.WalletChargeShortcutLabel, TelegramBotService.WalletChargeShortcutCallback) },
                        new[] { InlineKeyboardButton.WithCallbackData(ConfirmPaidTrialText, $"x3:ct:{grant.Id}:{livePrice.ToString(CultureInfo.InvariantCulture)}") }
                    }), cancellationToken: token);
                return;
            }
        }
        if (debit.TelegramUserId != profile.TelegramUserId || debit.BotId != grant.BotId || debit.AmountToman >= 0)
            throw new InvalidOperationException("Paid test debit receipt does not match its actor and originating bot.");
        await RecordColleagueTrialWalletReceiptAsync(debit, grant, false, CancellationToken.None);
        var price = checked(-debit.AmountToman);
        var winner = await _colleagueTrialQuotaStore.TryStartPaidCreationAsync(grant.Id, profile.TelegramUserId,
            grant.BotId, price, DateTime.UtcNow, CancellationToken.None);
        XuiV3AccountCreationResult creation = null;
        Exception failure = null;
        using var timing = XuiOperationTiming.Start();
        if (winner)
        {
            try
            {
                await botClient.SendMessage(message.Chat.Id, "پرداخت ثبت شد؛ در حال ساخت اکانت تست خریداری‌شده...",
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: token);
                creation = await _purchaseService.CreateTrialAccountAsync(profile, BuildConfiguredPanelServerInfo(),
                    grant.ServiceKey, 1, TrialTrafficBytes(grant.ServiceKey), TrialDays,
                    $"{grant.ServiceKey}-colleague-paid", paidKey, price, token);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Only this quiesced winner may reject absence/Reserved and permit compensation.
                grant = await _colleagueTrialQuotaStore.SettlePaidCreationAsync(grant.Id, profile.TelegramUserId,
                    grant.BotId, DateTime.UtcNow, CancellationToken.None);
            }
        }
        else if (grant.PaidCreationState != ColleagueTrialPaidCreationState.Rejected)
        {
            var operation = await _trialCreationOperations.FindAsync(paidKey, token);
            if (operation == null || operation.Outcome == XuiV3CreationOutcome.Reserved)
            {
                await SendUnresolvedTrialAsync(botClient, message.Chat.Id,
                    BuildPaidTrialInlineKeyboard(grant.Id, price), true, token);
                return;
            }
            if (operation.Outcome != XuiV3CreationOutcome.DefinitiveRejected)
                creation = await _purchaseService.CreateTrialAccountAsync(profile, BuildConfiguredPanelServerInfo(),
                    grant.ServiceKey, 1, TrialTrafficBytes(grant.ServiceKey), TrialDays,
                    $"{grant.ServiceKey}-colleague-paid", paidKey, price, token);
            grant = await _colleagueTrialQuotaStore.SettlePaidCreationAsync(grant.Id, profile.TelegramUserId,
                grant.BotId, DateTime.UtcNow, CancellationToken.None);
        }
        if (grant.PaidCreationState == ColleagueTrialPaidCreationState.Rejected)
        {
            // Refund uses the committed debit amount, never the current catalog or a mutable preview.
            var refund = await _credentialsDbContext.MutateWalletAsync(profile.TelegramUserId, price,
                paidKey + ":refund", CancellationToken.None, grant.BotId);
            if (refund == null) throw new InvalidOperationException("Paid test refund recipient is missing.");
            await RecordColleagueTrialWalletReceiptAsync(refund, grant, true, CancellationToken.None);
            await _colleagueTrialQuotaStore.MarkPaidRefundRecordedAsync(grant.Id, profile.TelegramUserId, grant.BotId,
                DateTime.UtcNow, CancellationToken.None);
            await ClearMatchingPaidTrialStateAsync(user, grant.Id);
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            await botClient.SendMessage(message.Chat.Id, "ساخت تست انجام نشد؛ مبلغ خرید به کیف پول شما برگشت داده شد.",
                replyMarkup: mainReplyMarkup, cancellationToken: token);
            return;
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (creation?.Success != true || grant.PaidCreationState != ColleagueTrialPaidCreationState.Applied)
        {
            await SendUnresolvedTrialAsync(botClient, message.Chat.Id,
                BuildPaidTrialInlineKeyboard(grant.Id, price), true, token);
            return;
        }
        // The paid creation is already applied; website availability must not hold up its account delivery.
        await QueueGozargahSyncBestEffortAsync("create",
            () => _gozargahSiteSyncService.QueueCreateAsync(ResolveGozargahSiteOwnerTelegramUserId(profile),
                profile.TelegramUserId, creation, paidKey, ResolveGozargahTenantBotId(),
                cancellationToken: token, deferSend: true));
        LogV3Purchase("خرید اکانت تست همکار", profile, price, debit.BeforeBalance, debit.AfterBalance,
            "کیف پول ربات", new[] { $"نام اکانت `{creation.Email}`", $"مدت `{TrialDays} روز`" }, timing.Snapshot(),
            isTrialAccount: true);
        await ClearMatchingPaidTrialStateAsync(user, grant.Id);
        await SendTrialAccountAsync(botClient, message.Chat.Id, creation, mainReplyMarkup, token);
    }

    /// <summary>Appends the auditable paid-test debit or exact compensation using its immutable global wallet receipt.</summary>
    /// <param name="receipt">Required committed credentials.db receipt; its signed amount and balances are authoritative.</param>
    /// <param name="grant">Required originating bot/actor receipt whose id links quota denial, paid quote and panel creation.</param>
    /// <param name="refund">True for a proven non-creation compensation; false for the confirmed test purchase debit.</param>
    /// <param name="token">Independent cancellation of the idempotent users.db ledger append.</param>
    /// <returns>A task completing when one ledger row exists; retries never duplicate a balance or ledger entry.</returns>
    /// <remarks>The wallet was already changed atomically with the receipt. This separate write never changes balances; the existing receipt reconciliation worker can repair its crash window.</remarks>
    /// <example><code>await RecordColleagueTrialWalletReceiptAsync(debit, grant, false, CancellationToken.None);</code></example>
    private async Task RecordColleagueTrialWalletReceiptAsync(WalletOperation receipt, ColleagueTrialGrant grant,
        bool refund, CancellationToken token)
    {
        await _walletLedgerService.RecordAsync(receipt.TelegramUserId,
            refund ? WalletLedgerDirections.Credit : WalletLedgerDirections.Debit,
            refund ? receipt.AmountToman : checked(-receipt.AmountToman), receipt.BeforeBalance, receipt.AfterBalance,
            refund ? WalletLedgerReasons.ColleagueTrialRefund : WalletLedgerReasons.AccountPurchase, provider: "wallet",
            referenceType: "colleague_trial", referenceId: grant.Id, botId: grant.BotId,
            idempotencyKey: receipt.OperationKey, cancellationToken: token);
    }

    /// <summary>Delivers verified three-day test credentials without creating or charging an account.</summary>
    /// <param name="botClient">Required transport for the caller's owned or tenant bot.</param>
    /// <param name="chatId">Original authorized Telegram chat receiving the account.</param>
    /// <param name="creation">Required verified successful creation; private subscription data is sent only to its actor.</param>
    /// <param name="replyMarkup">Nullable main keyboard following delivery.</param>
    /// <param name="token">Cancellation of Telegram delivery; never used to release/refund an already applied creation.</param>
    /// <returns>A task completing after text or QR/photo delivery.</returns>
    /// <remarks>Call only after durable quota/financial settlement. A notification failure leaves the account owned by the original Telegram colleague, who can retrieve it through the existing account-management flow and forward it.</remarks>
    /// <example><code>await SendTrialAccountAsync(client, message.Chat.Id, creation, keyboard, token);</code></example>
    private async Task SendTrialAccountAsync(ITelegramBotClient botClient, ChatId chatId,
        XuiV3AccountCreationResult creation, ReplyMarkup replyMarkup, CancellationToken token)
    {
        var text = "✅ اکانت تست شما ساخته شد.\n\n" + _purchaseService.BuildCreatedAccountText(creation);
        if (!string.IsNullOrWhiteSpace(creation.SubLink))
        {
            using var stream = new MemoryStream(QrCodeGen.GenerateQRCodeWithMargin(creation.SubLink, 200));
            await botClient.SendPhoto(chatId, InputFile.FromStream(stream, "trial-subscription-qr.png"),
                caption: text, parseMode: ParseMode.Html, replyMarkup: replyMarkup, cancellationToken: token);
        }
        else
            await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: replyMarkup,
                cancellationToken: token);
    }

    /// <summary>Reports an unresolved original test attempt without suggesting a new POST or refund.</summary>
    /// <param name="botClient">Required current owned-bot transport.</param>
    /// <param name="chatId">Original authenticated chat.</param>
    /// <param name="replyMarkup">Nullable main/paid confirmation keyboard for safe later read-back.</param>
    /// <param name="paid">True when the committed debit is held; false when the original daily slot is held.</param>
    /// <param name="token">Cancellation of this diagnostic notification only.</param>
    /// <returns>A task completing after the exact unresolved status is sent.</returns>
    /// <remarks>No account creation, balance change or quota release occurs. Operator review remains necessary if no authorized panel operation exists after a crashed executor claim.</remarks>
    /// <example><code>await SendUnresolvedTrialAsync(client, chatId, keyboard, true, token);</code></example>
    private static Task SendUnresolvedTrialAsync(ITelegramBotClient botClient, ChatId chatId,
        ReplyMarkup replyMarkup, bool paid, CancellationToken token) =>
        botClient.SendMessage(chatId,
            "نتیجه درخواست تست قبلی هنوز قطعی نیست؛ درخواست ساخت تکرار نمی‌شود. " +
            (paid ? "مبلغ پرداخت برای بررسی همان خرید نگه داشته شده است." : "سهمیه همان درخواست تا مشخص‌شدن نتیجه نگه داشته می‌شود.") +
            " در صورت ادامه مشکل با پشتیبانی تماس بگیرید.", replyMarkup: replyMarkup, cancellationToken: token);

    /// <summary>Returns the unchanged authoritative allowance for one supported national or normal three-day test.</summary>
    /// <param name="serviceKey">Required internal global test-service key, national or normal.</param>
    /// <returns>100 MiB for national or 1 GiB for normal, in panel bytes.</returns>
    /// <exception cref="InvalidOperationException">The service is not a supported test type.</exception>
    /// <remarks>Free and paid colleague tests share exactly this quota; regular purchase minimums are not changed.</remarks>
    /// <example><code>var bytes = TrialTrafficBytes(grant.ServiceKey);</code></example>
    private static long TrialTrafficBytes(string serviceKey) => serviceKey switch
    {
        "national" => NationalTrialBytes,
        "normal" => NormalTrialBytes,
        _ => throw new InvalidOperationException("Unsupported colleague test service.")
    };

    /// <summary>Builds paid-test inline approval/cancellation tied to the grant and actually displayed whole-toman amount.</summary>
    /// <param name="grantId">Required 32-character durable grant id; no account or provider secret.</param>
    /// <param name="priceToman">Positive exact displayed or committed whole-toman price.</param>
    /// <returns>Inline actions under Telegram's 64-byte callback limit, independent of transient conversation resets.</returns>
    /// <remarks>The handler reloads actor/bot ownership and price; callback data alone never authorizes spending. Every caption repeats its exact amount, including redisplay after a failed revised preview.</remarks>
    /// <example><code>var keyboard = BuildPaidTrialInlineKeyboard(grant.Id, price);</code></example>
    private static InlineKeyboardMarkup BuildPaidTrialInlineKeyboard(string grantId, long priceToman) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData($"{ConfirmPaidTrialText} ({priceToman.FormatCurrency()})", $"x3:ct:{grantId}:{priceToman.ToString(CultureInfo.InvariantCulture)}") },
        new[] { InlineKeyboardButton.WithCallbackData("انصراف", $"x3:ctc:{grantId}") }
    });

    /// <summary>Clears a completed/cancelled paid-test conversation only when it still references the exact grant.</summary>
    /// <param name="user">Required detached current actor state, possibly already reset or navigated elsewhere.</param>
    /// <param name="grantId">Required immutable paid-test receipt id being finalized.</param>
    /// <returns>A task completing after a matching clear, or an already completed task for unrelated navigation.</returns>
    /// <remarks>Old funded callbacks remain reachable without erasing a newer purchase, renewal or wallet conversation.</remarks>
    /// <example><code>await ClearMatchingPaidTrialStateAsync(user, grant.Id);</code></example>
    private Task ClearMatchingPaidTrialStateAsync(User user, string grantId) =>
        user.Flow == TrialFlowName && user.PurchaseSessionId == grantId
            ? _state.ClearUserStatus(user) : Task.CompletedTask;
}
