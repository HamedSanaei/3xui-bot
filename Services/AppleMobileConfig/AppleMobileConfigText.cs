namespace Adminbot.Services.AppleMobileConfig;

/// <summary>Persian Telegram copy for the iOS APN profile flow, kept separate from generation logic.</summary>
public static class AppleMobileConfigText
{
    public const string MenuCommand = "📱 تنظیم APN آیفون";
    public const string CustomApnButton = "✏️ APN سفارشی";
    public const string CancelButton = "لغو";
    public const string BackButton = "بازگشت";
    public const string ProtocolTitle = "نوع IP را انتخاب کنید:";
    public const string GenericError =
        "❌ ساخت پروفایل انجام نشد. لطفاً دوباره تلاش کنید یا APN را مجدداً وارد کنید.";
    public const string ExpiredStep =
        "این مرحله منقضی شده است. از منوی اصلی دوباره ساخت پروفایل APN را شروع کنید.";
    public const string Cancelled = "ساخت پروفایل APN لغو شد.";

    public const string Intro =
        "📱 ساخت پروفایل APN آیفون\n\n" +
        "پروفایل به‌صورت مستقیم داخل ربات ساخته می‌شود و اطلاعات شما به سرویس دیگری ارسال نمی‌شود.\n\n" +
        "در نسخه فعلی برای جلوگیری از حدس‌زدن تنظیمات اپراتور، APN را خودتان وارد کنید.";

    public const string ApnPrompt =
        "APN را وارد کنید:\n\n" +
        "مثال: اگر اپراتور شما APN مشخصی اعلام کرده، همان مقدار را دقیقاً ارسال کنید.";

    public const string InvalidApn =
        "❌ مقدار APN معتبر نیست. یک مقدار غیرخالی و کوتاه‌تر ارسال کنید.";

    public const string ExistingProfileNote =
        "ℹ️ اگر از قبل یک پروفایل APN/Cellular دیگری نصب شده و این پروفایل اثر نکرد، ممکن است لازم باشد پروفایل قبلی را بررسی یا جایگزین کنید.";

    public const string Ipv6DependencyNote =
        "⚠️ فعال بودن IPv6 همچنان نیازمند پشتیبانی IPv6 از طرف اپراتور، APN و شبکه است؛ این فایل فقط استفاده از پروتکل را مجاز می‌کند.";

    public static string BuildSuccess(string apn, IpProtocolMode protocol)
        => "✅ پروفایل APN آیفون ساخته شد.\n\n" +
           $"APN: {apn}\n" +
           $"IP Mode: {ProtocolLabel(protocol)}\n\n" +
           "فایل بالا را روی آیفون/آیپد دانلود و نصب کنید.\n\n" +
           "پس از دانلود:\n" +
           "Settings → Profile Downloaded\n" +
           "یا:\n" +
           "Settings → General → VPN & Device Management\n\n" +
           Ipv6DependencyNote + "\n\n" +
           ExistingProfileNote;

    public static string ProtocolLabel(IpProtocolMode protocol)
        => protocol switch
        {
            IpProtocolMode.IPv4 => "IPv4",
            IpProtocolMode.IPv6 => "IPv6",
            IpProtocolMode.IPv4AndIPv6 => "IPv4 + IPv6",
            _ => "Unknown"
        };

    public static string FileName(IpProtocolMode protocol)
        => protocol switch
        {
            IpProtocolMode.IPv4 => "iphone-apn-ipv4.mobileconfig",
            IpProtocolMode.IPv6 => "iphone-apn-ipv6.mobileconfig",
            _ => "iphone-apn-ipv4-ipv6.mobileconfig"
        };
}
