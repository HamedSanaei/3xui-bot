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
    private readonly UserDbContext _userDbcontext;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SalesAssistantService> _logger;

    /// <summary>
    /// creates the sales assistant service with the runtime Bot registry and tenant order database dependencies.
    /// </summary>
    /// <param name="UserDbContext">users.db context containing tenant orders and receipt rows.</param>
    /// <param name="BotRegistry">runtime registry used to resolve the configured assistant Bot.</param>
    /// <param name="BotClientProvider">Telegram client Provider used to Send assistant notifications.</param>
    /// <param name="ServiceProvider">service Provider used to resolve <see cref="TenantBotService" /> for final receipt Approval.</param>
    /// <param name="Logger">Logger used for failed assistant delivery or callback processing.</param>
    public SalesAssistantService(
        UserDbContext UserDbContext,
        BotRegistry BotRegistry,
        BotClientProvider BotClientProvider,
        IServiceProvider ServiceProvider,
        ILogger<SalesAssistantService> Logger)
    {
        _userDbcontext = UserDbContext;
        _botRegistry = BotRegistry;
        _botClientProvider = BotClientProvider;
        _serviceProvider = ServiceProvider;
        _logger = Logger;
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
        var assistant = GetAssistantBot();
        if (assistant == null || string.IsNullOrWhiteSpace(assistant.Token))
            return;

        var Text =
            "✅ <b>فروش موفق ربات همکار</b>\n\n" +
            $"🤖 ربات: <code>{Html(order.TenantBotUsername)}</code>\n" +
            $"🧾 سفارش: <code>{Html(order.OrderId)}</code>\n" +
            $"👤 مشتری: <code>{order.CustomerTelegramUserId}</code>\n" +
            $"💰 مبلغ فروش: <code>{Html(order.SalePriceToman.FormatCurrency())}</code>\n" +
            $"🏷 هزینه پایه همکار: <code>{Html(order.BaseCostToman.FormatCurrency())}</code>\n" +
            $"📈 تغییر کیف پول: <code>{Html(order.OwnerWalletDelta.FormatCurrency())}</code>\n" +
            $"💳 موجودی قبل: <code>{Html(beforeBalance.FormatCurrency())}</code>\n" +
            $"💳 موجودی بعد: <code>{Html(afterBalance.FormatCurrency())}</code>\n" +
            $"👤 اکانت: <code>{Html(order.CreatedAccountEmail)}</code>";

        try
        {
            await _botClientProvider.GetClient(assistant.Id).SendTextMessageAsync(
                order.OwnerTelegramUserId,
                Text,
                parseMode: ParseMode.Html,
                cancellationToken: CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant sale notification failed. OrderId={OrderId}", order.OrderId);
        }
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
    public async Task NOTIFYMANUALRECEIPTASYNC(TenantManualPaymentReceipt receipt, CancellationToken CancellationToken)
    {
        var assistant = GetAssistantBot();
        if (assistant == null || string.IsNullOrWhiteSpace(assistant.Token))
            return;

        var Text =
            "🧾 <b>رسید کارت‌به‌کارت جدید</b>\n\n" +
            $"🤖 ربات: <code>{Html(receipt.TenantBotUsername)}</code>\n" +
            $"🧾 سفارش: <code>{Html(receipt.OrderId)}</code>\n" +
            $"👤 مشتری: <code>{receipt.CustomerTelegramUserId}</code>\n" +
            $"💰 مبلغ: <code>{Html(receipt.AmountToman.FormatCurrency())}</code>\n\n" +
            "برای ساخت اکانت ابتدا تایید و سپس تایید نهایی را بزنید.";

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

            await _botClientProvider.GetClient(assistant.Id).SendPhotoAsync(
                chatId: receipt.OwnerTelegramUserId,
                photo: InputFile.FromStream(PHOTOSTREAM, $"tenant-receipt-{receipt.Id}.JPG"),
                caption: Text,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant receipt notification failed. RECEIPTID={RECEIPTID}", receipt.Id);
            await SENDMANUALRECEIPTFALLBACKTEXTASYNC(receipt, Text, keyboard, ex, CancellationToken);
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
    /// <param name="originalError">
    /// The Telegram or file-download exception that prevented photo delivery. Its message is included only as
    /// HTML-encoded diagnostic text and must not contain secrets or bot tokens.
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
    private async Task SENDMANUALRECEIPTFALLBACKTEXTASYNC(
        TenantManualPaymentReceipt receipt,
        string baseText,
        InlineKeyboardMarkup keyboard,
        Exception originalError,
        CancellationToken CancellationToken)
    {
        var assistant = GetAssistantBot();
        if (assistant == null || string.IsNullOrWhiteSpace(assistant.Token))
            return;

        var fallbackText =
            baseText +
            "\n\n⚠️ ارسال عکس رسید به دستیار فروش انجام نشد، اما تایید همین سفارش از همین پیام ممکن است." +
            $"\nReceiptId: <code>{receipt.Id}</code>" +
            $"\nOrderId: <code>{Html(receipt.OrderId)}</code>" +
            $"\nTenantBot: <code>{Html(receipt.TenantBotId)}</code>" +
            $"\nCustomerId: <code>{receipt.CustomerTelegramUserId}</code>" +
            $"\nخطای عکس: <code>{Html(originalError.Message)}</code>";

        try
        {
            await _botClientProvider.GetClient(assistant.Id).SendTextMessageAsync(
                chatId: receipt.OwnerTelegramUserId,
                text: fallbackText,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sales assistant receipt fallback text failed. RECEIPTID={RECEIPTID}", receipt.Id);
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
    private async Task HANDLECALLBACKASYNC(ITelegramBotClient botClient, CallbackQuery CallbackQuery, CancellationToken CancellationToken)
    {
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
            var receipt = await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == RECEIPTID, CancellationToken);
            if (receipt != null && receipt.Status == TenantManualPaymentReceiptStatuses.Pending)
            {
                receipt.Status = TenantManualPaymentReceiptStatuses.Rejected;
                receipt.ReviewerTelegramUserId = CallbackQuery.From.Id;
                receipt.RejectedAtUtc = DateTime.UtcNow;
                receipt.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken);
            }

            await SafeAnswerCallbackQueryAsync(botClient, CallbackQuery.Id, "رسید رد شد.", showAlert: true, cancellationToken: CancellationToken);
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
        var receipt = await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == receiptId, CancellationToken);
        if (receipt == null)
            return new ReceiptDetailsView("رسید پیدا نشد.", false, false);

        if (receipt.OwnerTelegramUserId != ownerTelegramUserId)
            return new ReceiptDetailsView("این رسید متعلق به ربات فروشگاهی شما نیست.", false, false);

        var order = await _userDbcontext.TenantBotOrders.FirstOrDefaultAsync(
            x => x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId,
            CancellationToken);

        var text =
            "🔎 <b>جزئیات رسید کارت‌به‌کارت</b>\n\n" +
            $"🤖 ربات: <code>{Html(receipt.TenantBotUsername)}</code>\n" +
            $"🧾 سفارش: <code>{Html(receipt.OrderId)}</code>\n" +
            $"👤 مشتری: <code>{receipt.CustomerTelegramUserId}</code>\n" +
            $"💰 مبلغ رسید: <code>{Html(receipt.AmountToman.FormatCurrency())}</code>\n" +
            $"📌 وضعیت رسید: <code>{Html(receipt.Status)}</code>\n";

        if (order == null)
        {
            text += "\nسفارش مرتبط با این رسید پیدا نشد.";
            return new ReceiptDetailsView(text, false, false);
        }

        text +=
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
    private async Task<bool> IsReceiptOrderFulfilledAsync(int receiptId, long ownerTelegramUserId, CancellationToken CancellationToken)
    {
        var receipt = await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == receiptId, CancellationToken);
        if (receipt == null || receipt.OwnerTelegramUserId != ownerTelegramUserId)
            return false;

        return await _userDbcontext.TenantBotOrders.AnyAsync(
            x => (x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId) && x.IsFulfilled,
            CancellationToken);
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
        var receipt = await _userDbcontext.TenantManualPaymentReceipts.FirstOrDefaultAsync(x => x.Id == receiptId, CancellationToken);
        if (receipt == null ||
            receipt.OwnerTelegramUserId != ownerTelegramUserId ||
            string.Equals(receipt.Status, TenantManualPaymentReceiptStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await _userDbcontext.TenantBotOrders.AnyAsync(
            x => (x.Id == receipt.TenantBotOrderId || x.OrderId == receipt.OrderId) && !x.IsFulfilled,
            CancellationToken);
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
        try
        {
            await botClient.AnswerCallbackQueryAsync(callbackQueryId, text, showAlert, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
                                            (ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
                                             ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase) ||
                                             ex.Message.Contains("response timeout expired", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(ex, "Ignoring stale sales-assistant callback answer. callbackQueryId={CallbackQueryId}", callbackQueryId);
        }
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
            _logger.LogWarning(ex, "Ignoring unchanged sales-assistant reply markup. messageId={MessageId}", messageId);
        }
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
