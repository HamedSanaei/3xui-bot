namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Immutable secret-free operator explanation shared by endpoint controls and diagnostics.</summary>
/// <param name="Code">Controlled uppercase error identifier; never external response text.</param>
/// <param name="Stage">Fixed migration boundary, or unknown for historical records without stage evidence.</param>
/// <param name="Checked">Fixed Persian description of the checked category, without paths or identities.</param>
/// <param name="Message">Fixed Persian explanation safe for the main operator surface.</param>
/// <param name="Action">Fixed Persian corrective guidance; never a guessed mutation or replay instruction.</param>
public sealed record TelegramEndpointDiagnostic(string Code, string Stage, string Checked, string Message, string Action);

/// <summary>Closed migration vocabulary and fixed Persian explanations; old health categories retain unknown migration stage.</summary>
/// <remarks>Only names in this catalog may enter migration failure persistence. Raw errors, paths, tokens and payloads are never interpolated. Health classification and retry authority remain outside this presentation catalog.</remarks>
public static class TelegramEndpointDiagnosticCatalog
{
    /// <summary>Finite protocol probe boundaries; cross-products with typed failures remain closed.</summary>
    private static readonly string[] ProbeStages = ["local_root", "cloud_source_identity", "local_source_identity", "local_destination_identity", "cloud_destination_identity"];
    /// <summary>Finite safe typed probe outcomes, shared with the unchanged health classifier.</summary>
    private static readonly string[] ProbeFailures = ["connection_refused", "timeout", "network", "invalid_response", "rate_limited", "telegram_upstream", "token_rejected", "identity_mismatch", "configuration_missing"];
    /// <summary>Finite direct migration failures outside the safe identity/root probes.</summary>
    private static readonly string[] DirectFailures = ["local_file_mapping_missing", "local_file_mapping_invalid", "local_file_host_missing", "local_file_access_denied", "local_file_path_linked", "local_file_probe_failed", "cloud_logout_refused", "local_logout_refused", "cloud_logout_uncertain", "local_logout_uncertain", "source_lifecycle_unavailable", "source_stop_timeout", "source_stop_failed", "source_drain_timeout", "source_drain_failed", "local_destination_receiver_not_ready", "cloud_destination_receiver_not_ready", "local_destination_receiver_timeout", "cloud_destination_receiver_timeout", "local_destination_receiver_failed", "cloud_destination_receiver_failed", "source_restore_receiver_not_ready", "source_restore_receiver_timeout", "source_restore_receiver_failed"];

    /// <summary>Enumerates the fixed additional categories for the persistence allowlist.</summary>
    /// <returns>Only direct failures and finite boundary/failure combinations; no runtime input.</returns>
    internal static IEnumerable<string> MigrationFailureCategories => DirectFailures.Concat(ProbeStages.SelectMany(stage => ProbeFailures.Select(failure => stage + "_" + failure)));

    /// <summary>Qualifies a typed migration probe failure without changing health/failover classification.</summary>
    /// <param name="stage">One fixed probe boundary owned by the coordinator.</param>
    /// <param name="failure">Typed protocol failure, never an exception message.</param>
    /// <returns>A stage-distinguishable persisted category; throws for an invalid coordinator boundary.</returns>
    /// <exception cref="ArgumentException">The internal boundary is not in the closed catalog.</exception>
    /// <remarks>The stage-qualified category is diagnostic evidence only; the original typed failure remains the retry/failover authority.</remarks>
    /// <example><code>var category = TelegramEndpointDiagnosticCatalog.ProbeCategory("local_root", TelegramEndpointFailure.Timeout);</code></example>
    internal static string ProbeCategory(string stage, TelegramEndpointFailure failure)
    {
        if (!ProbeStages.Contains(stage, StringComparer.Ordinal)) throw new ArgumentException("Unknown migration probe stage.", nameof(stage));
        var category = TelegramEndpointHealthPolicy.Code(failure);
        if (!ProbeFailures.Contains(category, StringComparer.Ordinal)) category = "invalid_response";
        return stage + "_" + category;
    }

    /// <summary>Resolves a closed command/failure category into fixed secret-free operator guidance.</summary>
    /// <param name="category">Existing or current lowercase result/failure category; empty means no recorded error.</param>
    /// <param name="state">Optional detached state used only for known pending/cooldown and legacy logout endpoint evidence.</param>
    /// <returns>Null for accepted/unchanged or no important error; otherwise fixed code, stage, checked category, explanation and action. Unknown historical stages are never guessed.</returns>
    /// <remarks>Operation failure is never presented as migration success. A pending state is guidance, not proof of destination activation.</remarks>
    /// <example><code>var diagnostic = TelegramEndpointDiagnosticCatalog.Describe(state.LastFailureCategory, state);</code></example>
    public static TelegramEndpointDiagnostic Describe(string category, TelegramEndpointState state = null)
    {
        if (category is "accepted" or "unchanged") return null;
        if (string.IsNullOrEmpty(category))
        {
            if (state?.MigrationState == TelegramEndpointMigrationState.CloudWait) category = "cloud_cooldown";
            else if (state?.MigrationState is TelegramEndpointMigrationState.CheckingLocal or TelegramEndpointMigrationState.FallbackPending or TelegramEndpointMigrationState.SwitchingToLocal or TelegramEndpointMigrationState.SwitchingToCloud or TelegramEndpointMigrationState.CloudLogoutPending or TelegramEndpointMigrationState.LocalLogoutPending) category = "migration_in_progress";
            else return null;
        }
        var stage = "unknown";
        var failure = category;
        foreach (var candidate in ProbeStages)
            if (category.StartsWith(candidate + "_", StringComparison.Ordinal) && ProbeFailures.Contains(category[(candidate.Length + 1)..], StringComparer.Ordinal))
            { stage = candidate; failure = category[(candidate.Length + 1)..]; break; }
        if (DirectFailures.Contains(category, StringComparer.Ordinal))
        {
            stage = category.StartsWith("local_file_", StringComparison.Ordinal) ? "local_file_mapping" :
                category.StartsWith("cloud_logout_", StringComparison.Ordinal) ? "cloud_logout" :
                category.StartsWith("local_logout_", StringComparison.Ordinal) ? "local_logout" :
                category.StartsWith("source_restore_", StringComparison.Ordinal) ? "source_restore" :
                category.StartsWith("local_destination_", StringComparison.Ordinal) ? "local_destination_receiver" :
                category.StartsWith("cloud_destination_", StringComparison.Ordinal) ? "cloud_destination_receiver" :
                category.StartsWith("source_stop_", StringComparison.Ordinal) ? "source_stop" :
                category.StartsWith("source_drain_", StringComparison.Ordinal) ? "source_drain" : "source_lifecycle";
            if (category.EndsWith("logout_refused", StringComparison.Ordinal)) failure = "logout_refused";
            else if (category.EndsWith("logout_uncertain", StringComparison.Ordinal)) failure = "logout_uncertain";
            else if (category.Contains("receiver_", StringComparison.Ordinal)) failure = "receiver_not_ready";
            else if (category.StartsWith("source_", StringComparison.Ordinal)) failure = "drain_timeout";
        }
        if ((category is "logout_uncertain" or "logout_refused") && state?.LogoutEndpoint.HasValue == true)
            stage = state.LogoutEndpoint == TelegramEndpointType.Local ? "local_logout" : "cloud_logout";
        if (category is "migration_in_progress" or "busy" or "stale" or "denied" or "unavailable" or "disabled" or "unsafe" or "aliased" or "control_path_missing" or "registration_uncertain" or "not_submitted" or "retained_cloud_control" or "batch_busy") stage = "migration_admission";
        if (category == "cloud_cooldown") stage = "cloud_cooldown";
        var (message, action) = failure switch
        {
            "local_file_mapping_missing" => ("مسیرهای نگاشت فایل LOCAL تنظیم نشده‌اند.", "هر دو مسیر ریشهٔ سرور و پوشهٔ میزبان را در تنظیمات برنامه کامل کنید."),
            "local_file_mapping_invalid" => ("ریشهٔ نگاشت فایل LOCAL معتبر نیست.", "مسیرهای مطلق و بدون پیمایش والد یا نویسهٔ کنترلی تنظیم کنید."),
            "local_file_host_missing" => ("پوشهٔ میزبان نگاشت فایل LOCAL وجود ندارد.", "وجود همان پوشهٔ متصل به داده‌های LOCAL را بررسی کنید."),
            "local_file_access_denied" => ("برنامه اجازهٔ دسترسی و فهرست‌گیری پوشهٔ LOCAL را ندارد.", "مجوز جست‌وجوی مسیر و خواندن پوشه را برای کاربر سرویس اصلاح کنید."),
            "local_file_path_linked" => ("مسیر نگاشت فایل LOCAL شامل پیوند است.", "یک پوشهٔ واقعی و بدون پیوند یا نقطهٔ بازتحلیل تنظیم کنید."),
            "local_file_probe_failed" => ("بررسی خواندنی پوشهٔ LOCAL کامل نشد.", "دسترسی سیستم فایل و اتصال پوشهٔ موجود را بررسی کنید."),
            "connection_refused" or "network" => ("ارتباط با سرویس این مرحله برقرار نشد.", "دسترسی شبکه و فعال بودن سرویس را بررسی کنید؛ خروج از حساب تکرار نشود."),
            "timeout" => ("مهلت پاسخ سرویس این مرحله پایان یافت.", "دسترسی و پاسخ‌گویی سرویس را بررسی و وضعیت عملیات را دوباره مشاهده کنید."),
            "invalid_response" => ("پاسخ سرویس این مرحله معتبر نبود.", "سرویس رسمی و پاسخ آن را بررسی کنید؛ از پاسخ نامعتبر برای تغییر مسیر استفاده نکنید."),
            "identity_mismatch" => ("هویت ربات در این مرحله با هویت ثبت‌شده یکسان نیست.", "هویت و توکن همان ربات را بررسی کنید؛ انتقال با هویت دیگر مجاز نیست."),
            "token_rejected" => ("توکن ربات در این مرحله رد شد.", "اعتبار توکن همان ربات را بررسی کنید؛ توکن را در گزارش یا پیام منتشر نکنید."),
            "logout_refused" => ("خروج از نشست مبدا تایید نشد و انتقال انجام نشد.", "علت رد خروج را بررسی کنید؛ وضعیت مسیر مبدا را جداگانه مشاهده کنید."),
            "logout_uncertain" or "unsafe" or "unsafe_cleanup" => ("نتیجهٔ خروج از نشست روشن نیست و انتقال متوقف است.", "نشست را به‌صورت مستقل بررسی کنید؛ خروج نامطمئن نباید دوباره ارسال شود."),
            "receiver_not_ready" or "receiver_start_failed" => ("گیرندهٔ این مرحله آماده نشد و انتقال کامل نشده است.", "آمادگی دریافت و نبود تداخل گیرنده یا وب‌هوک را بررسی کنید."),
            "drain_timeout" or "lifecycle_unavailable" => ("توقف یا تخلیهٔ عملیات مبدا کامل نشد.", "عملیات در حال اجرا و چرخهٔ گیرنده را بررسی کنید؛ مسیر محافظت‌شده را باز نکنید."),
            "rate_limited" => ("سرویس این مرحله درخواست‌ها را موقتاً محدود کرده است.", "تا پایان محدودیت صبر کنید و وضعیت عملیات را دوباره مشاهده کنید."),
            "telegram_upstream" => ("سرویس بالادستی این مرحله خطا داد.", "پس از بازیابی سرویس وضعیت عملیات را دوباره مشاهده کنید."),
            "migration_in_progress" => ("انتقال دیگری برای این ربات در حال انجام است.", "تا پایان عملیات جاری صبر کنید و وضعیت همان ربات را مشاهده کنید."),
            "busy" => ("بررسی دیگری برای این ربات در حال اجراست.", "پس از پایان بررسی، درخواست را از صفحهٔ تازه ارسال کنید."),
            "cloud_cooldown" => ("زمان انتظار رسمی برای بازگشت به CLOUD هنوز پایان نیافته است.", "تا پایان زمان انتظار صبر کنید؛ خروج دوباره لازم نیست."),
            "stale" => ("این درخواست متعلق به وضعیت قدیمی ربات است.", "صفحه را تازه کنید و هدف را دوباره انتخاب کنید."),
            "registration_uncertain" => ("ثبت قطعی درخواست انتقال روشن نیست.", "وضعیت ذخیره‌شدهٔ همان ربات را مشاهده کنید؛ درخواست را کورکورانه تکرار نکنید."),
            "not_submitted" => ("این درخواست به عملیات انتقال ارسال نشد.", "گزارش دسته را بررسی و فقط پس از مشاهدهٔ وضعیت تازه دوباره انتخاب کنید."),
            "retained_cloud_control" => ("این ربات برای مدیریت مستقل روی CLOUD باقی ماند.", "از همین مسیر مستقل برای مشاهده و مدیریت وضعیت ربات‌ها استفاده کنید."),
            "batch_busy" => ("درخواست دسته‌ای دیگری در حال ثبت است.", "تا پایان ثبت دستهٔ جاری صبر کنید و گزارش آن را مشاهده کنید."),
            "control_path_missing" or "operator_control_missing" => ("مسیر مستقل مدیریت روی CLOUD باقی نمی‌ماند.", "یک ربات مدیریت مستقل و فعال روی CLOUD نگه دارید."),
            "aliased" or "identity_alias_conflict" => ("این هویت ربات مسیرهای هم‌نام یا متعارض دارد.", "ثبت هویت‌های تکراری و مسیرهای ذخیره‌شده را بررسی کنید."),
            "denied" => ("این درخواست مجاز نیست.", "از حساب مدیر اصلی و صفحهٔ مجاز استفاده کنید."),
            "disabled" => ("مدیریت مسیر Telegram غیرفعال است.", "تنظیم فعال بودن مدیریت مسیر را بررسی کنید."),
            "unavailable" or "configuration_missing" => ("ثبت یا تنظیمات لازم برای این ربات کامل نیست.", "فعال بودن ربات و ثبت هویت و تنظیمات آن را بررسی کنید."),
            "recovery_exhausted" => ("بازیابی امن پس از چند تلاش کامل نشد.", "سابقهٔ مرحلهٔ شکست را بررسی کنید؛ عملیات نامطمئن را تکرار نکنید."),
            "persistence_conflict" => ("ثبت وضعیت عملیات با تغییر هم‌زمان روبه‌رو شد.", "وضعیت تازهٔ ربات و سابقهٔ عملیات را مشاهده کنید."),
            _ => ("تشخیص دقیق این خطای قدیمی در دسترس نیست.", "سابقه و گزارش محلی را بررسی کنید؛ مرحله یا موفقیت انتقال را حدس نزنید.")
        };
        var known = DirectFailures.Contains(category, StringComparer.Ordinal) || stage != "unknown" || ProbeFailures.Contains(category, StringComparer.Ordinal) || category is "logout_refused" or "logout_uncertain" or "receiver_not_ready" or "drain_timeout" or "lifecycle_unavailable" or "unsafe_cleanup" or "recovery_exhausted" or "receiver_start_failed" or "persistence_conflict" or "identity_alias_conflict" or "operator_control_missing";
        var checkedCategory = stage switch
        {
            "local_file_mapping" => "ریشه‌های نگاشت، وجود پوشه، دسترسی خواندن و جست‌وجو و نبود پیوند",
            "local_root" => "پاسخ رسمی سرویس LOCAL بدون توکن",
            "cloud_source_identity" or "local_source_identity" => "هویت ربات در سرویس مبدا",
            "local_destination_identity" or "cloud_destination_identity" => "هویت ربات در سرویس مقصد پس از خروج تاییدشده",
            "cloud_logout" or "local_logout" => "تایید خروج یک‌باره از نشست مبدا",
            "source_stop" or "source_drain" or "source_lifecycle" => "توقف گیرنده و پایان عملیات پذیرفته‌شدهٔ مبدا",
            "source_restore" => "آمادگی دوبارهٔ گیرندهٔ مبدا پس از شکست انتقال",
            "local_destination_receiver" or "cloud_destination_receiver" => "آمادگی دریافت در مقصد",
            "migration_admission" => "اجازهٔ انتقال، ثبت ربات و وضعیت عملیات جاری",
            "cloud_cooldown" => "پایان انتظار رسمی پس از خروج تاییدشده",
            _ => "دستهٔ خطای ثبت‌شده؛ مرحلهٔ دقیق قدیمی نامعلوم"
        };
        return new(known ? category.ToUpperInvariant() : "DIAGNOSTIC_UNAVAILABLE", stage, checkedCategory, message, action);
    }
}
