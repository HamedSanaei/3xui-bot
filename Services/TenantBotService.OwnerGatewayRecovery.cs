using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Provides selected-store owner prompts for authoritative third-party gateway payment recovery.</summary>
/// <remarks>
/// This partial contains only conversation orchestration. Payment proof, reciprocal provider links, tenant isolation,
/// financial idempotency and provisioning retry authorization belong to the shared financial backend.
/// Card-to-card receipt approval remains a separate owner flow.
/// </remarks>
public partial class TenantBotService
{
    /// <summary>Persisted owner input step awaiting the selected store's exact public gateway-order identifier.</summary>
    /// <remarks>
    /// Stored with OWNERFLOW and OwnerStoreId in the active owned-bot/Telegram-user conversation, not globally by owner.
    /// It represents a request for a fresh provider inquiry, never permission to provisionally mark an order paid.
    /// </remarks>
    private const string STEPOWNERGATEWAYORDERID = "gateway-order-id";

    /// <summary>Acknowledges an authorized gateway-confirm callback and persists a retryable exact-order prompt.</summary>
    /// <param name="botClient">Required owner-panel-decorated client of the owned bot that received the callback.</param>
    /// <param name="callbackQuery">
    /// Required authenticated owner's Telegram callback, already decoded and checked against the selected tenant store,
    /// its current revision and expiry by TryHandleOwnerCallbackAsync. Its sender id is not a customer id.
    /// </param>
    /// <param name="cancellationToken">Token for the incoming update and Telegram prompt delivery.</param>
    /// <returns>A task completing after callback acknowledgement, durable bot/user/store state replacement and prompt delivery.</returns>
    /// <remarks>
    /// The acknowledgement precedes persistence and prompt delivery; no payment inquiry runs in the callback path.
    /// Resetting this bot/user's temporary fields atomically installs the exact selected OwnerStoreId so a new service
    /// scope or restart can resume without inferring a first store. Cancel, store-list navigation or store switching
    /// clear this pending step through the existing owner router. No sales/gateway toggle blocks recovery of issued invoices.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancels the update; cancellation is never converted into approval.</exception>
    /// <exception cref="ApiRequestException">Telegram rejects prompt delivery after the input state has been saved.</exception>
    /// <example><code>await StartOwnerGatewayOrderConfirmationAsync(ownerPanelClient, authorizedCallback, cancellationToken);</code></example>
    private async Task StartOwnerGatewayOrderConfirmationAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        await SafeAnswerCallbackQueryAsync(botClient, callbackQuery.Id, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // The router validated this exact store and sender. Persist the store explicitly, never select a sibling by owner id.
        await _state.ResetUserStatus(new User
        {
            Id = callbackQuery.From.Id,
            Flow = OWNERFLOW,
            LastStep = STEPOWNERGATEWAYORDERID,
            OwnerStoreId = _selectedOwnerStore.Id
        });
        cancellationToken.ThrowIfCancellationRequested();

        await botClient.SendMessage(
            chatId: callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id,
            text: "OrderId سفارش پرداخت درگاه را دقیقاً از فهرست «سفارش‌ها»ی همین فروشگاه کپی و ارسال کنید.\n" +
                  "مثال: <code>TENANTBOT-...</code>\n\n" +
                  "استعلام رسمی تازه از درگاه انجام می‌شود و سفارش فقط پس از تأیید پرداخت کامل توسط درگاه تکمیل می‌شود. " +
                  "این مسیر تأیید موقت یا ثبت پرداخت بدون اثبات درگاه نیست و برای کارت‌به‌کارت کاربرد ندارد.",
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("بازگشت به پنل") }
            })
            {
                ResizeKeyboard = true
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Passes an owner's exact selected-store OrderId to official payment recovery and renders its honest result.</summary>
    /// <param name="botClient">Required owner-panel-decorated client of the owned bot that received the message.</param>
    /// <param name="message">
    /// Required Telegram message from the authenticated owner. Text must contain the public TenantBotOrder.OrderId,
    /// not an integer database id, customer id or provider tracking code; surrounding whitespace is trimmed only.
    /// The persisted public identifier is required and cannot exceed the tenant-order column's 140-character limit.
    /// </param>
    /// <param name="owner">Required colleague profile matched to message.From.Id by the owner router; not supplied by message text.</param>
    /// <param name="cancellationToken">Incoming update token propagated to provider, financial and Telegram operations.</param>
    /// <returns>A task completing after a retryable format response or the backend result and refreshed exact-store owner panel are sent.</returns>
    /// <remarks>
    /// The caller reloads the exact persisted OwnerStoreId under this sender before entering this method. Blank or oversized
    /// input retains the pending bot/user/store state and makes no financial call. Other exact identifiers delegate to
    /// ConfirmTenantGatewayOrderByOwnerAsync using message.From.Id; that backend reauthenticates the store, order and all
    /// reciprocal payment links before official inquiry or settlement. It alone determines full payment and idempotent
    /// purchase/renewal delivery; unpaid or uncertain outcomes are never rewritten as approval here.
    /// The backend returns non-null sanitized Telegram HTML. After a returned outcome, only this active bot/user's temporary
    /// input is cleared and the same selected owner panel is freshly loaded. Exceptions and caller cancellation propagate;
    /// a transport failure cannot undo financial settlement or create another account on a later recovery request.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The owner update is canceled during payment recovery or Telegram work.</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">The selected store changed before the refreshed panel could be rendered.</exception>
    /// <exception cref="ApiRequestException">Telegram rejects the result or refreshed panel; financial completion is not rolled back.</exception>
    /// <example><code>await HandleOwnerGatewayOrderIdAsync(ownerPanelClient, update.Message, authenticatedOwner, cancellationToken);</code></example>
    private async Task HandleOwnerGatewayOrderIdAsync(
        ITelegramBotClient botClient,
        Message message,
        CredUser owner,
        CancellationToken cancellationToken)
    {
        var orderId = message.Text?.Trim();
        if (string.IsNullOrEmpty(orderId) || orderId.Length > 140)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "OrderId معتبر سفارش پرداخت درگاه را بدون متن اضافی از فهرست سفارش‌های همین فروشگاه کپی و دوباره ارسال کنید. برای لغو، «بازگشت به پنل» را بزنید.",
                cancellationToken: cancellationToken);
            return;
        }

        // The backend is the financial/authorization boundary: no local paid flag or owner assertion substitutes for provider proof.
        var result = await ConfirmTenantGatewayOrderByOwnerAsync(
            _selectedOwnerStore.Id, orderId, message.From.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await _state.ClearUserStatus(new User { Id = message.From.Id });
        await botClient.SendMessage(
            chatId: message.Chat.Id,
            text: result,
            parseMode: ParseMode.Html,
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);
        await SHOWOWNERPANELASYNC(botClient, message.Chat.Id, owner, null, cancellationToken);
    }
}
