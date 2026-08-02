using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

/// <summary>
/// Receives payment gateway callbacks and routes paid invoices to the correct settlement service.
/// </summary>
/// <remarks>
/// NOWPayments is used for crypto wallet charges. HooshPay and UniquePay are used for rial wallet charges and tenant
/// storefront orders. Tetraminator and UniquePay callbacks are unsigned triggers and are always verified through
/// provider inquiry.
/// Tenant HooshPay rows are detected by <see cref="HooshPayPaymentInfo.PaymentPurpose"/> and are fulfilled by
/// <see cref="TenantBotService.ApplyPaidTenantOrderAsync"/> instead of the wallet settlement path.
/// </remarks>
[ApiController]
[Route("nowpayments-ipn")]
public class PaymentController : ControllerBase
{
    private static readonly SemaphoreSlim IpnLock = new SemaphoreSlim(1, 1);
    private static readonly SemaphoreSlim HooshPayIpnLock = new SemaphoreSlim(1, 1);
    private static readonly SemaphoreSlim TetraminatorCallbackLock = new(1, 1);
    /// <summary>Serializes UniquePay callback and browser-return triggers before authoritative inquiry.</summary>
    private static readonly SemaphoreSlim UniquePayTriggerLock = new(1, 1);

    private readonly UserDbContext _userDbcontext;
    /// <summary>Creates isolated users.db contexts for concurrent UniquePay HTTP triggers.</summary>
    private readonly UserDbContextFactory _userDbContextFactory;
    private readonly AppConfig _appConfig;
    private readonly NowPaymentsSettlementService _settlementService;
    private readonly HooshPaySettlementService _hooshPaySettlementService;
    private readonly Tetraminator _tetraminator;
    private readonly TetraminatorSettlementService _tetraminatorSettlementService;
    private readonly UniquePayReconciliationHostedService _uniquePayReconciliation;
    private readonly TenantBotService _tenantBotService;
    private readonly ILogger<PaymentController> _logger;

    /// <summary>
    /// Creates the payment controller with all settlement services required by the IPN endpoints.
    /// </summary>
    /// <param name="userDbContext">Runtime database containing local payment records.</param>
    /// <param name="userDbContextFactory">
    /// Factory used by concurrent UniquePay callback and return requests so they never share an EF Core context.
    /// </param>
    /// <param name="config">Application configuration containing gateway secrets.</param>
    /// <param name="settlementService">NOWPayments wallet settlement service.</param>
    /// <param name="hooshPaySettlementService">HooshPay wallet settlement service.</param>
    /// <param name="tetraminator">Tetraminator API client used to verify unsigned callbacks.</param>
    /// <param name="tetraminatorSettlementService">Tetraminator owned-wallet settlement service.</param>
    /// <param name="uniquePayReconciliation">
    /// Shared polling coordinator that always performs authoritative UniquePay inquiry before settlement.
    /// </param>
    /// <param name="tenantBotService">Tenant storefront fulfillment service for direct HooshPay orders.</param>
    /// <param name="logger">Controller logger.</param>
    public PaymentController(
        UserDbContext userDbContext,
        UserDbContextFactory userDbContextFactory,
        IConfiguration config,
        NowPaymentsSettlementService settlementService,
        HooshPaySettlementService hooshPaySettlementService,
        Tetraminator tetraminator,
        TetraminatorSettlementService tetraminatorSettlementService,
        UniquePayReconciliationHostedService uniquePayReconciliation,
        TenantBotService tenantBotService,
        ILogger<PaymentController> logger)
    {
        _userDbcontext = userDbContext;
        _userDbContextFactory = userDbContextFactory ?? throw new ArgumentNullException(nameof(userDbContextFactory));
        _appConfig = config.Get<AppConfig>();
        _settlementService = settlementService;
        _hooshPaySettlementService = hooshPaySettlementService;
        _tetraminator = tetraminator;
        _tetraminatorSettlementService = tetraminatorSettlementService;
        _uniquePayReconciliation = uniquePayReconciliation;
        _tenantBotService = tenantBotService;
        _logger = logger;
    }

    /// <summary>
    /// Receives Tetraminator's unsigned GET callback and settles only after an authoritative provider inquiry.
    /// </summary>
    /// <param name="orderId">Unique local order id embedded in the invoice-specific callback URL.</param>
    /// <param name="cancellationToken">Cancellation token for users.db, provider inquiry, and settlement.</param>
    /// <returns>
    /// HTTP 200 for verified or already-settled payments, 202 while the provider is not paid, 404 for an unknown local
    /// order, 409 for identity/amount mismatch, or 503 for a transient provider failure.
    /// </returns>
    /// <remarks>
    /// The callback itself contains no trusted payment data. Global gateway disablement does not block this endpoint,
    /// because invoices created before disablement must remain settleable.
    /// </remarks>
    [HttpGet("/tetraminator-callback")]
    public async Task<IActionResult> ReceiveTetraminator(
        [FromQuery(Name = "order_id")] string orderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return BadRequest(new { status = false, message = "order_id is required" });

        await TetraminatorCallbackLock.WaitAsync(cancellationToken);
        try
        {
            var payment = await _userDbcontext.TetraminatorPaymentInfos.FirstOrDefaultAsync(
                x => x.OrderId == orderId,
                cancellationToken);
            if (payment == null)
                return NotFound(new { status = false, message = "payment not found" });

            payment.CallbackReceived = true;
            payment.CallbackReceivedAtUtc ??= DateTime.UtcNow;
            try
            {
                var inquiry = await _tetraminator.InquiryAsync(payment.PayId, cancellationToken);
                payment.RawResponseJson = JsonConvert.SerializeObject(inquiry);
                payment.Apply(inquiry);
                var verified = TetraminatorPaymentVerifier.IsVerifiedPaid(payment, inquiry, out var errorCode);
                payment.ErrorCode = errorCode;
                payment.ErrorMessage = verified ? null : errorCode;
                if (!verified)
                {
                    await _userDbcontext.SaveChangesAsync(cancellationToken);
                    if (string.Equals(errorCode, "provider_not_paid", StringComparison.Ordinal))
                        return Accepted(new { status = true, settled = false, providerStatus = payment.PaymentStatus });

                    _logger.LogCritical(
                        "Tetraminator callback verification mismatch. paymentId={PaymentId}, orderId={OrderId}, errorCode={ErrorCode}, expectedAmount={ExpectedAmount}, actualAmount={ActualAmount}",
                        payment.Id,
                        payment.OrderId,
                        errorCode,
                        payment.AmountToman,
                        inquiry?.Amount);
                    return Conflict(new { status = false, settled = false, error = errorCode });
                }

                payment.PaymentStatus = TetraminatorStatuses.Paid;
                payment.PaidAtUtc ??= DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(cancellationToken);
                var settlement = string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase)
                    ? await _tenantBotService.ApplyPaidTenantOrderAsync(payment, "tetraminator-callback", cancellationToken)
                    : await _tetraminatorSettlementService.ApplyOfficialPaymentAsync(payment, "callback", cancellationToken: cancellationToken);
                return Ok(new { status = true, settled = true, result = settlement.Status.ToString() });
            }
            catch (Exception ex) when (ex is TetraminatorApiException or HttpRequestException or OperationCanceledException)
            {
                payment.ErrorCode = "provider_inquiry_failed";
                payment.ErrorMessage = ex.Message;
                payment.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbcontext.SaveChangesAsync(CancellationToken.None);
                _logger.LogWarning(ex, "Tetraminator callback inquiry failed. paymentId={PaymentId}, orderId={OrderId}", payment.Id, payment.OrderId);
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = false, message = "provider inquiry unavailable" });
            }
        }
        finally
        {
            TetraminatorCallbackLock.Release();
        }
    }

    /// <summary>
    /// Uses UniquePay's browser return as a safe inquiry trigger and never settles from query-string data.
    /// </summary>
    /// <param name="hashId">Merchant hash returned in the configured redirect URL.</param>
    /// <param name="cancellationToken">Cancellation token for local lookup, provider inquiry, and settlement.</param>
    /// <returns>
    /// HTTP 200 for paid/already-settled or pending invoices, 404 for an unknown hash, and 503 for a transient
    /// provider or local inquiry failure. Existing invoices remain processable even when global creation is disabled.
    /// </returns>
    /// <remarks>
    /// The bot-compatible provider route also sends a separate server callback. Both endpoints call the same
    /// reconciliation coordinator, which performs <c>/api/check-invoice</c> and applies all financial guards.
    /// </remarks>
    [HttpGet("/uniquepay-return")]
    public async Task<IActionResult> ReceiveUniquePayReturn(
        [FromQuery(Name = "hashId")] string hashId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hashId))
            return BadRequest(new { status = false, message = "hashId is required" });

        return await HandleUniquePayTriggerAsync(hashId, "return-trigger", cancellationToken);
    }

    /// <summary>
    /// Receives UniquePay's bot-gateway notification and treats it only as an authoritative-inquiry trigger.
    /// </summary>
    /// <param name="hashId">
    /// Merchant hash embedded in the invoice-specific configured callback URL. It locates a local payment but does not
    /// prove identity, paid state, amount, currency, fee, or fee payer.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for users.db lookup, provider inquiry, and settlement.</param>
    /// <returns>
    /// HTTP 200 after a paid/already-settled or still-pending authoritative inquiry, 404 for an unknown merchant hash,
    /// and 503 when the official provider inquiry is temporarily unavailable.
    /// </returns>
    /// <remarks>
    /// Callback form/body fields are deliberately ignored because the documentation provides no signature contract.
    /// Duplicate or forged requests cannot credit a wallet or fulfill a tenant order: each request must pass a fresh
    /// <c>/api/check-invoice</c> response and the durable idempotent settlement boundary.
    /// </remarks>
    /// <example>
    /// UniquePay calls <c>POST /uniquepay-callback?hashId=merchant-hash</c>; the action ignores any claimed paid status
    /// in the body and checks the saved hash through the provider API.
    /// </example>
    [HttpPost("/uniquepay-callback")]
    public async Task<IActionResult> ReceiveUniquePayCallback(
        [FromQuery(Name = "hashId")] string hashId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hashId))
            return BadRequest(new { status = false, message = "hashId is required" });

        return await HandleUniquePayTriggerAsync(hashId, "provider-callback", cancellationToken);
    }

    /// <summary>
    /// Resolves one UniquePay merchant hash and serializes its official inquiry and idempotent settlement.
    /// </summary>
    /// <param name="hashId">Exact non-secret merchant hash from an invoice-specific return or callback URL.</param>
    /// <param name="source">Safe audit label distinguishing provider callback from customer browser return.</param>
    /// <param name="cancellationToken">Cancellation token for gate waiting, users.db, UniquePay, and settlement work.</param>
    /// <returns>An HTTP result suitable for the provider callback or customer return request.</returns>
    /// <remarks>
    /// A fresh factory-created users.db context avoids the singleton-context race previously observed in the polling
    /// worker. The incoming HTTP request supplies no trusted financial data and cannot bypass official verification.
    /// </remarks>
    /// <example>
    /// <code>
    /// return await HandleUniquePayTriggerAsync(hashId, "provider-callback", cancellationToken);
    /// </code>
    /// </example>
    private async Task<IActionResult> HandleUniquePayTriggerAsync(
        string hashId,
        string source,
        CancellationToken cancellationToken)
    {
        await UniquePayTriggerLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = _userDbContextFactory.CreateDbContext();
            var payment = await context.UniquePayPaymentInfos
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.HashId == hashId, cancellationToken);
            if (payment == null)
                return NotFound(new { status = false, message = "payment not found" });

            var settlement = await _uniquePayReconciliation.ReconcileProviderTriggerAsync(
                payment.Id,
                source,
                cancellationToken);
            return Ok(new
            {
                status = true,
                hashId = payment.HashId,
                settled = settlement.Status is NowPaymentsSettlementStatus.Applied or NowPaymentsSettlementStatus.AlreadyAdded,
                result = settlement.Status.ToString()
            });
        }
        catch (Exception ex) when (ex is UniquePayApiException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "UniquePay HTTP trigger inquiry failed. source={Source}, hashId={HashId}", source, hashId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { status = false, message = "provider inquiry unavailable" });
        }
        finally
        {
            UniquePayTriggerLock.Release();
        }
    }

    /// <summary>
    /// Health-check endpoint for the NOWPayments IPN route.
    /// </summary>
    /// <returns>HTTP 200 with endpoint diagnostics.</returns>
    [HttpGet]
    public IActionResult Probe()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var cfConnectingIp = Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        var userAgent = Request.Headers.UserAgent.FirstOrDefault();

        Console.WriteLine(
            $"[NOWPayments IPN Probe] GET reached app. remoteIp={remoteIp}, xForwardedFor={forwardedFor}, cfConnectingIp={cfConnectingIp}, userAgent={userAgent}");

        return Ok(new
        {
            status = "online",
            endpoint = "nowpayments-ipn",
            expectedMethod = "POST",
            message = "NOWPayments IPN endpoint is online. Send POST callbacks to this URL."
        });
    }

    /// <summary>
    /// Receives, verifies, stores, and settles NOWPayments IPN callbacks.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for body reading, database work, and settlement.</param>
    /// <returns>
    /// HTTP 200 when the callback is accepted, 401 for invalid signatures, 400 for invalid JSON,
    /// or 404 when no local payment row can be matched.
    /// </returns>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        var signature = Request.Headers["x-nowpayments-sig"].FirstOrDefault();
        LogIpnResult(
            requestId,
            stage: "received",
            statusCode: null,
            result: "NOWPayments IPN callback received.",
            body: body,
            signatureIsValid: null);

        bool isValidSignature;
        try
        {
            isValidSignature = NowPaymentsIpnSignature.Verify(body, signature, _appConfig.IpnSecretKey);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid NOWPayments IPN JSON while checking signature.");
            LogIpnResult(
                requestId,
                stage: "invalid-json-signature-check",
                statusCode: StatusCodes.Status400BadRequest,
                result: "Invalid IPN JSON while checking signature.",
                body: body,
                signatureIsValid: false,
                extra: ex.Message);
            return BadRequest(new { message = "Invalid IPN payload." });
        }

        if (!isValidSignature)
        {
            LogIpnResult(
                requestId,
                stage: "signature-invalid",
                statusCode: StatusCodes.Status401Unauthorized,
                result: "Invalid NOWPayments signature.",
                body: body,
                signatureIsValid: false);
            return Unauthorized(new { message = "Invalid NOWPayments signature." });
        }

        NowPaymentsIpn ipn;
        try
        {
            ipn = JsonConvert.DeserializeObject<NowPaymentsIpn>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid NOWPayments IPN payload.");
            LogIpnResult(
                requestId,
                stage: "invalid-json-payload",
                statusCode: StatusCodes.Status400BadRequest,
                result: "Invalid IPN payload JSON.",
                body: body,
                signatureIsValid: true,
                extra: ex.Message);
            return BadRequest(new { message = "Invalid IPN payload." });
        }

        if (ipn == null)
        {
            LogIpnResult(
                requestId,
                stage: "empty-payload",
                statusCode: StatusCodes.Status400BadRequest,
                result: "Empty IPN payload.",
                body: body,
                signatureIsValid: true);
            return BadRequest(new { message = "Empty IPN payload." });
        }

        LogIpnResult(
            requestId,
            stage: "signature-valid",
            statusCode: null,
            result: "IPN signature is valid.",
            ipn: ipn,
            body: body,
            signatureIsValid: true);

        await IpnLock.WaitAsync(cancellationToken);
        try
        {
            var payment = await FindPaymentAsync(ipn, cancellationToken);
            if (payment == null)
            {
                await LogPaymentLookupMissAsync(ipn, cancellationToken);

                _logger.LogWarning(
                    "NOWPayments IPN received for unknown order. OrderId={OrderId}, PaymentId={PaymentId}",
                    ipn.order_id,
                    ipn.payment_id);

                LogIpnResult(
                    requestId,
                    stage: "payment-not-found",
                    statusCode: StatusCodes.Status404NotFound,
                    result: "Payment was not found in local database.",
                    ipn: ipn,
                    body: body,
                    signatureIsValid: true);
                return NotFound(new { message = "Payment was not found." });
            }

            ApplyIpnToPayment(payment, ipn);
            payment.RawIpnJson = body;
            await _userDbcontext.SaveChangesAsync(cancellationToken);
            LogIpnResult(
                requestId,
                stage: "payment-updated",
                statusCode: null,
                result: "IPN data applied to local payment record.",
                ipn: ipn,
                payment: payment,
                body: body,
                signatureIsValid: true);

            if (!NowPaymentsStatuses.IsPaid(ipn.payment_status))
            {
                LogIpnResult(
                    requestId,
                    stage: "accepted-not-paid",
                    statusCode: StatusCodes.Status200OK,
                    result: "IPN accepted; payment is not finished yet.",
                    ipn: ipn,
                    payment: payment,
                    body: body,
                    signatureIsValid: true);
                return Ok(new
                {
                    message = "IPN accepted.",
                    status = ipn.payment_status,
                    orderId = payment.OrderId
                });
            }

            var settlement = string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase)
                ? await _tenantBotService.ApplyPaidTenantOrderAsync(
                    payment,
                    "ipn",
                    CancellationToken: cancellationToken)
                : await _settlementService.ApplyFinishedPaymentAsync(
                    payment,
                    "ipn",
                    cancellationToken: cancellationToken);

            LogIpnResult(
                requestId,
                stage: "accepted-paid",
                statusCode: StatusCodes.Status200OK,
                result: "IPN accepted; finished payment settlement processed.",
                ipn: ipn,
                payment: payment,
                settlement: settlement,
                body: body,
                signatureIsValid: true);
            return Ok(new
            {
                message = "IPN accepted.",
                status = ipn.payment_status,
                settlement = settlement.Status.ToString(),
                orderId = payment.OrderId
            });
        }
        finally
        {
            IpnLock.Release();
        }
    }

    /// <summary>
    /// Health-check endpoint for the HooshPay IPN route.
    /// </summary>
    /// <returns>HTTP 200 with endpoint diagnostics.</returns>
    [HttpGet("/hooshpay-ipn")]
    public IActionResult ProbeHooshPay()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var cfConnectingIp = Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        var userAgent = Request.Headers.UserAgent.FirstOrDefault();

        Console.WriteLine(
            $"[HooshPay IPN Probe] GET reached app. remoteIp={remoteIp}, xForwardedFor={forwardedFor}, cfConnectingIp={cfConnectingIp}, userAgent={userAgent}");

        return Ok(new
        {
            status = "online",
            endpoint = "hooshpay-ipn",
            expectedMethod = "POST",
            message = "HooshPay IPN endpoint is online. Send POST callbacks to this URL."
        });
    }

    /// <summary>
    /// Receives, verifies, stores, and settles HooshPay IPN callbacks.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for body reading, database work, and settlement.</param>
    /// <returns>
    /// HTTP 200 when the callback is accepted, 401 for invalid signatures, 400 for invalid JSON,
    /// or 404 when no local payment row can be matched.
    /// </returns>
    [HttpPost("/hooshpay-ipn")]
    public async Task<IActionResult> ReceiveHooshPay(CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var signature = Request.Headers["X-HooshPay-Signature"].FirstOrDefault();

        LogHooshPayIpnResult(
            requestId,
            stage: "received",
            statusCode: null,
            result: "HooshPay IPN callback received.",
            body: body,
            signatureIsValid: null);

        bool isValidSignature;
        try
        {
            isValidSignature = HooshPaySignature.Verify(body, signature, _appConfig.HooshPayIpnSecretKey);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid HooshPay IPN JSON while checking signature.");
            LogHooshPayIpnResult(
                requestId,
                stage: "invalid-json-signature-check",
                statusCode: StatusCodes.Status400BadRequest,
                result: "Invalid IPN JSON while checking signature.",
                body: body,
                signatureIsValid: false,
                extra: ex.Message);
            return BadRequest(new { message = "Invalid IPN payload." });
        }

        if (!isValidSignature)
        {
            LogHooshPayIpnResult(
                requestId,
                stage: "signature-invalid",
                statusCode: StatusCodes.Status401Unauthorized,
                result: "Invalid HooshPay signature.",
                body: body,
                signatureIsValid: false);
            return Unauthorized(new { message = "Invalid HooshPay signature." });
        }

        HooshPayIpn ipn;
        try
        {
            ipn = JsonConvert.DeserializeObject<HooshPayIpn>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid HooshPay IPN payload.");
            LogHooshPayIpnResult(
                requestId,
                stage: "invalid-json-payload",
                statusCode: StatusCodes.Status400BadRequest,
                result: "Invalid IPN payload JSON.",
                body: body,
                signatureIsValid: true,
                extra: ex.Message);
            return BadRequest(new { message = "Invalid IPN payload." });
        }

        if (ipn == null)
        {
            LogHooshPayIpnResult(
                requestId,
                stage: "empty-payload",
                statusCode: StatusCodes.Status400BadRequest,
                result: "Empty IPN payload.",
                body: body,
                signatureIsValid: true);
            return BadRequest(new { message = "Empty IPN payload." });
        }

        await HooshPayIpnLock.WaitAsync(cancellationToken);
        try
        {
            var payment = await FindHooshPayPaymentAsync(ipn, cancellationToken);
            if (payment == null)
            {
                await LogHooshPayPaymentLookupMissAsync(ipn, cancellationToken);
                LogHooshPayIpnResult(
                    requestId,
                    stage: "payment-not-found",
                    statusCode: StatusCodes.Status404NotFound,
                    result: "Payment was not found in local database.",
                    ipn: ipn,
                    body: body,
                    signatureIsValid: true);
                return NotFound(new { message = "Payment was not found." });
            }

            payment.Apply(ipn);
            payment.RawIpnJson = body;
            await _userDbcontext.SaveChangesAsync(cancellationToken);

            LogHooshPayIpnResult(
                requestId,
                stage: "payment-updated",
                statusCode: null,
                result: "IPN data applied to local payment record.",
                ipn: ipn,
                payment: payment,
                body: body,
                signatureIsValid: true);

            if (!HooshPayStatuses.IsPaid(ipn.status))
            {
                LogHooshPayIpnResult(
                    requestId,
                    stage: "accepted-not-paid",
                    statusCode: StatusCodes.Status200OK,
                    result: "IPN accepted; payment is not paid yet.",
                    ipn: ipn,
                    payment: payment,
                    body: body,
                    signatureIsValid: true);
                return Ok(new
                {
                    message = "IPN accepted.",
                    status = ipn.status,
                    orderId = payment.OrderId
                });
            }

            // Tenant orders create accounts and credit owners; wallet charges only add user balance.
            var isTenantOrder = string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.TenantOrder, StringComparison.OrdinalIgnoreCase);
            var provisionalReconciled = !isTenantOrder && await _hooshPaySettlementService
                .RecordProviderConfirmationAfterProvisionalAsync(payment, "ipn", cancellationToken);
            var settlement = isTenantOrder
                ? await _tenantBotService.ApplyPaidTenantOrderAsync(
                    payment,
                    "ipn",
                    CancellationToken: cancellationToken)
                : await _hooshPaySettlementService.ApplyFinishedPaymentAsync(
                    payment,
                    "ipn",
                    cancellationToken: cancellationToken);

            LogHooshPayIpnResult(
                requestId,
                stage: "accepted-paid",
                statusCode: StatusCodes.Status200OK,
                result: provisionalReconciled
                    ? "IPN accepted; provisional wallet credit officially confirmed without a second balance mutation."
                    : "IPN accepted; paid payment settlement processed.",
                ipn: ipn,
                payment: payment,
                settlement: settlement,
                body: body,
                signatureIsValid: true);
            return Ok(new
            {
                message = "IPN accepted.",
                status = ipn.status,
                settlement = settlement.Status.ToString(),
                orderId = payment.OrderId
            });
        }
        finally
        {
            HooshPayIpnLock.Release();
        }
    }

    /// <summary>
    /// Writes structured console diagnostics for each stage of NOWPayments IPN processing.
    /// </summary>
    /// <param name="requestId">Short per-request id used to correlate log lines.</param>
    /// <param name="stage">Processing stage name.</param>
    /// <param name="statusCode">HTTP status that will be returned, when known.</param>
    /// <param name="result">Human-readable processing result.</param>
    /// <param name="ipn">Parsed IPN payload, when available.</param>
    /// <param name="payment">Matched local payment row, when available.</param>
    /// <param name="settlement">Settlement result, when settlement has run.</param>
    /// <param name="body">Raw callback body.</param>
    /// <param name="signatureIsValid">Signature validation state.</param>
    /// <param name="extra">Optional diagnostic detail.</param>
    private void LogIpnResult(
        string requestId,
        string stage,
        int? statusCode,
        string result,
        NowPaymentsIpn ipn = null,
        SwapinoPaymentInfo payment = null,
        NowPaymentsSettlementResult settlement = null,
        string body = null,
        bool? signatureIsValid = null,
        string extra = null)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "";
        var cfConnectingIp = Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? "";
        var userAgent = Request.Headers["User-Agent"].FirstOrDefault() ?? "";
        var signature = Request.Headers["x-nowpayments-sig"].FirstOrDefault();
        var signatureState = signatureIsValid.HasValue
            ? (signatureIsValid.Value ? "valid" : "invalid")
            : "not-checked";
        var secretState = string.IsNullOrWhiteSpace(_appConfig?.IpnSecretKey)
            ? "missing"
            : "configured";

        Console.WriteLine("========== NOWPayments IPN ==========");
        Console.WriteLine($"[NOWPayments IPN] requestId={requestId}, stage={stage}, result={result}");
        Console.WriteLine($"[NOWPayments IPN] responseStatus={(statusCode.HasValue ? statusCode.Value.ToString() : "pending")}");
        Console.WriteLine($"[NOWPayments IPN] remoteIp={remoteIp}, xForwardedFor={forwardedFor}, cfConnectingIp={cfConnectingIp}, userAgent={userAgent}");
        Console.WriteLine($"[NOWPayments IPN] signatureHeader={(string.IsNullOrWhiteSpace(signature) ? "missing" : "present")}, signature={signatureState}, ipnSecret={secretState}");
        Console.WriteLine($"[NOWPayments IPN] orderId={ipn?.order_id ?? payment?.OrderId ?? ""}, paymentId={ipn?.payment_id ?? payment?.PaymentId ?? ""}, invoiceId={ipn?.invoice_id ?? payment?.InvoiceId ?? ""}, status={ipn?.payment_status ?? payment?.PaymentStatus ?? ""}");
        Console.WriteLine($"[NOWPayments IPN] localPayment={(payment == null ? "not-found" : $"found:id={payment.Id}, user={payment.TelegramUserId}, added={payment.IsAddedToBalance}")}");
        Console.WriteLine($"[NOWPayments IPN] settlement={(settlement == null ? "not-applied" : settlement.Status.ToString())}");
        if (!string.IsNullOrWhiteSpace(extra))
            Console.WriteLine($"[NOWPayments IPN] extra={extra}");
        Console.WriteLine($"[NOWPayments IPN] rawBody={body ?? ""}");
        Console.WriteLine("=====================================");
    }

    /// <summary>
    /// Writes structured console diagnostics for each stage of HooshPay IPN processing.
    /// </summary>
    /// <param name="requestId">Short per-request id used to correlate log lines.</param>
    /// <param name="stage">Processing stage name.</param>
    /// <param name="statusCode">HTTP status that will be returned, when known.</param>
    /// <param name="result">Human-readable processing result.</param>
    /// <param name="ipn">Parsed HooshPay IPN payload, when available.</param>
    /// <param name="payment">Matched local HooshPay payment row, when available.</param>
    /// <param name="settlement">Settlement result, when settlement has run.</param>
    /// <param name="body">Raw callback body.</param>
    /// <param name="signatureIsValid">Signature validation state.</param>
    /// <param name="extra">Optional diagnostic detail.</param>
    private void LogHooshPayIpnResult(
        string requestId,
        string stage,
        int? statusCode,
        string result,
        HooshPayIpn ipn = null,
        HooshPayPaymentInfo payment = null,
        NowPaymentsSettlementResult settlement = null,
        string body = null,
        bool? signatureIsValid = null,
        string extra = null)
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "";
        var cfConnectingIp = Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? "";
        var userAgent = Request.Headers["User-Agent"].FirstOrDefault() ?? "";
        var signature = Request.Headers["X-HooshPay-Signature"].FirstOrDefault();
        var signatureState = signatureIsValid.HasValue
            ? (signatureIsValid.Value ? "valid" : "invalid")
            : "not-checked";
        var secretState = string.IsNullOrWhiteSpace(_appConfig?.HooshPayIpnSecretKey)
            ? "missing"
            : "configured";

        Console.WriteLine("========== HooshPay IPN ==========");
        Console.WriteLine($"[HooshPay IPN] requestId={requestId}, stage={stage}, result={result}");
        Console.WriteLine($"[HooshPay IPN] responseStatus={(statusCode.HasValue ? statusCode.Value.ToString() : "pending")}");
        Console.WriteLine($"[HooshPay IPN] remoteIp={remoteIp}, xForwardedFor={forwardedFor}, cfConnectingIp={cfConnectingIp}, userAgent={userAgent}");
        Console.WriteLine($"[HooshPay IPN] signatureHeader={(string.IsNullOrWhiteSpace(signature) ? "missing" : "present")}, signature={signatureState}, ipnSecret={secretState}");
        Console.WriteLine($"[HooshPay IPN] orderId={ipn?.order_id ?? payment?.OrderId ?? ""}, invoice={ipn?.invoice ?? payment?.InvoiceUid ?? ""}, status={ipn?.status ?? payment?.PaymentStatus ?? ""}, trackingCode={ipn?.tracking_code ?? payment?.TrackingCode ?? ""}");
        Console.WriteLine($"[HooshPay IPN] localPayment={(payment == null ? "not-found" : $"found:id={payment.Id}, user={payment.TelegramUserId}, added={payment.IsAddedToBalance}")}");
        Console.WriteLine($"[HooshPay IPN] settlement={(settlement == null ? "not-applied" : settlement.Status.ToString())}");
        if (!string.IsNullOrWhiteSpace(extra))
            Console.WriteLine($"[HooshPay IPN] extra={extra}");
        Console.WriteLine($"[HooshPay IPN] rawBody={body ?? ""}");
        Console.WriteLine("===================================");
    }

    /// <summary>
    /// Finds the local HooshPay payment row for a webhook by order id or invoice uid.
    /// </summary>
    /// <param name="ipn">Verified HooshPay IPN payload.</param>
    /// <param name="cancellationToken">Cancellation token for the database lookup.</param>
    /// <returns>Matched payment row, or null when the callback cannot be matched.</returns>
    private async Task<HooshPayPaymentInfo> FindHooshPayPaymentAsync(HooshPayIpn ipn, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ipn.order_id))
        {
            var byOrderId = await _userDbcontext.HooshPayPaymentInfos
                .FirstOrDefaultAsync(p => p.OrderId == ipn.order_id, cancellationToken);
            if (byOrderId != null)
                return byOrderId;
        }

        if (!string.IsNullOrWhiteSpace(ipn.invoice))
        {
            var byInvoiceUid = await _userDbcontext.HooshPayPaymentInfos
                .FirstOrDefaultAsync(p => p.InvoiceUid == ipn.invoice, cancellationToken);
            if (byInvoiceUid != null)
                return byInvoiceUid;
        }

        return null;
    }

    /// <summary>
    /// Logs extra HooshPay database diagnostics when an IPN cannot be matched to a local payment row.
    /// </summary>
    /// <param name="ipn">Incoming IPN that failed lookup.</param>
    /// <param name="cancellationToken">Cancellation token for diagnostic database queries.</param>
    private async Task LogHooshPayPaymentLookupMissAsync(HooshPayIpn ipn, CancellationToken cancellationToken)
    {
        var totalCount = await _userDbcontext.HooshPayPaymentInfos.CountAsync(cancellationToken);
        var latestPayments = await _userDbcontext.HooshPayPaymentInfos
            .OrderByDescending(p => p.Id)
            .Take(5)
            .Select(p => new
            {
                p.Id,
                p.OrderId,
                p.InvoiceUid,
                p.TelegramUserId,
                p.CreatedAtUtc,
                p.PaymentStatus
            })
            .ToListAsync(cancellationToken);

        Console.WriteLine(
            $"[HooshPay IPN] lookup miss diagnostics: db=./Data/users.db, totalPayments={totalCount}, incomingOrderId={ipn?.order_id}, incomingInvoice={ipn?.invoice}");

        foreach (var payment in latestPayments)
        {
            Console.WriteLine(
                $"[HooshPay IPN] recent local payment: id={payment.Id}, orderId={payment.OrderId}, invoiceUid={payment.InvoiceUid}, user={payment.TelegramUserId}, createdAtUtc={payment.CreatedAtUtc:O}, status={payment.PaymentStatus}");
        }
    }

    /// <summary>
    /// Finds the local NOWPayments payment row by order id, invoice id, payment id, or legacy JSON metadata.
    /// </summary>
    /// <param name="ipn">Verified NOWPayments IPN payload.</param>
    /// <param name="cancellationToken">Cancellation token for database lookup.</param>
    /// <returns>Matched payment row, or null when the callback cannot be matched.</returns>
    private async Task<SwapinoPaymentInfo> FindPaymentAsync(NowPaymentsIpn ipn, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ipn.order_id))
        {
            var byOrderId = await _userDbcontext.SwapinoPaymentInfos
                .FirstOrDefaultAsync(p => p.OrderId == ipn.order_id, cancellationToken);
            if (byOrderId != null)
                return byOrderId;
        }

        if (!string.IsNullOrWhiteSpace(ipn.invoice_id))
        {
            var byInvoiceId = await _userDbcontext.SwapinoPaymentInfos
                .FirstOrDefaultAsync(p => p.InvoiceId == ipn.invoice_id, cancellationToken);
            if (byInvoiceId != null)
                return byInvoiceId;
        }

        if (!string.IsNullOrWhiteSpace(ipn.payment_id))
        {
            var byPaymentId = await _userDbcontext.SwapinoPaymentInfos
                .FirstOrDefaultAsync(p => p.PaymentId == ipn.payment_id, cancellationToken);
            if (byPaymentId != null)
                return byPaymentId;
        }

        var recentPayments = await _userDbcontext.SwapinoPaymentInfos
            .OrderByDescending(p => p.Id)
            .Take(200)
            .ToListAsync(cancellationToken);

        return recentPayments.FirstOrDefault(p =>
        {
            var data = p.GetNowPaymentsData();
            return (!string.IsNullOrWhiteSpace(ipn.payment_id) &&
                    string.Equals(data.PaymentId, ipn.payment_id, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(ipn.invoice_id) &&
                    string.Equals(data.InvoiceId, ipn.invoice_id, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(ipn.order_id) &&
                    string.Equals(data.OrderId, ipn.order_id, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// Logs extra NOWPayments database diagnostics when an IPN cannot be matched to a local payment row.
    /// </summary>
    /// <param name="ipn">Incoming IPN that failed lookup.</param>
    /// <param name="cancellationToken">Cancellation token for diagnostic database queries.</param>
    private async Task LogPaymentLookupMissAsync(NowPaymentsIpn ipn, CancellationToken cancellationToken)
    {
        var totalCount = await _userDbcontext.SwapinoPaymentInfos.CountAsync(cancellationToken);
        var latestPayments = await _userDbcontext.SwapinoPaymentInfos
            .OrderByDescending(p => p.Id)
            .Take(5)
            .Select(p => new
            {
                p.Id,
                p.OrderId,
                p.InvoiceId,
                p.PaymentId,
                p.TelegramUserId,
                p.CreatedAtUtc,
                p.PaymentStatus
            })
            .ToListAsync(cancellationToken);

        Console.WriteLine(
            $"[NOWPayments IPN] lookup miss diagnostics: db=./Data/users.db, totalPayments={totalCount}, incomingOrderId={ipn?.order_id}, incomingInvoiceId={ipn?.invoice_id}, incomingPaymentId={ipn?.payment_id}");

        foreach (var payment in latestPayments)
        {
            Console.WriteLine(
                $"[NOWPayments IPN] recent local payment: id={payment.Id}, orderId={payment.OrderId}, invoiceId={payment.InvoiceId}, paymentId={payment.PaymentId}, user={payment.TelegramUserId}, createdAtUtc={payment.CreatedAtUtc:O}, status={payment.PaymentStatus}");
        }
    }

    /// <summary>
    /// Copies NOWPayments IPN fields into both the normalized columns and the legacy JSON payload on the payment row.
    /// </summary>
    /// <param name="payment">Local payment row to update.</param>
    /// <param name="ipn">Verified NOWPayments IPN payload.</param>
    private static void ApplyIpnToPayment(SwapinoPaymentInfo payment, NowPaymentsIpn ipn)
    {
        var data = payment.GetNowPaymentsData();
        data.Apply(ipn);
        payment.SetNowPaymentsData(data);

        payment.IpnCallbackUrl ??= data.InvoiceUrl;
        payment.BaseAmount = data.PriceAmount == 0 ? payment.BaseAmount : data.PriceAmount;
        payment.OutcomeAmount = data.PayAmount == 0 ? payment.OutcomeAmount : data.PayAmount;
        payment.PaymentStatus = data.PaymentStatus ?? payment.PaymentStatus;
        payment.PaymentId = data.PaymentId ?? payment.PaymentId;
        payment.InvoiceId = data.InvoiceId ?? payment.InvoiceId;
        payment.InvoiceUrl = data.InvoiceUrl ?? payment.InvoiceUrl;
        payment.PayAddress = data.PayAddress ?? payment.PayAddress;
        payment.PayCurrency = data.PayCurrency ?? payment.PayCurrency;
        payment.PayinHash = data.PayinHash ?? payment.PayinHash;
        payment.PayoutHash = data.PayoutHash ?? payment.PayoutHash;
        payment.UpdatedAtUtc = DateTime.UtcNow;
    }
}
