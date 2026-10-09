namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Formats secret-free route admission and current-operation outcomes without treating durable intent as a live route.</summary>
/// <remarks>Gate admission is not a network probe or proof that a receiver is alive. Success requires a destination activation committed during the current operation.</remarks>
internal static class TelegramEndpointPresentation
{
    /// <summary>Formats only the admitted endpoint or an explicit unavailable/unknown connection headline.</summary>
    /// <param name="state">Detached exact-identity state with nullable runtime observations.</param>
    /// <param name="enabled">Whether the current registry configuration enables this bot.</param>
    /// <returns>Exact uppercase CLOUD/LOCAL for an admitted route; never a historical source presented as active.</returns>
    /// <remarks>The headline describes admission, not network reachability. Migration progress is added separately by the panel.</remarks>
    internal static string ActiveBadge(TelegramEndpointState state, bool enabled)
    {
        if (state.TelegramBotId <= 0) return "❔ اتصال نامشخص؛ هویت ربات موجود نیست";
        if (!enabled) return "⛔ اتصال غیرفعال؛ ربات غیرفعال است";
        if (state.RuntimeAvailable == false) return "⛔ اتصال متوقف؛ پذیرش درخواست بسته است";
        var endpoint = AdmittedEndpoint(state, enabled);
        return endpoint.HasValue ? EndpointBadge(endpoint.Value) : "❔ اتصال نامشخص؛ مسیر فعلی مشاهده نشده است";
    }

    /// <summary>Identifies only a currently observed ordinary-request endpoint for both the inventory totals and individual badges.</summary>
    /// <param name="state">Required detached current BotFather identity with the coordinator's gate snapshot; disabled bots have RuntimeAvailable=false.</param>
    /// <param name="enabled">Current registry enablement, not the saved endpoint preference or receiver health.</param>
    /// <returns>Cloud or Local only for an enabled, identified, admitted and observed generation; null for paused, disabled or unknown routing.</returns>
    /// <remarks>This is read-only classification, not a health probe. Desired and last-effective endpoints never fill missing runtime observations.</remarks>
    /// <example><code>var endpoint = TelegramEndpointPresentation.AdmittedEndpoint(state, enabled: true);</code></example>
    internal static TelegramEndpointType? AdmittedEndpoint(TelegramEndpointState state, bool enabled)
        => enabled && state.TelegramBotId > 0 && state.RuntimeAvailable == true && state.RuntimeEndpoint.HasValue &&
            Enum.IsDefined(state.RuntimeEndpoint.Value) && state.RuntimeGeneration.HasValue ? state.RuntimeEndpoint : null;

    /// <summary>Formats a verified endpoint origin without claiming network health or completed migration.</summary>
    /// <param name="endpoint">Closed endpoint selected by a current route or the legacy Cloud-only client; never a URL or credential.</param>
    /// <returns>The fixed CLOUD or LOCAL badge; an invalid enum remains explicitly unknown rather than defaulting to Cloud.</returns>
    /// <remarks>Receiver-start events use their captured generation's origin, which may still be a staged destination with ordinary admission closed.</remarks>
    /// <example><code>var label = TelegramEndpointPresentation.EndpointBadge(receiverRoute.Endpoint);</code></example>
    internal static string EndpointBadge(TelegramEndpointType endpoint) => endpoint switch
    {
        TelegramEndpointType.Cloud => "☁️ CLOUD",
        TelegramEndpointType.Local => "🏠 LOCAL",
        _ => "❔ Endpoint نامشخص"
    };

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

    /// <summary>Separates the newest refused registration from the last committed operation after process-local controls are lost.</summary>
    /// <param name="state">Fresh current configured identity.</param>
    /// <param name="history">Current identity's bounded durable request/activation receipts.</param>
    /// <returns>A fixed Persian main-screen summary; a newer refusal never becomes a successful new migration.</returns>
    /// <remarks>Exact-operation Outcome remains unchanged for batch attribution; admission refusal cannot rewrite an older batch's committed success.</remarks>
    internal static string MainOutcomeLabel(TelegramEndpointState state, IReadOnlyList<TelegramEndpointHistory> history)
    {
        TelegramEndpointHistory latest = null;
        foreach (var item in history)
        {
            if (item.BotId != state.BotId || item.TelegramBotId != state.TelegramBotId ||
                item.Reason is not ("migration_admission_failed" or "migration_requested")) continue;
            if (latest == null || item.CreatedAtUtc > latest.CreatedAtUtc ||
                (item.CreatedAtUtc == latest.CreatedAtUtc && item.Id > latest.Id)) latest = item;
        }
        return latest?.Reason == "migration_admission_failed"
            ? "❌ آخرین درخواست انتقال: اعتبارسنجی رد شد؛ هیچ درخواست جدیدی ثبت نشد.\nآخرین انتقال ثبت‌شدهٔ پیشین:\n" + OutcomeLabel(state, history)
            : OutcomeLabel(state, history);
    }

    /// <summary>Formats the current migration outcome and next operator action before telemetry.</summary>
    /// <param name="history">Current identity's bounded durable operation receipts, not a batch registration result.</param>
    /// <param name="state">Detached current operation metadata.</param>
    /// <returns>A fixed Persian result/action without timestamps or technical protocol metadata.</returns>
    /// <remarks>Admission, registration and execution are distinct. Dates and protocol metadata belong only to the authenticated technical view.</remarks>
    internal static string OutcomeLabel(TelegramEndpointState state, IReadOnlyList<TelegramEndpointHistory> history) => Outcome(state, history) switch
    {
        "unknown" => "نتیجه انتقال: نامشخص؛ ابتدا هویت ربات را اصلاح کنید.",
        "none" => "نتیجه انتقال: عملیات انتقالی برای هویت جاری ثبت نشده است.",
        "unproven" => "نتیجه انتقال: مسیر مقصد/انتخاب را بررسی کنید؛ فعال‌سازی جدید برای همین عملیات اثبات نشده است (ممکن است مقصد از قبل برقرار باشد).",
        "succeeded" => "✅ نتیجه انتقال: موفق؛ مقصد برای همین درخواست فعال شد." +
            (state.RuntimeAvailable == false || state.MigrationState is TelegramEndpointMigrationState.LocalDegraded or TelegramEndpointMigrationState.LocalUnavailable
                ? " ⚠️ افت سلامت/بسته‌شدن مسیر پس از انتقال موفق؛ وضعیت فعلی را جداگانه بررسی کنید." : ""),
        "refused" => "❌ نتیجه انتقال: خروج رد شد؛ مقصد فعال نشد. اتصال فعلی مبدأ را جداگانه بررسی کنید.",
        "failed" => "❌ نتیجه انتقال: ناموفق؛ مقصد فعال نشد. مسیر مبدأ/خطا را بررسی کنید.",
        "uncertain" => "⚠️ نتیجه انتقال: نامطمئن/نیازمند بررسی دستی؛ خروج یا درخواست انتقال را تکرار نکنید.",
        _ => state.MigrationState == TelegramEndpointMigrationState.CloudWait
            ? "⏳ نتیجه انتقال: در انتظار مهلت رسمی CLOUD؛ ثبت درخواست به معنی تکمیل نیست. تازه‌سازی کنید."
            : "⏳ نتیجه انتقال: در حال انتقال؛ ثبت درخواست به معنی تکمیل نیست. تازه‌سازی کنید."
    };
}
