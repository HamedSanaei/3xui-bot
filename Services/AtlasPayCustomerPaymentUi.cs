using System.Globalization;
using System.Net;
using Adminbot.Domain;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Builds customer-facing AtlasPay payment instructions without persisting full merchant card details.</summary>
public static class AtlasPayCustomerPaymentUi
{
    private static readonly TimeZoneInfo TehranTimeZone = ResolveTehranTimeZone();

    /// <summary>Ephemeral direct-payment values returned only by AtlasPay order creation.</summary>
    public sealed record DirectPayment(
        string CardNumber,
        string CardHolderName,
        string BankName,
        long TotalAmountToman,
        DateTimeOffset PaymentDeadlineAt,
        string TrackingCode);

    /// <summary>Extracts direct card details only when AtlasPay supplied a full card number.</summary>
    public static DirectPayment TryCreateDirectPayment(AtlasPayCreateOrderResponse response)
    {
        if (response == null || string.IsNullOrWhiteSpace(response.CardNumber))
            return null;

        var digits = new string(response.CardNumber.Where(char.IsDigit).ToArray());
        if (digits.Length != 16)
            return null;

        return new DirectPayment(
            digits,
            response.CardHolderName?.Trim(),
            response.BankName?.Trim(),
            response.TotalAmountToman,
            response.PaymentDeadlineAt,
            response.TrackingCode?.Trim());
    }

    /// <summary>Builds the direct card-to-card message using the exact provider total, never the requested base amount.</summary>
    public static string BuildDirectPaymentText(DirectPayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var toman = payment.TotalAmountToman.ToString("0", CultureInfo.InvariantCulture);
        var rial = (payment.TotalAmountToman * 10m).ToString("0", CultureInfo.InvariantCulture);
        var deadline = FormatTehranShamsiDeadline(payment.PaymentDeadlineAt);
        var lines = new List<string>
        {
            "💳 <b>اطلاعات پرداخت مستقیم اطلس‌پی</b>",
            "",
            $"💰 مبلغ دقیق: <code>{Html(toman)} تومان</code>",
            $"💵 معادل بانکی: <code>{Html(rial)} ریال</code>",
            $"💳 شماره‌کارت: <code>{Html(payment.CardNumber)}</code>"
        };

        if (!string.IsNullOrWhiteSpace(payment.CardHolderName))
            lines.Add($"👤 به نام: <b>{Html(payment.CardHolderName)}</b>");
        if (!string.IsNullOrWhiteSpace(payment.BankName))
            lines.Add($"🏦 بانک: <b>{Html(payment.BankName)}</b>");

        lines.Add($"⏱ مهلت پرداخت: <code>{Html(deadline)}</code>");
        if (!string.IsNullOrWhiteSpace(payment.TrackingCode))
            lines.Add($"🔖 شماره پیگیری: <code>{Html(payment.TrackingCode)}</code>");

        lines.Add("");
        lines.Add("🚨 <b>مهم: مبلغ را دقیقاً و ریال‌به‌ریال واریز کنید.</b>");
        lines.Add($"اگر اپ بانکی مبلغ را به ریال می‌گیرد، دقیقاً <code>{Html(rial)} ریال</code> وارد کنید.");
        lines.Add("حتی <b>۱ تومان</b> کمتر یا بیشتر، رند کردن مبلغ یا تغییر رقم آخر می‌تواند تأیید خودکار را با مشکل مواجه کند.");
        lines.Add("این مبلغ نهایی است و کارمزد اطلس‌پی داخل آن محاسبه شده؛ چیزی به آن اضافه یا از آن کم نکنید.");
        lines.Add("");
        lines.Add("✅ پس از واریز حدود ۲ دقیقه برای تأیید خودکار صبر کنید.");
        lines.Add("اگر تأیید خودکار انجام نشد، از دکمه «ارسال رسید / مشکل در تأیید» استفاده کنید.");

        return string.Join("\n", lines);
    }

    /// <summary>Builds the complete link-only fallback message when direct card display is not enabled for the merchant.</summary>
    public static string BuildLinkFallbackText(long totalAmountToman, DateTime? paymentDeadlineAtUtc, string trackingCode)
    {
        var deadline = paymentDeadlineAtUtc.HasValue
            ? FormatTehranShamsiDeadline(new DateTimeOffset(
                DateTime.SpecifyKind(paymentDeadlineAtUtc.Value, DateTimeKind.Utc)))
            : "نامشخص";
        var lines = new List<string>
        {
            "⚠️ <b>پیش از پرداخت لطفاً موارد زیر را با دقت بررسی کنید:</b>",
            "",
            BuildExactAmountWarning(totalAmountToman),
            $"⏱ مهلت پرداخت: <code>{Html(deadline)}</code>"
        };
        if (!string.IsNullOrWhiteSpace(trackingCode))
            lines.Add($"🔖 شماره پیگیری: <code>{Html(trackingCode.Trim())}</code>");
        lines.Add("");
        lines.Add("شماره‌کارت مقصد داخل صفحه اطلس‌پی نمایش داده می‌شود. پس از واریز، اگر تأیید خودکار انجام نشد همان صفحه برای ارسال رسید در دسترس است.");
        return string.Join("\n", lines);
    }

    /// <summary>Builds the exact provider-total warning for link-only fallback flows.</summary>
    public static string BuildExactAmountWarning(long totalAmountToman)
    {
        var toman = totalAmountToman.ToString("0", CultureInfo.InvariantCulture);
        var rial = (totalAmountToman * 10m).ToString("0", CultureInfo.InvariantCulture);
        return $"💰 مبلغ دقیق قابل پرداخت: <code>{Html(toman)} تومان</code>\n" +
               $"💵 مبلغ دقیق بانکی: <code>{Html(rial)} ریال</code>\n\n" +
               "🚨 <b>مبلغ را دقیقاً و ریال‌به‌ریال واریز کنید.</b>\n" +
               $"اگر اپ بانکی مبلغ را به ریال می‌گیرد، دقیقاً <code>{Html(rial)} ریال</code> وارد کنید.\n" +
               "حتی ۱ تومان اختلاف یا رند کردن مبلغ می‌تواند تأیید خودکار را با مشکل مواجه کند.";
    }

    /// <summary>Builds AtlasPay controls; direct-card mode demotes the mini-app link to receipt/recovery fallback.</summary>
    public static InlineKeyboardMarkup BuildKeyboard(string customerStartLink, string checkCallback, bool directCard)
    {
        var rows = new List<InlineKeyboardButton[]>();
        if (!string.IsNullOrWhiteSpace(customerStartLink))
        {
            rows.Add(new[]
            {
                InlineKeyboardButton.WithUrl(
                    directCard ? "🧾 ارسال رسید / مشکل در تأیید" : "💳 پرداخت با اطلس‌پی | کارمزد ۱۲٪ | ریالی",
                    customerStartLink)
            });
        }

        if (!string.IsNullOrWhiteSpace(checkCallback))
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔄 بررسی وضعیت پرداخت", checkCallback) });

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>Formats an AtlasPay UTC deadline as a Solar Hijri date in Tehran local time.</summary>
    public static string FormatTehranShamsiDeadline(DateTimeOffset deadline)
    {
        var tehran = TimeZoneInfo.ConvertTime(deadline, TehranTimeZone);
        var local = tehran.DateTime;
        var calendar = new PersianCalendar();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{calendar.GetYear(local):0000}/{calendar.GetMonth(local):00}/{calendar.GetDayOfMonth(local):00} - {tehran:HH:mm} (ساعت تهران)");
    }

    private static TimeZoneInfo ResolveTehranTimeZone()
    {
        foreach (var id in new[] { "Asia/Tehran", "Iran Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // Iran has used a fixed UTC+03:30 offset since abolishing DST in 2022.
        return TimeZoneInfo.CreateCustomTimeZone(
            "TehranFallback",
            TimeSpan.FromMinutes(210),
            "Tehran",
            "Tehran");
    }

    private static string Html(string value)
        => WebUtility.HtmlEncode(value ?? string.Empty);
}
