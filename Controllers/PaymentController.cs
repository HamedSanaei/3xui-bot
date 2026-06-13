using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

[ApiController]
[Route("nowpayments-ipn")]
public class PaymentController : ControllerBase
{
    private static readonly SemaphoreSlim IpnLock = new SemaphoreSlim(1, 1);

    private readonly UserDbContext _userDbcontext;
    private readonly AppConfig _appConfig;
    private readonly NowPaymentsSettlementService _settlementService;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(
        UserDbContext userDbContext,
        IConfiguration config,
        NowPaymentsSettlementService settlementService,
        ILogger<PaymentController> logger)
    {
        _userDbcontext = userDbContext;
        _appConfig = config.Get<AppConfig>();
        _settlementService = settlementService;
        _logger = logger;
    }

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

            var settlement = await _settlementService.ApplyFinishedPaymentAsync(
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

    private async Task<SwapinoPaymentInfo> FindPaymentAsync(NowPaymentsIpn ipn, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ipn.order_id))
        {
            var byOrderId = await _userDbcontext.SwapinoPaymentInfos
                .FirstOrDefaultAsync(p => p.OrderId == ipn.order_id, cancellationToken);
            if (byOrderId != null)
                return byOrderId;
        }

        if (string.IsNullOrWhiteSpace(ipn.payment_id))
            return null;

        var recentPayments = await _userDbcontext.SwapinoPaymentInfos
            .OrderByDescending(p => p.Id)
            .Take(200)
            .ToListAsync(cancellationToken);

        return recentPayments.FirstOrDefault(p =>
            string.Equals(p.GetNowPaymentsData().PaymentId, ipn.payment_id, StringComparison.OrdinalIgnoreCase));
    }

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
