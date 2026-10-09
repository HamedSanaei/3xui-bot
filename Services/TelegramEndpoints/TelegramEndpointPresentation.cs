using System.Globalization;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Formats secret-free route admission and current-operation outcomes without treating durable intent as a live route.</summary>
/// <remarks>Gate admission is not a network probe or proof that a receiver is alive. Success requires a destination activation committed during the current operation.</remarks>
internal static class TelegramEndpointPresentation
{
    /// <summary>Formats the actual admission badge before any desired or historical route.</summary>
    /// <param name="state">Detached exact-identity state with nullable runtime observations.</param>
    /// <param name="enabled">Whether the current registry configuration enables this bot.</param>
    /// <returns>A fixed Persian badge; absent observations never default to Cloud.</returns>
    internal static string ActiveBadge(TelegramEndpointState state, bool enabled)
    {
        if (state.TelegramBotId <= 0) return "❔ مسیر فعال: نامشخص؛ هویت موجود نیست";
        if (!enabled) return "⛔ مسیر فعال: ندارد؛ ربات غیرفعال است";
        if (state.RuntimeAvailable == false) return "⛔ مسیر فعال: ندارد؛ پذیرش درخواست بسته است";
        if (state.RuntimeAvailable != true || !state.RuntimeEndpoint.HasValue || !Enum.IsDefined(state.RuntimeEndpoint.Value) || !state.RuntimeGeneration.HasValue)
            return "❔ مسیر فعال: نامشخص؛ مشاهده مسیر موجود نیست";
        return state.RuntimeEndpoint == TelegramEndpointType.Cloud ? "☁️ Cloud — مسیر فعال" : "🏠 Local — مسیر فعال";
    }

    /// <summary>Gets the destination of the current operation, separately from automatic fallback's preserved desired Local route.</summary>
    /// <param name="state">Detached durable operation metadata.</param>
    /// <returns>The explicit manual destination or Cloud for an automatic outage recovery.</returns>
    internal static TelegramEndpointType Target(TelegramEndpointState state)
        => state.Trigger == "automatic_outage" ? TelegramEndpointType.Cloud : state.DesiredEndpoint;

    /// <summary>Classifies the current operation without confusing old activation timestamps or later health failures with migration completion.</summary>
    /// <param name="history">Current identity's bounded history; success requires an activation receipt for the exact current operation.</param>
    /// <param name="state">Detached exact-identity state.</param>
    /// <returns>A closed presentation code: unknown, none, succeeded, pending, unproven, refused, failed or uncertain.</returns>
    /// <remarks>A restored source has no new destination activation. A later degraded destination retains its successful migration result.</remarks>
    internal static string Outcome(TelegramEndpointState state, IReadOnlyList<TelegramEndpointHistory> history)
    {
        if (state.TelegramBotId <= 0) return "unknown";
        if (string.IsNullOrEmpty(state.OperationId)) return "none";
        if (state.MigrationStartedAtUtc.HasValue && state.LastMigrationAtUtc >= state.MigrationStartedAtUtc &&
            state.MigrationState is (TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered or
                TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded or TelegramEndpointMigrationState.LocalUnavailable) &&
            state.EffectiveEndpoint == Target(state) && history.Any(x => x.OperationId == state.OperationId &&
                x.Reason is ("migration_succeeded" or "cloud_recovered") && x.ToEffectiveEndpoint == Target(state))) return "succeeded";
        if (state.MigrationState is TelegramEndpointMigrationState.CloudLogoutUncertain or TelegramEndpointMigrationState.LocalLogoutUncertain or
            TelegramEndpointMigrationState.ManualInterventionRequired ||
            (state.LogoutAttemptedAtUtc.HasValue && !state.LogoutAcknowledgedAtUtc.HasValue && state.LastFailureCategory != "logout_refused"))
            return "uncertain";
        if (history.Any(x => x.OperationId == state.OperationId && x.Reason == "logout_refused")) return "refused";
        if (state.MigrationState == TelegramEndpointMigrationState.MigrationFailed ||
            history.Any(x => x.OperationId == state.OperationId && x.Reason == "migration_failed")) return "failed";
        if (state.MigrationState is TelegramEndpointMigrationState.Cloud or TelegramEndpointMigrationState.CloudRecovered or
            TelegramEndpointMigrationState.Local or TelegramEndpointMigrationState.LocalDegraded or TelegramEndpointMigrationState.LocalUnavailable)
            return "unproven";
        return "pending";
    }

    /// <summary>Formats the current migration outcome and next operator action before telemetry.</summary>
    /// <param name="history">Current identity's bounded durable operation receipts, not a batch registration result.</param>
    /// <param name="state">Detached current operation metadata.</param>
    /// <returns>A fixed Persian operational label with UTC activation evidence only for proven current-operation activation.</returns>
    internal static string OutcomeLabel(TelegramEndpointState state, IReadOnlyList<TelegramEndpointHistory> history) => Outcome(state, history) switch
    {
        "unknown" => "نتیجه انتقال: نامشخص؛ ابتدا هویت ربات را اصلاح کنید.",
        "none" => "نتیجه انتقال: عملیات انتقالی برای هویت جاری ثبت نشده است.",
        "unproven" => "نتیجه انتقال: مسیر مقصد/انتخاب را بررسی کنید؛ فعال‌سازی جدید برای همین عملیات اثبات نشده است (ممکن است مقصد از قبل برقرار باشد).",
        "succeeded" => $"✅ نتیجه انتقال: موفق؛ مقصد در {state.LastMigrationAtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)} فعال شد." +
            (state.RuntimeAvailable == false || state.MigrationState is TelegramEndpointMigrationState.LocalDegraded or TelegramEndpointMigrationState.LocalUnavailable
                ? " ⚠️ افت سلامت/بسته‌شدن مسیر پس از انتقال موفق؛ وضعیت فعلی را جداگانه بررسی کنید." : ""),
        "refused" => "❌ نتیجه انتقال: خروج رد شد؛ مقصد فعال نشد. مسیر مبدأ بازیابی‌شده را از نشان مسیر فعال بررسی کنید.",
        "failed" => "❌ نتیجه انتقال: ناموفق؛ مقصد فعال نشد. مسیر مبدأ/خطا را بررسی کنید.",
        "uncertain" => "⚠️ نتیجه انتقال: نامطمئن/نیازمند بررسی دستی؛ logOut یا درخواست انتقال را تکرار نکنید.",
        _ => state.MigrationState == TelegramEndpointMigrationState.CloudWait
            ? "⏳ نتیجه انتقال: در انتظار مهلت رسمی Cloud؛ ثبت درخواست به معنی تکمیل نیست. تازه‌سازی کنید."
            : "⏳ نتیجه انتقال: در حال اجرا/در انتظار؛ ثبت درخواست به معنی تکمیل نیست. تازه‌سازی کنید."
    };
}
