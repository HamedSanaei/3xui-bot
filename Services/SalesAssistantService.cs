using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Handles the Central sales assistant Bot used by tenant owners for sale notifications and manual receipt Approval.
/// </summary>
/// <remarks>
/// the assistant Bot is not customer-FACING. it SERVES tenant owners only and Receives events from tenant storefronts,
/// such as successful sales and card-to-card receipt PHOTOS that REQUIRE two-step Approval.
/// </remarks>
public class SalesAssistantService
{
    private const string CALLBACKPREFIX = "SA:";
    private readonly UserDbContextFactory _userDbContextFactory;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly XuiV3PurchaseService _purchaseService;
    private readonly ILogger<SalesAssistantService> _logger;

    /// <summary>
    /// Immutable budget for UX-only callback acknowledgement on the Sales Assistant bot. Production uses the
    /// shared two-second default; tests inject a millisecond budget so bounded-acknowledgement behaviour is
    /// proven without waiting the real production timeout.
    /// </summary>
    private readonly TelegramInteractionTimeouts _interactionTimeouts;

    /// <summary>
    /// creates the sales assistant service with the runtime Bot registry and tenant order database dependencies.
    /// </summary>
    /// <param name="UserDbContext">Factory for operation-owned users.db contexts. Detached input rows are reloaded before writes.</param>
    /// <param name="BotRegistry">runtime registry used to resolve the configured assistant Bot.</param>
    /// <param name="BotClientProvider">Telegram client Provider used to Send assistant notifications.</param>
    /// <param name="ServiceProvider">service Provider used to resolve <see cref="TenantBotService" /> for final receipt Approval.</param>
    /// <param name="PurchaseService">XUI v3 catalog service used to render the persisted purchase plan safely.</param>
    /// <param name="Logger">Logger used for failed assistant delivery or callback processing.</param>
    /// <param name="InteractionTimeouts">
    /// Optional immutable budgets for UX-only Telegram interactions. When null the production budgets are used, so
    /// callback acknowledgement is bounded at two seconds. Tests pass millisecond values. This value never affects
    /// receipt approval, tenant-owner authorization, or order fulfillment.
    /// </param>
    /// <remarks>Each operation owns its users.db context. Receipt/order checks preserve tenant-owner authorization; financial approval delegates to the durable tenant fulfillment boundary.</remarks>
    public SalesAssistantService(
        UserDbContextFactory UserDbContext,
        BotRegistry BotRegistry,
        BotClientProvider BotClientProvider,
        IServiceProvider ServiceProvider,
        XuiV3PurchaseService PurchaseService,
        ILogger<SalesAssistantService> Logger,
        TelegramInteractionTimeouts InteractionTimeouts = null)
    {
        _userDbContextFactory = UserDbContext;
        _botRegistry = BotRegistry;
        _botClientProvider = BotClientProvider;
        _serviceProvider = ServiceProvider;
        _purchaseService = PurchaseService;
        _logger = Logger;
        _interactionTimeouts = InteractionTimeouts ?? TelegramInteractionTimeouts.Production;
    }

    /// <summary>
    /// Gets whether the current async Bot context belongs to the configured sales assistant Bot.
    /// </summary>
    public bool IsAssistantBot => string.Equals(BotContextAccessor.CurrentBotType, BotInstanceTypes.SalesAssistant, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Handles updates received by the sales assistant Bot.
    /// </summary>
    /// <param name="botClient">Telegram client for the assistant Bot that received the update.</param>
    /// <param name="update">Raw Telegram update from the assistant receiver.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram and users.db operations.</param>
    /// <returns>true when the current Bot is the assistant Bot and the update was consumed; otherwise false.</returns>
    /// <remarks>
    /// callback updates can APPROVE, final-confirm, Cancel, or REJECT tenant manual receipts. Text messages
    /// Receive A short status response because the assistant is event-DRIVEN RATHER than Menu-DRIVEN.
    /// </remarks>
    public async Task<bool> TryHandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken CancellationToken)
    {
        if (!IsAssistantBot)
            return false;

        if (update.CallbackQuery is { } CallbackQuery)
        {
            await HANDLECALLBACKASYNC(botClient, CallbackQuery, CancellationToken);
            return true;
        }

        if (update.Message is { } Message)
        {
            await botClient.SendTextMessageAsync(
                Message.Chat.Id,
                "ربات دستیار فروش فعال است.\nاعلان فروش‌ها و رسیدهای کارت‌به‌کارت اینجا نمایش داده می‌شود.",
                cancellationToken: CancellationToken);
            return true;
        }

        return true;
    }

    /// <summary>
    /// NOTIFIES A tenant owner in the assistant Bot after A tenant sale has been fulfilled.
    /// </summary>
    /// <param name="order">fulfilled tenant order containing customer, account, sale, cost, and owner-balance Data.</param>
    /// <param name="beforeBalance">tenant owner wallet balance in toman before the sale ledger EFFECT.</param>
    /// <param name="afterBalance">tenant owner wallet balance in toman after the sale ledger EFFECT.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram delivery.</param>
    /// <remarks>
    /// delivery is best-effort. A failed assistant notification must not ROLL back account fulfillment or wallet ledger Writes.
    /// </remarks>
    public async Task NOTIFYTENANTSALEASYNC(TenantBotOrder order, long beforeBalance, long afterBalance, CancellationToken CancellationToken)
    {
        try { await SENDTENANTSALEASYNC(order, beforeBalance, afterBalance, CancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant sale notification failed. OrderId={OrderId}", order.OrderId);
        }
    }

    /// <summary>Sends one tenant-sale notification and returns a concrete Telegram message id for durable outbox acknowledgement.</summary>
    public async Task<int?> SENDTENANTSALEASYNC(TenantBotOrder order, long beforeBalance, long afterBalance, CancellationToken cancellationToken)
    {
        var assistant = GetAssistantBot();
        if (assistant == null || !assistant.Enabled || string.IsNullOrWhiteSpace(assistant.Token)) return null;
        var text =
            "✅ <b>فروش ربات همکار انجام شد</b>\n\n" +
            $"🤖 ربات: <code>{Html(order.TenantBotUsername)}</code>\n" +
            $"🧾 سفارش: <code>{Html(order.OrderId)}</code>\n" +
            $"👤 مشتری: <code>{order.CustomerTelegramUserId}</code>\n" +
            $"💰 مبلغ فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"📌 هزینه پایه همکار: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
            $"📈 تغییر موجودی: <code>{Html(order.OwnerWalletDelta.FormatCurrency())}</code>\n" +
            $"💳 موجودی قبل: <code>{Html(beforeBalance.FormatCurrency())}</code>\n" +
            $"💳 موجودی بعد: <code>{Html(afterBalance.FormatCurrency())}</code>\n" +
            $"📦 اکانت: <code>{Html(order.CreatedAccountEmail)}</code>";
        var sent = await _botClientProvider.GetClient(assistant.Id).SendTextMessageAsync(
            order.OwnerTelegramUserId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
        return sent.MessageId;
    }

    /// <summary>
    /// sends A tenant card-to-card receipt photo to the tenant owner through the assistant Bot.
    /// </summary>
    /// <param name="receipt">pending receipt row LINKED to one tenant order.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram delivery.</param>
    /// <remarks>
    /// the generated inline keyboard uses A two-step Approval Flow: APPROVE first, then final confirmation.
    /// final confirmation DELEGATES to <see cref="TenantBotService.APPROVEMANUALRECEIPTASYNC" />.
    /// the receipt photo is first DOWNLOADED through the tenant Bot that received it and then UPLOADED Again
    /// through the sales assistant Bot because Telegram file IDENTIFIERS are not safely REUSABLE across bots.
    /// </remarks>
    /// <returns>A task completing after the owner receipt notification attempt; the receipt approval state is unchanged.</returns>
    public async Task<int?> NOTIFYMANUALRECEIPTASYNC(TenantManualPaymentReceipt receipt, CancellationToken CancellationToken)
    {
        var _workflow = new UserWorkflowStore(_userDbContextFactory);
        var assistant = GetAssistantBot();
        if (assistant == null || !assistant.Enabled || string.IsNullOrWhiteSpace(assistant.Token))
            return null;

        TenantBotOrder order = null;
        try
        {
            order = await _workflow.ReadAsync(async db => await db.TenantBotOrders.FirstOrDefaultAsync(
                x => x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId,
                CancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant receipt order lookup failed. RECEIPTID={RECEIPTID}", receipt.Id);
        }
        var Text =
            "🧾 <b>رسید کارت‌به‌کارت جدید</b>\n\n" +
            $"🤖 ربات: <code>{Html(receipt.TenantBotUsername)}</code>\n" +
            $"🧾 سفارش: <code>{Html(receipt.OrderId)}</code>\n" +
            BuildCustomerSummary(order, receipt.CustomerTelegramUserId) +
            BuildPaymentPlanSummary(order) +
            $"💰 مبلغ: <code>{Html(receipt.AmountToman.FormatCurrency())}</code>\n\n" +
            "ابتدا تایید و سپس تایید نهایی را بزنید. اگر برای این سفارش اکانت موقت فعال شده باشد، با تایید نهایی همان\n" +
            "اکانت به بسته خریداری‌شده ارتقا پیدا می‌کند و اکانت جدیدی ساخته نمی‌شود.";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ تایید", CALLBACKPREFIX + $"APPROVE:{receipt.Id}"),
                InlineKeyboardButton.WithCallbackData("❌ رد", CALLBACKPREFIX + $"REJECT:{receipt.Id}")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔎 مشاهده جزئیات", CALLBACKPREFIX + $"DETAIL:{receipt.Id}")
            }
        });

        try
        {
            var TENANTCLIENT = _botClientProvider.GetClient(receipt.TenantBotId);
            var TELEGRAMFILE = await TENANTCLIENT.GetFileAsync(receipt.PhotoFileId, CancellationToken);
            await using var PHOTOSTREAM = new MemoryStream();
            // Telegram file ids received by A tenant Bot can fail when REUSED by the assistant Bot,
            // so the assistant always UPLOADS A FRESH Stream instead of forwarding the Original file Id.
            await TENANTCLIENT.DownloadFileAsync(TELEGRAMFILE.FilePath, PHOTOSTREAM, CancellationToken);
            PHOTOSTREAM.Position = 0;

            var sent = await _botClientProvider.GetClient(assistant.Id).SendPhotoAsync(
                chatId: receipt.OwnerTelegramUserId,
                photo: InputFile.FromStream(PHOTOSTREAM, $"tenant-receipt-{receipt.Id}.JPG"),
                caption: Text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
            return sent.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant receipt notification failed. RECEIPTID={RECEIPTID}", receipt.Id);
            return await SENDMANUALRECEIPTFALLBACKTEXTASYNC(receipt, Text, keyboard, CancellationToken);
        }
    }

    /// <summary>
    /// Sends a text-only Sales Assistant receipt notification when the receipt photo cannot be relayed.
    /// </summary>
    /// <param name="receipt">
    /// Tenant card-to-card receipt row that could not be delivered as a photo. The row supplies the owner chat,
    /// tenant bot id, order id, amount, and customer Telegram id used in the fallback message.
    /// </param>
    /// <param name="baseText">
    /// HTML-safe receipt text that would normally be used as the photo caption.
    /// </param>
    /// <param name="keyboard">
    /// Inline review keyboard containing approve, reject, and detail callbacks. It must be kept identical to the
    /// photo path so the owner can still complete the receipt flow from the fallback message.
    /// </param>
    /// <param name="CancellationToken">
    /// Cancellation token for the fallback Telegram send operation.
    /// </param>
    /// <returns>A task that completes after the fallback message is sent or skipped because the assistant bot is unavailable.</returns>
    /// <remarks>
    /// This method preserves the manual-payment approval path when a tenant bot file id cannot be downloaded or
    /// re-uploaded by the Sales Assistant bot. It is intentionally best-effort: failure to send the fallback is
    /// logged but never thrown back into tenant order processing.
    /// </remarks>
    private async Task<int?> SENDMANUALRECEIPTFALLBACKTEXTASYNC(
        TenantManualPaymentReceipt receipt,
        string baseText,
        InlineKeyboardMarkup keyboard,
        CancellationToken CancellationToken)
    {
        var assistant = GetAssistantBot();
        if (assistant == null || !assistant.Enabled || string.IsNullOrWhiteSpace(assistant.Token))
            return null;

        var fallbackText =
            baseText +
            "\n\n⚠️ ارسال عکس رسید به دستیار فروش انجام نشد، اما تایید همین سفارش از همین پیام ممکن است." +
            $"\nReceiptId: <code>{receipt.Id}</code>" +
            $"\nOrderId: <code>{Html(receipt.OrderId)}</code>" +
            $"\nTenantBot: <code>{Html(receipt.TenantBotId)}</code>" +
            $"\nCustomerId: <code>{receipt.CustomerTelegramUserId}</code>" +
            "\nخطای عکس: <code>ارسال تصویر ناموفق بود؛ جزئیات در لاگ ثبت شده است.</code>";

        try
        {
            var sent = await _botClientProvider.GetClient(assistant.Id).SendTextMessageAsync(
                chatId: receipt.OwnerTelegramUserId,
                text: fallbackText,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
            return sent.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant receipt fallback text failed. RECEIPTID={RECEIPTID}", receipt.Id);
            return null;
        }
    }

    /// <summary>
    /// routes assistant inline callbacks for receipt Approval, Cancellation, final confirmation, or rejection.
    /// </summary>
    /// <param name="botClient">assistant Bot client used to answer and edit the callback Message.</param>
    /// <param name="CallbackQuery">Incoming assistant callback Query.</param>
    /// <param name="CancellationToken">Cancellation Token for Telegram and users.db operations.</param>
    /// <remarks>
    /// final confirmation is idempotent through the tenant order fulfillment service. REPEATED CLICKS do not
    /// Create another account or another ledger entry when the order is already fulfilled.
    /// </remarks>
    /// <returns>A task completing after the authorized receipt action and its Telegram response.</returns>
    private async Task HANDLECALLBACKASYNC(ITelegramBotClient botClient, CallbackQuery CallbackQuery, CancellationToken CancellationToken)
    {
        var _workflow = new UserWorkflowStore(_userDbContextFactory);
        var Data = CallbackQuery.Data ?? string.Empty;
        if (!Data.StartsWith(CALLBACKPREFIX, StringComparison.Ordinal))
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        var action = Data[CALLBACKPREFIX.Length..];
        var parts = action.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var RECEIPTID))
        {
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "درخواست نامعتبر است.", showAlert: true, cancellationToken: CancellationToken);
            return;
        }

        if (parts[0] == "DETAIL" || parts[0] == "DETAILF")
        {
            var details = await BuildReceiptDetailsTextAsync(RECEIPTID, CallbackQuery.From.Id, CancellationToken);
            var keyboard = parts[0] == "DETAILF"
                ? BuildReceiptFinalConfirmationKeyboard(RECEIPTID)
                : BuildReceiptPostReviewKeyboard(RECEIPTID, details.CanResend, details.CanApprove);
            await SafeEditMessageCaptionAsync(
                botClient,
                CallbackQuery.Message.Chat.Id,
                CallbackQuery.Message.MessageId,
                details.Text,
                keyboard,
                CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, cancellationToken: CancellationToken);
            return;
        }

        if (parts[0] == "RESEND")
        {
            var TENANTSERVICE = _serviceProvider.GetRequiredService<TenantBotService>();
            var result = await TENANTSERVICE.RESENDMANUALRECEIPTACCOUNTASYNC(RECEIPTID, CallbackQuery.From.Id, CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, result, showAlert: true, cancellationToken: CancellationToken);
            return;
        }

        if (parts[0] == "APPROVE")
        {
            await SafeEditMessageReplyMarkupAsync(
                botClient,
                CallbackQuery.Message.Chat.Id,
                CallbackQuery.Message.MessageId,
                BuildReceiptFinalConfirmationKeyboard(RECEIPTID),
                CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "برای انجام قطعی، تایید نهایی را بزنید.", cancellationToken: CancellationToken);
            return;
        }

        if (parts[0] == "Cancel")
        {
            await SafeEditMessageReplyMarkupAsync(
                botClient,
                CallbackQuery.Message.Chat.Id,
                CallbackQuery.Message.MessageId,
                BuildReceiptPostReviewKeyboard(RECEIPTID, canResend: false),
                CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "لغو شد.", cancellationToken: CancellationToken);
            return;
        }

        if (parts[0] == "final")
        {
            var TENANTSERVICE = _serviceProvider.GetRequiredService<TenantBotService>();
            var result = await TENANTSERVICE.APPROVEMANUALRECEIPTASYNC(RECEIPTID, CallbackQuery.From.Id, CancellationToken);
            var canResend = await IsReceiptOrderFulfilledAsync(RECEIPTID, CallbackQuery.From.Id, CancellationToken);
            var canApprove = !canResend && await CanRetryReceiptApprovalAsync(RECEIPTID, CallbackQuery.From.Id, CancellationToken);
            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, result, showAlert: true, cancellationToken: CancellationToken);
            await SafeEditMessageReplyMarkupAsync(
                botClient,
                CallbackQuery.Message.Chat.Id,
                CallbackQuery.Message.MessageId,
                BuildReceiptPostReviewKeyboard(RECEIPTID, canResend, canApprove),
                CancellationToken);
            return;
        }

        if (parts[0] == "REJECT")
        {
            // Delegated so a rejected receipt also disables any provisional courtesy client through the order's own
            // durable revoke saga. Owner authorization is rechecked inside the tenant service, so an assistant-side
            // callback cannot bypass it. Nothing here settles money in either direction.
            var TENANTSERVICE = _serviceProvider.GetRequiredService<TenantBotService>();
            var REJECTRESULT = await TENANTSERVICE.REJECTMANUALRECEIPTASYNC(RECEIPTID, CallbackQuery.From.Id, CancellationToken);

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, REJECTRESULT, showAlert: true, cancellationToken: CancellationToken);
            await SafeEditMessageReplyMarkupAsync(
                botClient,
                CallbackQuery.Message.Chat.Id,
                CallbackQuery.Message.MessageId,
                BuildReceiptPostReviewKeyboard(RECEIPTID, canResend: false),
                CancellationToken);
        }
    }

    /// <summary>
    /// Builds the inline keyboard that remains under a receipt photo after review actions.
    /// </summary>
    /// <param name="receiptId">Internal users.db receipt id embedded in callback data.</param>
    /// <param name="canResend">Whether the linked order is fulfilled and account details can be resent.</param>
    /// <param name="canApprove">
    /// Whether the receipt is still pending and the owner should retain the approve/reject controls after
    /// opening the detail view.
    /// </param>
    /// <returns>Inline keyboard for review, detail lookup, and optional account resend.</returns>
    /// <remarks>
    /// Pending receipts must keep approval controls even after the owner opens details. Otherwise the owner
    /// loses the ability to finish a valid receipt from the same assistant message.
    /// </remarks>
    private static InlineKeyboardMarkup BuildReceiptPostReviewKeyboard(int receiptId, bool canResend, bool canApprove = false)
    {
        var rows = new List<InlineKeyboardButton[]>();

        if (canApprove)
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ تایید", CALLBACKPREFIX + $"APPROVE:{receiptId}"),
                InlineKeyboardButton.WithCallbackData("❌ رد", CALLBACKPREFIX + $"REJECT:{receiptId}")
            });
        }

        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 مشاهده جزئیات", CALLBACKPREFIX + $"DETAIL:{receiptId}") });

        if (canResend)
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("📤 ارسال مجدد مشخصات", CALLBACKPREFIX + $"RESEND:{receiptId}") });

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>
    /// Builds the second-step confirmation keyboard after an owner clicks approve for a receipt.
    /// </summary>
    /// <param name="receiptId">Internal users.db receipt id awaiting final confirmation.</param>
    /// <returns>Inline keyboard with final confirmation, cancellation, and a context-preserving detail button.</returns>
    /// <remarks>
    /// The detail button uses <c>DETAILF</c> so viewing details during the final-confirmation step does not
    /// replace the keyboard with the first-step approve/reject controls.
    /// </remarks>
    private static InlineKeyboardMarkup BuildReceiptFinalConfirmationKeyboard(int receiptId)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ تایید نهایی", CALLBACKPREFIX + $"final:{receiptId}") },
            new[] { InlineKeyboardButton.WithCallbackData("↩‌ انصراف", CALLBACKPREFIX + $"Cancel:{receiptId}") },
            new[] { InlineKeyboardButton.WithCallbackData("🔎 مشاهده جزئیات", CALLBACKPREFIX + $"DETAILF:{receiptId}") }
        });
    }

    /// <summary>
    /// Builds the receipt/order/account detail caption for the Sales Assistant receipt photo.
    /// </summary>
    /// <param name="receiptId">Internal receipt id selected by the assistant callback.</param>
    /// <param name="ownerTelegramUserId">Telegram user id of the assistant user requesting the details.</param>
    /// <param name="CancellationToken">Cancellation token for users.db reads.</param>
    /// <returns>
    /// Detail text safe for Telegram HTML captions and a flag indicating whether account details can be resent.
    /// </returns>
    /// <remarks>
    /// The owner id is used as an access guard. A tenant owner can only inspect receipt rows that belong to
    /// their own storefront.
    /// </remarks>
    private async Task<ReceiptDetailsView> BuildReceiptDetailsTextAsync(
        int receiptId,
        long ownerTelegramUserId,
        CancellationToken CancellationToken)
    {
        var _workflow = new UserWorkflowStore(_userDbContextFactory);
        var receipt = await _workflow.ReadAsync(async db => await db.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == receiptId, CancellationToken));
        if (receipt == null)
            return new ReceiptDetailsView("رسید پیدا نشد.", false, false);

        if (receipt.OwnerTelegramUserId != ownerTelegramUserId)
            return new ReceiptDetailsView("این رسید متعلق به ربات فروشگاهی شما نیست.", false, false);

        var order = await _workflow.ReadAsync(async db => await db.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId,
            CancellationToken));

        var text =
            "🔎 <b>جزئیات رسید کارت‌به‌کارت</b>\n\n" +
            $"🤖 ربات: <code>{Html(receipt.TenantBotUsername)}</code>\n" +
            $"🧾 سفارش: <code>{Html(receipt.OrderId)}</code>\n" +
            BuildCustomerSummary(order, receipt.CustomerTelegramUserId) +
            $"💰 مبلغ رسید: <code>{Html(receipt.AmountToman.FormatCurrency())}</code>\n" +
            $"📌 وضعیت رسید: <code>{Html(receipt.Status)}</code>\n";

        if (order == null)
        {
            text += "\nسفارش مرتبط با این رسید پیدا نشد.";
            return new ReceiptDetailsView(text, false, false);
        }

        text +=
            BuildPaymentPlanSummary(order) +
            $"📌 وضعیت سفارش: <code>{Html(order.PaymentStatus)}</code>\n" +
            $"🏷 مبلغ فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"💳 هزینه پایه همکار: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
            $"🔚 موجودی بعد: <code>{Html(order.OwnerBalanceAfter?.FormatCurrency() ?? "ثبت نشده")}</code>\n" +
            $"✅ ساخته شده: <code>{(order.IsFulfilled ? "بله" : "خیر")}</code>\n";

        if (order.IsFulfilled)
        {
            text +=
                $"👤 اکانت: <code>{Html(order.CreatedAccountEmail)}</code>\n" +
                $"🔗 سابلینک: <code>{Html(order.CreatedSubLink)}</code>\n";
        }

        var canApprove = !order.IsFulfilled &&
                         (string.Equals(receipt.Status, TenantManualPaymentReceiptStatuses.Pending, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(receipt.Status, TenantManualPaymentReceiptStatuses.Approved, StringComparison.OrdinalIgnoreCase));
        return new ReceiptDetailsView(text, order.IsFulfilled, canApprove);
    }

    /// <summary>
    /// Checks whether the receipt belongs to the requesting owner and has a fulfilled order.
    /// </summary>
    /// <param name="receiptId">Internal users.db receipt id.</param>
    /// <param name="ownerTelegramUserId">Telegram user id of the assistant user.</param>
    /// <param name="CancellationToken">Cancellation token for users.db reads.</param>
    /// <returns><c>true</c> when the linked order is fulfilled and details can be resent.</returns>
    /// <remarks>Each operation owns its users.db context. Receipt/order checks preserve tenant-owner authorization; financial approval delegates to the durable tenant fulfillment boundary.</remarks>
    private async Task<bool> IsReceiptOrderFulfilledAsync(int receiptId, long ownerTelegramUserId, CancellationToken CancellationToken)
    {
        var _workflow = new UserWorkflowStore(_userDbContextFactory);
        var receipt = await _workflow.ReadAsync(async db => await db.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == receiptId, CancellationToken));
        if (receipt == null || receipt.OwnerTelegramUserId != ownerTelegramUserId)
            return false;

        return await _workflow.ReadAsync(async db => await db.TenantBotOrders.AnyAsync(
            x => (x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId) && x.IsFulfilled,
            CancellationToken));
    }

    /// <summary>
    /// Checks whether a receipt can show the approve button again after a failed final-confirmation attempt.
    /// </summary>
    /// <param name="receiptId">Internal users.db receipt id embedded in the assistant callback.</param>
    /// <param name="ownerTelegramUserId">Telegram user id of the tenant owner using the assistant bot.</param>
    /// <param name="CancellationToken">Cancellation token for users.db reads.</param>
    /// <returns>
    /// <c>true</c> when the receipt belongs to the owner, is not rejected, and its linked order is still
    /// unfulfilled; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// XUI account creation can time out after a manual receipt is accepted. The retry button must remain available
    /// so the owner can run the same idempotent fulfillment path again without asking the customer for another photo.
    /// </remarks>
    private async Task<bool> CanRetryReceiptApprovalAsync(int receiptId, long ownerTelegramUserId, CancellationToken CancellationToken)
    {
        var _workflow = new UserWorkflowStore(_userDbContextFactory);
        var receipt = await _workflow.ReadAsync(async db => await db.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == receiptId, CancellationToken));
        if (receipt == null ||
            receipt.OwnerTelegramUserId != ownerTelegramUserId ||
            string.Equals(receipt.Status, TenantManualPaymentReceiptStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await _workflow.ReadAsync(async db => await db.TenantBotOrders.AnyAsync(
            x => (x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId) && !x.IsFulfilled,
            CancellationToken));
    }

    /// <summary>
    /// Safely answers a Telegram callback without letting stale callback ids stop the assistant receiver.
    /// </summary>
    /// <param name="botClient">Assistant bot client that received the callback.</param>
    /// <param name="callbackQueryId">Opaque Telegram callback query id.</param>
    /// <param name="text">Optional toast or alert text.</param>
    /// <param name="showAlert">Whether Telegram should show an alert dialog.</param>
    /// <param name="cancellationToken">Cancellation token for the Telegram API call.</param>
    private async Task SafeAnswerCallbackQueryAsync(
        ITelegramBotClient botClient,
        string callbackQueryId,
        string text = null,
        bool? showAlert = null,
        CancellationToken cancellationToken = default)
    {
        await TelegramCallbackAnswerPolicy.TryAnswerAsync(
            botClient, callbackQueryId, text, showAlert,
            cancellationToken: cancellationToken, logger: _logger,
            botId: BotContextAccessor.CurrentBotId, timeout: _interactionTimeouts.CallbackAnswer);
    }

    /// <summary>
    /// Safely edits only the inline keyboard under a Sales Assistant receipt message.
    /// </summary>
    /// <param name="botClient">Assistant bot client that owns the message.</param>
    /// <param name="chatId">Telegram chat id containing the receipt message.</param>
    /// <param name="messageId">Telegram message id of the receipt photo.</param>
    /// <param name="replyMarkup">Replacement inline keyboard.</param>
    /// <param name="CancellationToken">Cancellation token for the Telegram API call.</param>
    private async Task SafeEditMessageReplyMarkupAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken CancellationToken)
    {
        try
        {
            await botClient.EditMessageReplyMarkupAsync(chatId, messageId, replyMarkup, CancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
                                            ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Telegram confirms the requested markup is already present; this is a successful no-op.
        }
    }

    /// <summary>
    /// Builds the HTML-safe customer identity block used by receipt captions and detail views.
    /// </summary>
    /// <param name="order">Tenant order containing the cached Telegram profile fields, when available.</param>
    /// <param name="fallbackTelegramUserId">Customer id copied to the receipt row.</param>
    /// <returns>Customer name link, username, and numeric id lines.</returns>
    /// <remarks>
    /// Telegram deep links are best-effort: privacy settings may prevent opening the profile or sending a message.
    /// The numeric id is therefore always rendered independently of the link.
    /// </remarks>
    private static string BuildCustomerSummary(TenantBotOrder order, long fallbackTelegramUserId)
    {
        var telegramUserId = order?.CustomerTelegramUserId > 0
            ? order.CustomerTelegramUserId
            : fallbackTelegramUserId;
        var firstName = order?.CustomerFirstName?.Trim();
        var lastName = order?.CustomerLastName?.Trim();
        var fullName = string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(fullName))
            fullName = NormalizeUsername(order?.CustomerUsername) ?? telegramUserId.ToString();

        var linkedName = telegramUserId > 0
            ? $"<a href=\"tg://user?id={telegramUserId}\">{Html(fullName)}</a>"
            : $"<code>{Html(fullName)}</code>";
        var username = NormalizeUsername(order?.CustomerUsername);

        return $"👤 مشتری: {linkedName}\n" +
               $"🔹 یوزرنیم: <code>{Html(username ?? "ندارد")}</code>\n" +
               $"🆔 آیدی عددی: <code>{telegramUserId}</code>\n";
    }

    /// <summary>
    /// Builds the provider and persisted plan block for a tenant order.
    /// </summary>
    /// <param name="order">Tenant order whose provider and catalog keys are displayed.</param>
    /// <returns>HTML-safe payment-provider and plan lines, or an empty string when no order is available.</returns>
    /// <remarks>
    /// Catalog loading is presentation-only and failure-safe. Historical orders remain reviewable when a service or
    /// duration was later disabled or removed from the live catalog.
    /// </remarks>
    private string BuildPaymentPlanSummary(TenantBotOrder order)
    {
        if (order == null)
            return string.Empty;

        var provider = FormatPaymentProvider(order.PaymentProvider);
        var plan = ResolvePlanLabel(order);
        return $"💳 درگاه: <code>{Html(provider)}</code>\n" +
               $"📦 پلن: <code>{Html(plan)}</code>\n";
    }

    /// <summary>
    /// Maps persisted payment-provider keys to stable Persian labels for the sales assistant.
    /// </summary>
    /// <param name="provider">Persisted provider key.</param>
    /// <returns>A non-sensitive user-facing provider label.</returns>
    private static string FormatPaymentProvider(string provider)
    {
        return provider?.Trim().ToLowerInvariant() switch
        {
            "hooshpay" => "هوش‌پی",
            "nowpayments" => "ارز دیجیتال",
            "uniquepay" => "یونیک‌پی",
            "tetraminator" => "تترامیناتور",
            "tenant_card" => "کارت‌به‌کارت",
            _ => "سایر/نامشخص"
        };
    }

    /// <summary>
    /// Resolves a readable service, duration, traffic, or unlimited-plan label from the live catalog and order keys.
    /// </summary>
    /// <param name="order">Persisted tenant order containing stable service and plan keys.</param>
    /// <returns>A safe plan label with persisted-key fallback when the catalog is unavailable.</returns>
    private string ResolvePlanLabel(TenantBotOrder order)
    {
        var serviceKey = order.ServiceKey?.Trim();
        var serviceLabel = serviceKey;
        try
        {
            var service = _purchaseService.GetEnabledServices().FirstOrDefault(item =>
                string.Equals(item.Key, serviceKey, StringComparison.OrdinalIgnoreCase));
            if (service != null)
            {
                serviceLabel = string.IsNullOrWhiteSpace(service.DisplayName) ? service.Key : service.DisplayName;
                if (service.IsUnlimited)
                {
                    var unlimited = XuiV3PurchaseService.GetUnlimitedPlansForTenant(service)
                        .FirstOrDefault(item => string.Equals(item.Key, order.UnlimitedPlanKey, StringComparison.OrdinalIgnoreCase));
                    var unlimitedLabel = unlimited == null
                        ? order.UnlimitedPlanKey
                        : (string.IsNullOrWhiteSpace(unlimited.DisplayName) ? unlimited.Key : unlimited.DisplayName);
                    return string.IsNullOrWhiteSpace(unlimitedLabel)
                        ? serviceLabel ?? "نامشخص"
                        : $"{serviceLabel} — {unlimitedLabel}";
                }

                var duration = XuiV3PurchaseService.GetEnabledDurationOptions(service)
                    .FirstOrDefault(item => string.Equals(item.Key, order.DurationKey, StringComparison.OrdinalIgnoreCase));
                var durationLabel = duration == null
                    ? XuiV3PurchaseService.FormatDurationSelectionKey(order.DurationKey)
                    : (string.IsNullOrWhiteSpace(duration.DisplayName) ? duration.Key : duration.DisplayName);
                var trafficLabel = order.TrafficGb.GetValueOrDefault() > 0
                    ? $"{order.TrafficGb.Value} GB"
                    : "حجم نامشخص";
                return string.Join(" — ", new[] { serviceLabel, durationLabel, trafficLabel }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "sales assistant plan catalog lookup failed. OrderId={OrderId}", order.OrderId);
        }

        var fallback = new[]
        {
            serviceLabel,
            string.IsNullOrWhiteSpace(order.UnlimitedPlanKey)
                ? XuiV3PurchaseService.FormatDurationSelectionKey(order.DurationKey)
                : order.UnlimitedPlanKey,
            order.TrafficGb.GetValueOrDefault() > 0 ? $"{order.TrafficGb.Value} GB" : null
        };
        return string.Join(" — ", fallback.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } value
            ? value
            : "نامشخص";
    }

    /// <summary>
    /// Normalizes one stored Telegram username for display without exposing malformed whitespace.
    /// </summary>
    /// <param name="username">Stored username with or without an at-sign.</param>
    /// <returns>Username including one leading at-sign, or <c>null</c> when unavailable.</returns>
    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var normalized = username.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(normalized) ? null : $"@{normalized}";
    }

    /// <summary>
    /// Safely edits the caption of a Sales Assistant receipt photo to show receipt details.
    /// </summary>
    /// <param name="botClient">Assistant bot client that owns the photo message.</param>
    /// <param name="chatId">Telegram chat id containing the photo.</param>
    /// <param name="messageId">Telegram message id of the receipt photo.</param>
    /// <param name="caption">New HTML caption.</param>
    /// <param name="replyMarkup">Inline keyboard to keep under the receipt photo.</param>
    /// <param name="CancellationToken">Cancellation token for the Telegram API call.</param>
    private async Task SafeEditMessageCaptionAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        string caption,
        InlineKeyboardMarkup replyMarkup,
        CancellationToken CancellationToken)
    {
        try
        {
            await botClient.EditMessageCaptionAsync(
                chatId: chatId,
                messageId: messageId,
                caption: caption,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: CancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
                                            ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Ignoring unchanged sales-assistant receipt caption. messageId={MessageId}", messageId);
        }
    }

    /// <summary>
    /// Resolves the configured sales assistant Bot from the runtime registry.
    /// </summary>
    /// <returns>the enabled or configured assistant Bot, or null when no assistant Token has been configured.</returns>
    private BotInstanceConfig GetAssistantBot()
    {
        return _botRegistry.Bots.FirstOrDefault(x => string.Equals(x.Type, BotInstanceTypes.SalesAssistant, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Html-Encodes free Text before inserting it into Telegram Html messages.
    /// </summary>
    /// <param name="value">Raw Text that may contain Telegram Html-sensitive characters.</param>
    /// <returns>Html-encoded Text; null becomes an empty string.</returns>
    private static string Html(string value)
    {
        return System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
    }

    /// <summary>
    /// Small return model for Sales Assistant receipt detail rendering.
    /// </summary>
    private sealed class ReceiptDetailsView
    {
        /// <summary>
        /// Creates a detail view result.
        /// </summary>
        /// <param name="text">HTML caption text to show under the receipt photo.</param>
        /// <param name="canResend">Whether the linked order is fulfilled and resend controls should be shown.</param>
        /// <param name="canApprove">Whether approve/reject controls should remain visible under the receipt.</param>
        public ReceiptDetailsView(string text, bool canResend, bool canApprove)
        {
            Text = text;
            CanResend = canResend;
            CanApprove = canApprove;
        }

        /// <summary>
        /// HTML caption text to show under the receipt photo.
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// Whether the linked order is fulfilled and resend controls should be shown.
        /// </summary>
        public bool CanResend { get; }

        /// <summary>
        /// Whether the receipt is still pending and can be approved or rejected by the owner.
        /// </summary>
        public bool CanApprove { get; }
    }
}
