using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Adminbot.Utils;

public partial class TenantBotService
{
    /// <summary>Routes approved customer wallet actions without entering the owned renewal engine.</summary>
    /// <param name="client">Current tenant Telegram client.</param>
    /// <param name="update">Customer update; its authenticated sender selects the global wallet.</param>
    /// <param name="customer">Global profile loaded from that sender.</param>
    /// <param name="state">Existing conversation state scoped to this bot and sender.</param>
    /// <param name="token">Cancellation of admission and the existing fulfillment saga.</param>
    /// <returns>True for wallet actions, including rejected stale actions.</returns>
    /// <remarks>Fresh persisted approval is mandatory. Callback data never supplies a trusted price or wallet owner.</remarks>
    private async Task<bool> TryHandleCustomerWalletAsync(ITelegramBotClient client, Update update, CredUser customer, User state, CancellationToken token)
    {
        var callback = update.CallbackQuery;
        var action = callback?.Data ?? update.Message?.Text;
        var amountInput = callback == null && state?.Flow == "tenant-wallet-charge" && state.LastStep == "amount";
        if (amountInput && action is "/start" or "بازگشت")
        { await _state.ClearUserStatus(state); return false; }
        var purchase = action?.StartsWith("TN:PAYWALLET:", StringComparison.Ordinal) == true;
        var renewal = action?.StartsWith("TN:RNWALLET:", StringComparison.Ordinal) == true;
        var history = action == "📒 تراکنش‌های من" || action?.StartsWith("TCW:h:", StringComparison.Ordinal) == true;
        if (!amountInput && !purchase && !renewal && !history && action != "💰 کیف پول" && action?.StartsWith("TCW:", StringComparison.Ordinal) != true) return false;
        var actor = callback?.From.Id ?? update.Message?.From?.Id ?? 0;
        var chat = callback?.Message?.Chat.Id ?? update.Message?.Chat.Id ?? actor;
        if (actor <= 0 || customer?.TelegramUserId != actor) return true;
        BotInstance store;
        try { store = await _serviceProvider.GetRequiredService<TenantCustomerWalletPolicy>().RequireAsync(BotContextAccessor.CurrentBotId, token); }
        catch (InvalidOperationException)
        {
            if (callback != null)
                await SafeAnswerCallbackQueryAsync(client, callback.Id, "کیف پول مشتری در این فروشگاه فعال نیست.", showAlert: true, cancellationToken: token);
            else
                await client.SendMessage(chat, "کیف پول مشتری در این فروشگاه فعال نیست.", cancellationToken: token);
            return true;
        }
        if (!await EnsureTenantCustomerJoinAsync(client, chat, actor, store, token))
        {
            if (callback != null)
                await SafeAnswerCallbackQueryAsync(client, callback.Id, "ابتدا در کانال عضو شوید.", showAlert: true, cancellationToken: token);
            return true;
        }
        if (callback != null) await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
        if (action == "TCW:charge")
        {
            await _state.SaveUserStatus(new User { Id = actor, Flow = "tenant-wallet-charge", LastStep = "amount" });
            await client.SendMessage(chat, "مبلغ افزایش موجودی کیف پول سراسری را به تومان وارد کنید.\nکارت‌به‌کارت شخصی فروشگاه برای شارژ کیف پول قابل استفاده نیست.", cancellationToken: token);
            return true;
        }
        var charges = _serviceProvider.GetRequiredService<WalletChargeApplicationService>();
        if (amountInput)
        {
            if (!long.TryParse(action?.PersianNumbersToEnglish(), out var amount) || amount <= 0)
            { await client.SendMessage(chat, "مبلغ مثبت و معتبر به تومان وارد کنید.", cancellationToken: token); return true; }
            var nonce = Guid.NewGuid().ToString("N");
            await _state.SaveUserStatus(new User { Id = actor, Flow = "tenant-wallet-charge", LastStep = "gateway",
                ConfigLink = amount.ToString(System.Globalization.CultureInfo.InvariantCulture), SubLink = nonce });
            var gateways = new[] { PaymentGateway.HooshPay, PaymentGateway.Tetraminator, PaymentGateway.UniquePay, PaymentGateway.AtlasPay, PaymentGateway.NowPayments };
            var rows = gateways.Where(g => charges.IsAvailable(g, store) && WalletChargeApplicationService.IsValidAmount(g, amount, _appConfig))
                .Select(g => new[] { InlineKeyboardButton.WithCallbackData(TenantPaymentProviderLabel(g.ToString()), $"TCW:g:{(int)g}:{nonce}") }).ToList();
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به کیف پول", "TCW:home") });
            await client.SendMessage(chat, $"مبلغ افزایش موجودی: {amount:N0} تومان\nدرگاه مرکزی را انتخاب کنید. کارمزد احتمالی در صفحه پرداخت نمایش داده می‌شود.",
                replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: token);
            return true;
        }
        if (action?.StartsWith("TCW:g:", StringComparison.Ordinal) == true)
        {
            var parts = action.Split(':');
            if (parts.Length != 4 || state.Flow != "tenant-wallet-charge" || state.LastStep != "gateway" || state.SubLink != parts[3]
                || !int.TryParse(parts[2], out var gateway) || !long.TryParse(state.ConfigLink, out var amount))
            { await client.SendMessage(chat, "این درخواست منقضی شده است. کیف پول را دوباره باز کنید.", cancellationToken: token); return true; }
            // Consume before the non-idempotent provider create. A replay cannot issue another invoice.
            await _state.ClearUserStatus(state);
            try
            {
                var invoice = await charges.CreateTenantAsync(store.Id, actor, chat, amount, (PaymentGateway)gateway, token);
                await client.SendMessage(chat, invoice.Notice, replyMarkup: new InlineKeyboardMarkup(new[] {
                    new[] { InlineKeyboardButton.WithUrl("پرداخت در درگاه مرکزی", invoice.Url) },
                    new[] { InlineKeyboardButton.WithCallbackData("بررسی وضعیت پرداخت", invoice.CheckCallback) }
                }), cancellationToken: token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Tenant wallet invoice unavailable. BotId={BotId} ErrorType={ErrorType}", store.Id, ex.GetType().Name);
                await client.SendMessage(chat, "فاکتور قابل نمایش نیست. نتیجه درخواست برای بررسی محفوظ است؛ پرداخت یا برداشت خودکار تکرار نمی‌شود.", cancellationToken: token);
            }
            return true;
        }
        if (history)
        {
            var page = action.StartsWith("TCW:h:", StringComparison.Ordinal) && int.TryParse(action[6..], out var parsed) ? Math.Clamp(parsed, 0, 10000) : 0;
            var (items, total) = await _walletLedgerService.GetPageAsync(actor, page, 8, token);
            var lines = items.Select(x => $"{x.CreatedAtUtc:yyyy-MM-dd} | {x.Direction} | {x.AmountToman:N0} تومان | {x.Reason} | {x.Provider}");
            await client.SendMessage(chat, "📒 تراکنش‌های کیف پول سراسری شما\n" + string.Join("\n", lines),
                replyMarkup: new InlineKeyboardMarkup(new[] {
                    new[] { InlineKeyboardButton.WithCallbackData("صفحه بعد", $"TCW:h:{page + 1}") },
                    new[] { InlineKeyboardButton.WithCallbackData("کیف پول", "TCW:home") }
                }), cancellationToken: token);
            return true;
        }
        if (purchase || renewal)
        {
            var funding = _serviceProvider.GetRequiredService<TenantCustomerWalletFunding>();
            TenantBotOrder order;
            if (purchase)
            {
                var selection = PARSESELECTIONFROMPAYACTION(action["TN:PAYWALLET:".Length..]);
                if (selection == null || callback.Message == null || !await EnsureTenantPurchaseSelectionIsCurrentAsync(client, callback, store, selection, token)) return true;
                var price = CalculateTenantPrice(store, selection);
                order = CreateTenantOrder(store, customer, chat, selection, price, "wallet");
                // Multiple clicks on one invoice message identify one sale even when Telegram update ids differ.
                order = await funding.AdmitAsync(order, $"tcw:{store.Id}:{actor}:{chat}:{callback.Message.MessageId}", token);
            }
            else
            {
                if (!int.TryParse(action["TN:RNWALLET:".Length..], out var id)) return true;
                order = await _workflow.ReadAsync(db => db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == id && x.CustomerTelegramUserId == actor && x.TenantBotId == store.Id && x.OrderKind == TenantBotOrderKinds.Renew, token));
                if (order == null) return true;
                if (order.PaymentProvider != "wallet")
                {
                    order = await GetPendingTenantRenewOrderAsync(id, store, customer, token);
                    if (order == null) return true;
                    order = await funding.AdmitAsync(order, $"tcw-renew:{id}", token);
                }
                else if (await funding.ReadPaidEvidenceAsync(order, token) == null)
                {
                    // An insufficient-balance attempt is still unpaid; its old quote cannot bypass live renewal checks.
                    order = await GetPendingTenantRenewOrderCoreAsync(id, store, customer, token, allowCustomerWallet: true);
                    if (order == null) return true;
                }
            }
            if (!await funding.DebitAsync(order, token))
            {
                var currentStore = await _workflow.ReadAsync(db => db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == store.Id, token));
                await client.SendMessage(chat, $"موجودی کیف پول کافی نیست.\nموجودی: {await _credentialsDbContext.GetAccountBalance(actor):N0} تومان\nمبلغ لازم: {order.SalePriceToman:N0} تومان",
                    replyMarkup: TenantCustomerWalletPolicy.IsApproved(currentStore)
                        ? new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("افزایش موجودی", "TCW:charge")) : null,
                    cancellationToken: token);
                return true;
            }
            await FULFILLPAIDTENANTORDERASYNC(order, "customer-wallet", null, null, false, token);
            return true;
        }
        await client.SendMessage(chat, $"💰 کیف پول سراسری پلتفرم\nاین موجودی متعلق به شماست و در اختیار مالک فروشگاه نیست.\nموجودی: {await _credentialsDbContext.GetAccountBalance(actor):N0} تومان",
            replyMarkup: new InlineKeyboardMarkup(new[] {
                new[] { InlineKeyboardButton.WithCallbackData("افزایش موجودی", "TCW:charge") },
                new[] { InlineKeyboardButton.WithCallbackData("📒 تراکنش‌های من", "TCW:h:0") },
                new[] { InlineKeyboardButton.WithCallbackData("بازگشت به فروشگاه", "TN:home") }
            }), cancellationToken: token);
        return true;
    }

    /// <summary>Resumes paid customer-wallet orders from durable proof after restart or approval revocation.</summary>
    /// <param name="orderId">Internal users.db tenant order id selected by recovery.</param>
    /// <param name="token">Host cancellation propagated to the existing fulfillment saga.</param>
    /// <returns>A task completing after receipt reconciliation and existing guarded fulfillment.</returns>
    /// <remarks>No automatic debit is permitted. Refund authorization remains terminal for provisioning.</remarks>
    public async Task RecoverCustomerWalletOrderAsync(int orderId, CancellationToken token)
    {
        var order = await _workflow.ReadAsync(db => db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, token));
        if (order?.PaymentProvider != "wallet" || order.IsFulfilled) return;
        var funding = _serviceProvider.GetRequiredService<TenantCustomerWalletFunding>();
        if (order.CustomerWalletState is "refund_pending" or "refunded")
        { await funding.RefundRejectedAsync(order.Id, token); return; }
        var receipt = await funding.ReadPaidEvidenceAsync(order, token);
        if (receipt == null) return;
        await funding.ReconcileReceiptAsync(order, receipt, token);
        await FULFILLPAIDTENANTORDERASYNC(order, "customer-wallet-recovery", null, null, false, token);
    }

    /// <summary>Persists a short-lived confirmation in the owned bot/user conversation, binding approval to the displayed storefront.</summary>
    /// <param name="BotId">Internal storefront id in users.db.</param>
    /// <param name="TelegramBotId">Verified BotFather identity at display time.</param>
    /// <param name="OwnerId">Storefront owner's Telegram user id at display time.</param>
    /// <param name="ExpiresAtUtc">UTC deadline after which confirmation is rejected.</param>
    /// <param name="Revision">Persisted storefront modification ticks used to reject stale confirmations.</param>
    private sealed record WalletApprovalPanel(string BotId, long TelegramBotId, long OwnerId, DateTime ExpiresAtUtc, long Revision);

    /// <summary>Lists/searches storefronts and processes super-admin-only customer-wallet approval from owned bots.</summary>
    /// <param name="client">Current owned bot client; never a tenant-owner-controlled client.</param>
    /// <param name="update">Telegram update whose authenticated sender supplies the administrator identity.</param>
    /// <param name="token">Cancellation of reads, permission persistence and Telegram delivery.</param>
    /// <returns>True when the wallet administration command or callback was consumed, including rejected attempts.</returns>
    /// <remarks>Use /tenantwallet with an optional username, brand, internal id or owner id. Confirmation callbacks carry only a
    /// nonce and expire after five minutes. Owners have no permission toggle. Every mutation rechecks configured super-admin authority.</remarks>
    public async Task<bool> TryHandleWalletAdminAsync(ITelegramBotClient client, Update update, CancellationToken token)
    {
        var callback = update.CallbackQuery;
        var text = update.Message?.Text;
        if (callback?.Data?.StartsWith("TWA:", StringComparison.Ordinal) != true
            && text != "/tenantwallet" && text?.StartsWith("/tenantwallet ", StringComparison.Ordinal) != true) return false;
        var actor = callback?.From.Id ?? update.Message?.From?.Id ?? 0;
        var chat = callback?.Message?.Chat.Id ?? update.Message?.Chat.Id ?? actor;
        if (BotContextAccessor.CurrentBotType != BotInstanceTypes.Owned || actor <= 0 || _appConfig.AdminsUserIds?.Contains(actor) != true)
        {
            await client.SendMessage(chat, "دسترسی مجاز نیست.", cancellationToken: token);
            return true;
        }
        if (callback == null)
        {
            var search = text.Length > 13 ? text[13..].Trim() : "";
            var rows = await _workflow.ReadAsync(db => db.BotInstances.AsNoTracking()
                .Where(x => x.Type == BotInstanceTypes.Tenant && (search == "" || x.Id.Contains(search)
                    || (x.Username != null && x.Username.Contains(search)) || (x.BrandName != null && x.BrandName.Contains(search))
                    || x.OwnerTelegramUserId.ToString() == search))
                .OrderBy(x => x.OwnerTelegramUserId).ThenBy(x => x.TenantStoreNumber).Take(30).ToListAsync(token));
            await client.SendMessage(chat, "مدیریت مجوز کیف پول مشتری\nبرای جستجو: /tenantwallet نام یا شناسه مالک\nحداکثر ۳۰ نتیجه:\n\nمجوز مدیر و فعال‌سازی مالک مستقل هستند؛ کیف پول فقط با هر دو فعال می‌شود.",
                replyMarkup: new InlineKeyboardMarkup(rows.Select(x => new[] { InlineKeyboardButton.WithCallbackData(
                    $"{x.BrandName ?? x.Username ?? x.Id} | {(TenantCustomerWalletPolicy.HasValidGrant(x) ? "مجوز✅" : "مجوز❌")} | {(x.TenantCustomerWalletOwnerEnabled ? "مالک✅" : "مالک❌")}",
                    $"TWA:o:{x.OwnerTelegramUserId}:{x.TenantStoreNumber}") })), cancellationToken: token);
            return true;
        }
        await SafeAnswerCallbackQueryAsync(client, callback.Id, cancellationToken: token);
        var parts = callback.Data.Split(':');
        if (parts.Length == 4 && parts[1] == "o" && long.TryParse(parts[2], out var owner) && int.TryParse(parts[3], out var number))
        {
            var store = await _workflow.ReadAsync(db => db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == owner && x.TenantStoreNumber == number, token));
            if (store?.TelegramBotId is not > 0) { await client.SendMessage(chat, "هویت ربات فروشگاه ثبت نشده است.", cancellationToken: token); return true; }
            var nonce = Guid.NewGuid().ToString("N");
            await _state.SaveUserStatus(new User { Id = actor, Flow = "tenant-wallet-admin", LastStep = "confirm",
                ConfigLink = nonce, SubLink = JsonConvert.SerializeObject(new WalletApprovalPanel(store.Id, store.TelegramBotId.Value, owner, DateTime.UtcNow.AddMinutes(5), (store.UpdatedAtUtc ?? store.CreatedAtUtc).Ticks)) });
            await client.SendMessage(chat,
                $"فروشگاه: {store.BrandName} @{store.Username}\nشناسه: {store.Id}\nمالک: {owner}\nربات: {store.TelegramBotId}\nروشن بودن فروشگاه: {store.Enabled}\nمجوز سوپرادمین: {TenantCustomerWalletPolicy.HasValidGrant(store)}\nفعال‌سازی توسط مالک: {store.TenantCustomerWalletOwnerEnabled}\nوضعیت نهایی کیف پول: {TenantCustomerWalletPolicy.IsApproved(store)}\nتأییدکننده: {store.TenantCustomerWalletApprovedByTelegramUserId}\nزمان تأیید: {store.TenantCustomerWalletApprovedAtUtc:O}\n\n⚠️ اعطای مجوز به‌تنهایی کیف پول را فعال نمی‌کند. مالک همان فروشگاه باید بعداً از پنل مدیریتی خودش آن را فعال کند.",
                replyMarkup: new InlineKeyboardMarkup(new[] {
                    new[] { InlineKeyboardButton.WithCallbackData("اعطای مجوز کیف پول به مالک", "TWA:y:" + nonce) },
                    new[] { InlineKeyboardButton.WithCallbackData("لغو مجوز کیف پول مشتری", "TWA:n:" + nonce) }
                }), cancellationToken: token);
            return true;
        }
        var state = await _state.GetUserStatus(actor);
        WalletApprovalPanel panel = null;
        if (state.Flow == "tenant-wallet-admin" && state.LastStep == "confirm" && parts.Length == 3 && state.ConfigLink == parts[2])
            panel = JsonConvert.DeserializeObject<WalletApprovalPanel>(state.SubLink ?? "null");
        if (panel == null || panel.ExpiresAtUtc <= DateTime.UtcNow || parts[1] is not ("y" or "n"))
        { await client.SendMessage(chat, "این پنل منقضی شده است. /tenantwallet را دوباره باز کنید.", cancellationToken: token); return true; }
        try
        {
            await _serviceProvider.GetRequiredService<TenantCustomerWalletPolicy>().SetAsync(actor, panel.BotId,
                panel.TelegramBotId, panel.OwnerId, parts[1] == "y", token, panel.Revision);
            await _state.ResetUserStatus(new User { Id = actor });
            await client.SendMessage(chat,
                parts[1] == "y"
                    ? "✅ مجوز کیف پول برای این فروشگاه صادر شد. کیف پول هنوز خاموش است و مالک باید آن را از پنل مدیریتی فروشگاه فعال کند."
                    : "⛔️ مجوز کیف پول لغو شد و فعال‌سازی مالک نیز خاموش شد. عملیات مالی قبلاً commit‌شده همچنان از مسیر recovery امن ادامه می‌یابد.",
                cancellationToken: token);
        }
        catch (InvalidOperationException)
        { await client.SendMessage(chat, "هویت فروشگاه تغییر کرده است. /tenantwallet را دوباره باز کنید.", cancellationToken: token); }
        return true;
    }
}
