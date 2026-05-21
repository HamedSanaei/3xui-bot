
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Utils;
using Microsoft.AspNetCore.Mvc;


[ApiController]
[Route("nowpayments-ipn")]
public class PaymentController : ControllerBase
{
    private readonly UserDbContext _userDbcontext;
    private readonly CredentialsDbContext _credentialsDbContext;
    //private readonly IConfiguration config;
    private readonly AppConfig _appConfig;
    private readonly ILogger<TelegramLogger> _logger;

    public PaymentController(UserDbContext userDbContext, CredentialsDbContext credentialsDbContext, IConfiguration config, ILogger<TelegramLogger> logger)
    {
        this._userDbcontext = userDbContext;
        this._credentialsDbContext = credentialsDbContext;
        _appConfig = config.Get<AppConfig>();
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var signature = Request.Headers["x-nowpayments-sig"].FirstOrDefault();
        var secret = _appConfig.IpnSecretKey;

        if (string.IsNullOrEmpty(signature) ||
            !VerifySignature(body, signature, secret))
        {
            return Unauthorized();
        }

        var ipn = System.Text.Json.JsonSerializer.Deserialize<NowPaymentsIpn>(body);

        if (ipn == null)
            return BadRequest();

        // پیدا کردن پرداخت
        SwapinoPaymentInfo payment = null;

        if (!string.IsNullOrEmpty(ipn.order_id))
        {
            payment = _userDbcontext.SwapinoPaymentInfos
                .FirstOrDefault(p => p.Payment_Id.ToString() == ipn.order_id);
        }
        else
        {
            payment = _userDbcontext.SwapinoPaymentInfos
                .FirstOrDefault(p => p.Payment_Id.ToString() == ipn.payment_id.ToString());
        }


        payment.Payment_Status = ipn.payment_status;
        payment.Pay_Amount = ipn.pay_amount;
        payment.Price_Amount = ipn.price_amount;
        payment.Pay_Currency = ipn.pay_currency;
        payment.Actually_Paid = ipn.actually_paid;
        payment.Updated_At = DateTime.UtcNow;
        var findedUser = await _credentialsDbContext.GetUserStatusWithId(payment.TelegramUserId);
        long beforeBalance = findedUser.AccountBalance;


        if (ipn.payment_status == "finished")
        {
            if (payment.IsAddedToBallance == true) return Conflict(new
            {
                message = "Payment has already been added to balance."
            });

            //notify user
            await _credentialsDbContext.AddFund(payment.TelegramUserId, payment.RialAmount);
        }
        long afterBalance = await _credentialsDbContext.GetAccountBalance(payment.TelegramUserId);

        // فقط یکبار انجام بده
        payment.IsAddedToBallance = true;
        payment.IpN_Processed = true;

        await _userDbcontext.SaveChangesAsync();

        var start = "درگاه پرداخت Nowpayments \n";
        var logMesseage = $"{start}یوزر <code>{payment.TelegramUserId}</code> \n {findedUser} \n به مبلغ {(payment.RialAmount / 10).FormatCurrency()}" + " حساب کاربری خود را شارژ کرد." + $"\n موجودی قبل از شارژ {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از شارژ {afterBalance.FormatCurrency()} \n";



        return Ok();
    }

    private bool VerifySignature(string requestBody, string signature, string secret)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(secret));

        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(requestBody));
        var computedSignature = BitConverter.ToString(hash)
            .Replace("-", "")
            .ToLower();

        return computedSignature == signature;
    }

    private async Task ActivateUserPlan(SwapinoPaymentInfo payment)
    {
        // if (!payment.IsAddedToBallance)
        // {

        //     await _credentialsDbContext.AddFund(payment.TelegramUserId, payment.RialAmount);
        //     _credentialsDbContext.SaveChanges();
        //     logMesseage = $"{start}یوزر <code>{payment.Order_Id}</code> \n {findedUser} \n به مبلغ {(zpi.Amount / 10).FormatCurrency()}" + " حساب کاربری خود را شارژ کرد." + $"\n موجودی قبل از شارژ {beforeBalance.FormatCurrency()}" + $"\n موجودی پس از شارژ {afterBalance.FormatCurrency()} \n" + msg;

        //     _logger.LogPayment(logMesseage);


        //     //change buttons!
        //     await EditMessageWithCallback(_botClient, zpi.ChatId, Convert.ToInt32(zpi.TelMsgId));

        // }
    }
}

