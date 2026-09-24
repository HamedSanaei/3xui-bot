using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Validates central wallet-charge admission and creates persisted tenant-origin invoices through existing provider clients.</summary>
/// <param name="workflow">Operation-local users.db persistence; no context survives provider I/O.</param>
/// <param name="config">Platform-controlled gateway configuration, never tenant credentials.</param>
/// <param name="availability">Live global gateway switches.</param>
    /// <param name="policy">Fresh exact-store approval policy; null is allowed only for the owned-only legacy caller.</param>
/// <param name="hoosh">Existing HooshPay transport.</param>
/// <param name="tetra">Existing Tetraminator transport.</param>
/// <param name="unique">Existing UniquePay transport.</param>
/// <param name="atlas">Existing AtlasPay transport.</param>
/// <param name="now">Existing NOWPayments transport.</param>
/// <remarks>Provider settlement, wallet receipts, referrals and delivery stay in existing services. Personal storefront cards are never funding providers.</remarks>
public sealed class WalletChargeApplicationService(UserWorkflowStore workflow, AppConfig config,
    IPaymentGatewayAvailability availability, TenantCustomerWalletPolicy policy, HooshPay hoosh,
    Tetraminator tetra, UniquePay unique, AtlasPay atlas, NowPayments now)
{
    /// <summary>Customer-safe invoice delivery data; raw provider responses and API credentials are never exposed.</summary>
    /// <param name="Url">Provider-issued payment link, intended only for its paying customer.</param>
    /// <param name="CheckCallback">Compact callback identifying the persisted payment; the handler must revalidate ownership.</param>
    /// <param name="Notice">Persian customer-facing payment guidance without secrets.</param>
    /// <param name="DirectPayment">Optional AtlasPay card details kept only in this in-memory result for immediate customer display.</param>
    public sealed record Invoice(string Url, string CheckCallback, string Notice,
        AtlasPayCustomerPaymentUi.DirectPayment DirectPayment = null);

    /// <summary>Issues the shared owned/tenant HooshPay wallet invoice after its row has committed.</summary>
    /// <param name="payment">Persisted wallet-charge row, including immutable origin and amount.</param>
    /// <param name="token">Cancellation of admission revalidation and the non-retried provider call.</param>
    /// <returns>Provider creation result for the caller to apply and persist before Telegram delivery.</returns>
    /// <remarks>No provider request runs inside a database transaction. Failures do not authorize another create.</remarks>
    public async Task<HooshPayCreateInvoiceResponse> CreateHooshPayAsync(HooshPayPaymentInfo payment, CancellationToken token)
    {
        await ValidateCreationAsync(payment.Id, payment.PaymentPurpose, payment.BotId, payment.WalletOriginBotType, PaymentGateway.HooshPay, payment.AmountToman, token);
        return await hoosh.CreateInvoiceAsync(payment.AmountToman, payment.OrderId, $"Wallet charge {payment.OrderId}", payment.IpnCallbackUrl, payment.ReturnUrl, token);
    }

    /// <summary>Issues the shared owned/tenant Tetraminator wallet invoice from an already persisted identity.</summary>
    /// <param name="payment">Persisted wallet charge with central callback URL.</param>
    /// <param name="token">Cancellation of revalidation and provider I/O.</param>
    /// <returns>The provider result to persist; not proof of payment.</returns>
    /// <remarks>Automatic creation retry is forbidden after any ambiguous result.</remarks>
    public async Task<TetraminatorCreateInvoiceResponse> CreateTetraminatorAsync(TetraminatorPaymentInfo payment, CancellationToken token)
    {
        await ValidateCreationAsync(payment.Id, payment.PaymentPurpose, payment.BotId, payment.WalletOriginBotType, PaymentGateway.Tetraminator, payment.AmountToman, token);
        return await tetra.CreateInvoiceAsync(payment.AmountToman, payment.CallbackUrl, token);
    }

    /// <summary>Issues the shared owned/tenant UniquePay wallet invoice.</summary>
    /// <param name="payment">Persisted wallet-charge identity and positive toman amount.</param>
    /// <param name="returnUrl">Configured central return endpoint with the saved merchant hash.</param>
    /// <param name="callbackUrl">Configured central inquiry-trigger endpoint; it is not payment proof.</param>
    /// <param name="token">Cancellation of revalidation and provider I/O.</param>
    /// <returns>Provider invoice metadata to persist before displaying its link.</returns>
    /// <remarks>The row must be committed first. External creation runs outside a write transaction and is never automatically retried.</remarks>
    public async Task<UniquePayCreateInvoiceResponse> CreateUniquePayAsync(UniquePayPaymentInfo payment, string returnUrl, string callbackUrl, CancellationToken token)
    {
        await ValidateCreationAsync(payment.Id, payment.PaymentPurpose, payment.BotId, payment.WalletOriginBotType, PaymentGateway.UniquePay, payment.BaseAmountToman, token);
        return await unique.CreateInvoiceAsync(payment.HashId, payment.BaseAmountToman, returnUrl, callbackUrl, token);
    }

    /// <summary>Issues the shared owned/tenant AtlasPay wallet invoice using its committed creation-attempt identity.</summary>
    /// <param name="payment">Persisted wallet row with BeginCreationAttempt already saved.</param>
    /// <param name="token">Cancellation of revalidation and non-retried creation.</param>
    /// <returns>Provider invoice metadata; settlement still requires official inquiry.</returns>
    /// <remarks>The caller records creation failure/ambiguity on the existing attempt; neither an exception nor a Telegram retry authorizes another create.</remarks>
    public async Task<AtlasPayCreateOrderResponse> CreateAtlasPayAsync(AtlasPayPaymentInfo payment, CancellationToken token)
    {
        await ValidateCreationAsync(payment.Id, payment.PaymentPurpose, payment.BotId, payment.WalletOriginBotType, PaymentGateway.AtlasPay, payment.BaseAmountToman, token);
        return await atlas.CreateOrderAsync(payment.MerchantOrderRef, payment.BaseAmountToman, payment.TelegramUserId, token);
    }

    /// <summary>Issues the shared owned/tenant NOWPayments wallet invoice without changing exchange-rate or transport policy.</summary>
    /// <param name="payment">Persisted crypto wallet-charge row.</param>
    /// <param name="currency">Existing configured pricing currency.</param>
    /// <param name="successUrl">Customer return URL for this originating bot.</param>
    /// <param name="cancelUrl">Customer cancellation return URL for this originating bot.</param>
    /// <param name="token">Cancellation of revalidation, canonical rate read and non-retried provider request.</param>
    /// <returns>Invoice data, including the canonical rate evidence for persistence by the caller.</returns>
    /// <remarks>The existing transport converts toman using its canonical quote. Invoice creation is not payment proof and remains outside SQLite transactions.</remarks>
    public async Task<NowPaymentsInvoiceResponse> CreateNowPaymentsAsync(SwapinoPaymentInfo payment, string currency, string successUrl, string cancelUrl, CancellationToken token)
    {
        await ValidateCreationAsync(payment.Id, payment.PaymentPurpose, payment.BotId, payment.WalletOriginBotType, PaymentGateway.NowPayments, payment.AmountToman, token);
        return await now.CreateInvoiceAsync(payment.AmountToman, payment.OrderId, $"Wallet charge {payment.OrderId}", null, currency, successUrl, cancelUrl, token);
    }

    /// <summary>Revalidates live admission immediately before the external invoice mutation for either bot family.</summary>
    /// <param name="id">Positive local provider payment id, proving the caller persisted intent first.</param>
    /// <param name="purpose">Must be wallet_charge; tenant direct orders use their existing orchestrators.</param>
    /// <param name="botId">Persisted originating runtime id.</param>
    /// <param name="botType">Persisted owned or tenant origin, never callback supplied.</param>
    /// <param name="gateway">Central provider to contact.</param>
    /// <param name="amount">Positive wallet credit amount in toman.</param>
    /// <param name="token">Cancellation of fresh permission reads.</param>
    /// <returns>A task completing only when creation remains permitted.</returns>
    /// <exception cref="InvalidOperationException">Saved intent, gateway, amount or exact-store approval is invalid.</exception>
    private async Task ValidateCreationAsync(int id, string purpose, string botId, string botType, PaymentGateway gateway, long amount, CancellationToken token)
    {
        if (id <= 0 || purpose != TenantBotPaymentPurposes.WalletCharge || !availability.Snapshot.IsEnabled(gateway)
            || !IsValidAmount(gateway, amount, config)) throw new InvalidOperationException("Wallet charge admission is invalid.");
        if (botType == BotInstanceTypes.Tenant)
        {
            if (policy == null || !IsAvailable(gateway, await policy.RequireAsync(botId, token)))
                throw new InvalidOperationException("Tenant wallet admission was revoked.");
        }
        else if (botType != BotInstanceTypes.Owned) throw new InvalidOperationException("Unknown wallet charge origin.");
    }

    /// <summary>Checks global and per-store gateway availability, excluding personal card payments.</summary>
    /// <param name="gateway">One of the supported central automatic payment gateways.</param>
    /// <param name="store">Fresh approved storefront snapshot.</param>
    /// <returns>True only when the central switch and this store's switch both permit invoice creation.</returns>
    public bool IsAvailable(PaymentGateway gateway, BotInstance store) => TenantCustomerWalletPolicy.IsApproved(store)
        && availability.Snapshot.IsEnabled(gateway) && gateway switch
        {
            PaymentGateway.HooshPay => store.TenantHooshPayEnabled,
            PaymentGateway.Tetraminator => store.TenantTetraminatorEnabled,
            PaymentGateway.UniquePay => store.TenantUniquePayEnabled,
            PaymentGateway.AtlasPay => store.TenantAtlasPayEnabled,
            PaymentGateway.NowPayments => store.TenantNowPaymentsEnabled,
            _ => false
        };

    /// <summary>Validates the authoritative toman amount using the same provider policies as owned wallet charging.</summary>
    /// <param name="gateway">Supported central payment gateway.</param>
    /// <param name="amount">Positive whole Iranian toman amount supplied by the wallet owner.</param>
    /// <param name="config">Global provider amount limits.</param>
    /// <returns>True when the request satisfies local amount policy; providers may impose further limits.</returns>
    public static bool IsValidAmount(PaymentGateway gateway, long amount, AppConfig config) => amount > 0 && gateway switch
    {
        PaymentGateway.HooshPay => HooshPayAmountPolicy.IsValid(amount),
        PaymentGateway.UniquePay => UniquePayAmountPolicy.IsValid(amount),
        PaymentGateway.Tetraminator => amount >= config.TetraminatorMinimumAmountToman,
        PaymentGateway.AtlasPay or PaymentGateway.NowPayments => true,
        _ => false
    };

    /// <summary>Creates one tenant wallet-charge invoice after the conversation has consumed its confirmation.</summary>
    /// <param name="botId">Internal originating storefront id from the active runtime.</param>
    /// <param name="customerId">Telegram sender's global wallet id, never the owner id.</param>
    /// <param name="chatId">Original customer conversation chat id for durable notification.</param>
    /// <param name="amount">Validated positive whole toman credit amount.</param>
    /// <param name="gateway">Explicit central gateway chosen by this customer.</param>
    /// <param name="token">Cancellation of database and external calls; provider calls never run inside a transaction.</param>
    /// <returns>A customer invoice link and ownership-checked inquiry callback.</returns>
    /// <exception cref="InvalidOperationException">Approval, gateway or amount is invalid.</exception>
    /// <remarks>The row commits before the non-retried provider create. Uncertain creation must never be automatically repeated.
    /// PaymentPurpose always remains wallet_charge; legitimate settlement continues after approval is revoked.</remarks>
    /// <example><code>var invoice = await charges.CreateTenantAsync(store.Id, sender.Id, chat.Id, amountToman, gateway, token);</code></example>
    public async Task<Invoice> CreateTenantAsync(string botId, long customerId, long chatId, long amount, PaymentGateway gateway, CancellationToken token)
    {
        var store = await policy.RequireAsync(botId, token);
        if (!IsAvailable(gateway, store) || !IsValidAmount(gateway, amount, config))
            throw new InvalidOperationException("Wallet charge gateway or amount is unavailable.");
        var returnUrl = $"https://t.me/{store.Username?.TrimStart('@')}?start=payment_success";
        const string notice = "فاکتور افزایش موجودی کیف پول سراسری ساخته شد. اعتبار فقط پس از تأیید رسمی درگاه افزوده می‌شود.";
        switch (gateway)
        {
            case PaymentGateway.HooshPay:
            {
                var p = HooshPayPaymentInfo.CreateWalletCharge(customerId, amount, config.HooshPayIpnUrl, returnUrl, chatId);
                p.BotId = botId; p.BotUsername = store.Username; p.WalletOriginBotType = BotInstanceTypes.Tenant; p.WalletOriginTelegramBotId = store.TelegramBotId;
                workflow.Add(p); await workflow.SaveAsync(token);
                p.Apply((await CreateHooshPayAsync(p, token))?.data);
                await workflow.SaveAsync(token);
                return new(p.PaymentUrl, $"hpchk_{p.Id}", notice);
            }
            case PaymentGateway.Tetraminator:
            {
                var p = TetraminatorPaymentInfo.CreateWalletCharge(customerId, amount, config.TetraminatorCallbackUrl, chatId);
                p.BotId = botId; p.BotUsername = store.Username; p.WalletOriginBotType = BotInstanceTypes.Tenant; p.WalletOriginTelegramBotId = store.TelegramBotId;
                p.CallbackUrl = AppendQuery(config.TetraminatorCallbackUrl, "orderId", p.OrderId);
                workflow.Add(p); await workflow.SaveAsync(token);
                p.Apply(await CreateTetraminatorAsync(p, token));
                await workflow.SaveAsync(token);
                return new(p.PaymentLink, $"tmchk_{p.Id}", notice);
            }
            case PaymentGateway.UniquePay:
            {
                var p = UniquePayPaymentInfo.CreateWalletCharge(customerId, chatId, amount, config.UniquePayFeePercent);
                p.BotId = botId; p.BotUsername = store.Username; p.WalletOriginBotType = BotInstanceTypes.Tenant; p.WalletOriginTelegramBotId = store.TelegramBotId;
                workflow.Add(p); await workflow.SaveAsync(token);
                p.Apply(await CreateUniquePayAsync(p, AppendQuery(config.UniquePayReturnUrl, "hashId", p.HashId),
                    AppendQuery(config.UniquePayCallbackUrl, "hashId", p.HashId), token));
                p.NextInquiryAtUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(config.UniquePayReconciliationIntervalSeconds, 10, 3600));
                await workflow.SaveAsync(token);
                return new(p.PaymentLink, $"upchk_{p.Id}", notice);
            }
            case PaymentGateway.AtlasPay:
            {
                var p = AtlasPayPaymentInfo.CreateWalletCharge(customerId, chatId, amount);
                p.BotId = botId; p.BotUsername = store.Username; p.WalletOriginBotType = BotInstanceTypes.Tenant; p.WalletOriginTelegramBotId = store.TelegramBotId;
                p.BeginCreationAttempt(DateTime.UtcNow);
                workflow.Add(p); await workflow.SaveAsync(token);
                AtlasPayCustomerPaymentUi.DirectPayment directPayment = null;
                try
                {
                    var created = await CreateAtlasPayAsync(p, token);
                    p.ApplyCreate(created, DateTime.UtcNow,
                        AtlasPayPollingPolicy.GetInitialNextInquiryUtc(config, DateTime.UtcNow));
                    await workflow.SaveAsync(token);
                    directPayment = AtlasPayCustomerPaymentUi.TryCreateDirectPayment(created);
                }
                catch (Exception ex)
                {
                    p.RecordCreationFailure(AtlasPay.IsDefinitiveCreateFailure(ex), "create_unconfirmed", DateTime.UtcNow);
                    await workflow.SaveAsync(CancellationToken.None);
                    throw;
                }
                return new(
                    p.CustomerStartLink,
                    $"apchk_{p.Id}",
                    AtlasPayCustomerPaymentUi.BuildLinkFallbackText(
                        p.TotalAmountToman!.Value,
                        p.PaymentDeadlineAtUtc,
                        p.TrackingCode),
                    directPayment);
            }
            case PaymentGateway.NowPayments:
            {
                var p = SwapinoPaymentInfo.CreateCryptoCharge(customerId, amount, config.NowpaymentIpnUrl,
                    chatId: chatId, baseCurrency: config.NowpaymentPriceCurrency);
                p.BotId = botId; p.BotUsername = store.Username; p.WalletOriginBotType = BotInstanceTypes.Tenant; p.WalletOriginTelegramBotId = store.TelegramBotId;
                workflow.Add(p); await workflow.SaveAsync(token);
                var invoice = await CreateNowPaymentsAsync(p,
                    string.IsNullOrWhiteSpace(config.NowpaymentPriceCurrency) ? "usdtbsc" : config.NowpaymentPriceCurrency, returnUrl, returnUrl, token);
                var data = NowPaymentsPaymentRecordData.FromInvoiceResponse(invoice); data.OrderId = p.OrderId;
                p.SetNowPaymentsData(data); p.BaseAmount = invoice.price_amount; p.BaseCurrency = invoice.price_currency;
                await workflow.SaveAsync(token);
                return new(invoice.invoice_url, $"check_crypto_payment_{p.OrderId}", notice);
            }
            default: throw new InvalidOperationException("Unsupported central wallet-charge provider.");
        }
    }

    /// <summary>Adds a non-authoritative invoice lookup identity to the configured central callback endpoint.</summary>
    /// <param name="url">Configured absolute platform URL.</param>
    /// <param name="key">Provider lookup parameter name.</param>
    /// <param name="value">Saved payment identity, never payment proof.</param>
    /// <returns>An absolute URL preserving existing parameters.</returns>
    private static string AppendQuery(string url, string key, string value)
    {
        var builder = new UriBuilder(url);
        builder.Query = (string.IsNullOrWhiteSpace(builder.Query) ? "" : builder.Query.TrimStart('?') + "&") + key + "=" + Uri.EscapeDataString(value);
        return builder.Uri.AbsoluteUri;
    }
}
