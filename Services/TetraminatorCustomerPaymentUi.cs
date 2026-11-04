using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Shared owned/tenant invoice choices; both URLs belong to one invoice and never authorize settlement.</summary>
public static class TetraminatorCustomerPaymentUi
{
    /// <summary>Customer guidance used on first delivery and redisplay, without claiming either route is another invoice.</summary>
    public const string Guidance = "برای پرداخت این فاکتور، فقط از یکی از لینک‌های زیر استفاده کنید و از پرداخت مجدد همان فاکتور خودداری کنید.";

    /// <summary>Safe display-only routes transported by tenant wallet invoice results, without exposing raw provider/payment data.</summary>
    /// <param name="PaymentLink">Optional bot payment_link, displayed first; do not log.</param>
    /// <param name="GatewayLink">Optional online gateway_link for the same invoice, displayed second; do not log.</param>
    public sealed record Links(string PaymentLink, string GatewayLink);

    /// <summary>Builds bot-then-online payment buttons followed by existing inquiry and optional tenant return controls.</summary>
    /// <param name="paymentLink">Optional provider bot URL; missing/blank omits this button.</param>
    /// <param name="gatewayLink">Optional online gateway URL; missing/blank omits this button.</param>
    /// <param name="checkCallback">Existing owned/tenant ownership-checked callback, never an authorization grant.</param>
    /// <param name="returnCallback">Optional existing storefront return callback; owned flows omit it.</param>
    /// <returns>Keyboard preserving both routes in provider field order, including when they happen to be equal.</returns>
    /// <remarks>No database/network/state/financial effects. Reuse saved URLs after inquiry/restart; never create another invoice for a missing route.</remarks>
    /// <example><code>var keys = TetraminatorCustomerPaymentUi.BuildKeyboard(payment.PaymentLink, payment.GatewayLink, $"tmchk_{payment.Id}");</code></example>
    public static InlineKeyboardMarkup BuildKeyboard(string paymentLink, string gatewayLink, string checkCallback, string returnCallback = null)
    {
        var rows = new List<InlineKeyboardButton[]>();
        if (!string.IsNullOrWhiteSpace(paymentLink)) rows.Add(new[] { InlineKeyboardButton.WithUrl("🤖 پرداخت از طریق ربات", paymentLink.Trim()) });
        if (!string.IsNullOrWhiteSpace(gatewayLink)) rows.Add(new[] { InlineKeyboardButton.WithUrl("🌐 پرداخت از طریق درگاه آنلاین", gatewayLink.Trim()) });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بررسی وضعیت پرداخت", checkCallback) });
        if (!string.IsNullOrWhiteSpace(returnCallback)) rows.Add(new[] { InlineKeyboardButton.WithCallbackData("بازگشت به فروشگاه", returnCallback) });
        return new InlineKeyboardMarkup(rows);
    }
}
