using Microsoft.EntityFrameworkCore;

namespace Adminbot.Services.TelegramEndpoints;

/// <summary>Trusted Telegram Bot API endpoint families; never operator-provided URLs.</summary>
public enum TelegramEndpointType
{
    /// <summary>The official public Telegram Bot API.</summary>
    Cloud,
    /// <summary>The existing loopback Bot API server.</summary>
    Local
}

/// <summary>Durable migration phases that fence ordinary traffic until activation is verified.</summary>
public enum TelegramEndpointMigrationState
{
    /// <summary>Ordinary official Cloud operation.</summary>
    Cloud,
    /// <summary>Tokenless Local reachability and Cloud identity checks precede logout.</summary>
    CheckingLocal,
    /// <summary>A Cloud logout might have begun; restart must not replay it.</summary>
    CloudLogoutPending,
    /// <summary>Cloud logout delivery or acknowledgment is unknown.</summary>
    CloudLogoutUncertain,
    /// <summary>Cloud logout acknowledged; destination Local activation is pending.</summary>
    SwitchingToLocal,
    /// <summary>Verified Local operation.</summary>
    Local,
    /// <summary>Local health is degraded but the outage threshold has not been reached.</summary>
    LocalDegraded,
    /// <summary>Local operation is unavailable and ordinary traffic is fenced.</summary>
    LocalUnavailable,
    /// <summary>Fallback needs safe Local cleanup before Cloud can be used.</summary>
    FallbackPending,
    /// <summary>A Local logout might have begun; restart must not replay it.</summary>
    LocalLogoutPending,
    /// <summary>Local logout delivery or acknowledgment is unknown.</summary>
    LocalLogoutUncertain,
    /// <summary>Logout acknowledged; the official Cloud reuse deadline has not elapsed.</summary>
    CloudWait,
    /// <summary>Cloud activation and receiver verification are pending.</summary>
    SwitchingToCloud,
    /// <summary>Cloud recovered an unavailable Local route without changing desired Local intent.</summary>
    CloudRecovered,
    /// <summary>The migration failed before an ambiguous logout.</summary>
    MigrationFailed,
    /// <summary>Safe automated progress is impossible; an operator must review the incident.</summary>
    ManualInterventionRequired
}

/// <summary>Detached, secret-free endpoint state scoped to an internal id and immutable BotFather identity, with optional unmapped runtime observations for administration.</summary>
public sealed class TelegramEndpointState
{
    /// <summary>Exact internal registry identifier, at most 64 characters.</summary>
    public string BotId { get; set; }
    /// <summary>Positive numeric BotFather identity; token rotation does not change this key.</summary>
    public long TelegramBotId { get; set; }
    /// <summary>Operator-selected destination, preserved during automatic fallback.</summary>
    public TelegramEndpointType DesiredEndpoint { get; set; } = TelegramEndpointType.Cloud;
    /// <summary>Last activated endpoint; not proof that ordinary admissions are currently open.</summary>
    public TelegramEndpointType EffectiveEndpoint { get; set; } = TelegramEndpointType.Cloud;
    /// <summary>Durable control protocol phase.</summary>
    public TelegramEndpointMigrationState MigrationState { get; set; } = TelegramEndpointMigrationState.Cloud;
    /// <summary>Monotonic route generation; ordinary requests must retain their admission generation.</summary>
    public long Generation { get; set; } = 1;
    /// <summary>Current process route selected by the admission gate, or null when exact current identity cannot be observed; never persisted.</summary>
    /// <remarks>Unlike EffectiveEndpoint, this observes the running process. It does not prove remote health or receiver liveness and must be read with RuntimeAvailable.</remarks>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public TelegramEndpointType? RuntimeEndpoint { get; set; }
    /// <summary>Whether the exact enabled current bot can admit new ordinary requests at observation time, or null when unobserved; never persisted.</summary>
    /// <remarks>False means paused, fenced, disabled or otherwise unavailable admission. Already admitted work can still finish its original epoch; no network probe is performed.</remarks>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool? RuntimeAvailable { get; set; }
    /// <summary>Current process route epoch observed alongside RuntimeEndpoint, or null when unobserved; never persisted.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public long? RuntimeGeneration { get; set; }
    /// <summary>CAS version incremented by every durable state save.</summary>
    public long Revision { get; set; }
    /// <summary>Operator-control version; health-only writes do not invalidate confirmation panels.</summary>
    public long ControlRevision { get; set; }
    /// <summary>Whether Local outages may request automatic Cloud fallback.</summary>
    public bool AutoFailoverEnabled { get; set; } = true;
    /// <summary>Last successful identity-bound health observation in UTC.</summary>
    public DateTime? LastSuccessfulHealthAtUtc { get; set; }
    /// <summary>Last health attempt in UTC.</summary>
    public DateTime? LastHealthCheckAtUtc { get; set; }
    /// <summary>Most recent health failure in UTC.</summary>
    public DateTime? LastFailureAtUtc { get; set; }
    /// <summary>Closed safe failure category; never provider descriptions or exception messages.</summary>
    public string LastFailureCategory { get; set; }
    /// <summary>Most recent activated migration in UTC.</summary>
    public DateTime? LastMigrationAtUtc { get; set; }
    /// <summary>Earliest UTC instant at which acknowledged logout permits Cloud activation.</summary>
    public DateTime? CloudReuseEligibleAtUtc { get; set; }
    /// <summary>Consecutive relevant health failures.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Consecutive relevant health successes.</summary>
    public int ConsecutiveSuccesses { get; set; }
    /// <summary>Secret-free 32-hex migration operation identity.</summary>
    public string OperationId { get; set; }
    /// <summary>Global superadmin Telegram user id, or null for automatic operations.</summary>
    public long? ActorTelegramUserId { get; set; }
    /// <summary>Closed manual, automatic_outage, automatic_failback, or startup_recovery trigger.</summary>
    public string Trigger { get; set; }
    /// <summary>Migration start in UTC.</summary>
    public DateTime? MigrationStartedAtUtc { get; set; }
    /// <summary>Persisted before the non-replayable logout request begins.</summary>
    public DateTime? LogoutAttemptedAtUtc { get; set; }
    /// <summary>Endpoint whose session cleanup was attempted.</summary>
    public TelegramEndpointType? LogoutEndpoint { get; set; }
    /// <summary>UTC logout acknowledgment; null must never be treated as cleanup proof.</summary>
    public DateTime? LogoutAcknowledgedAtUtc { get; set; }
    /// <summary>Next safe recovery attempt in UTC.</summary>
    public DateTime? NextAttemptAtUtc { get; set; }
    /// <summary>Number of safe bounded recovery attempts.</summary>
    public int RecoveryAttempts { get; set; }
    /// <summary>Secret-free 32-hex identity of the current Local outage.</summary>
    public string OutageId { get; set; }
    /// <summary>UTC instant at which durable outage notification intents were created.</summary>
    public DateTime? LastOutageNotifiedAtUtc { get; set; }

    /// <summary>Copies this state without carrying an EF tracker into runtime operations.</summary>
    /// <returns>An independently mutable detached copy containing no transport credentials.</returns>
    /// <example><code>var proposed = current.Copy();</code></example>
    public TelegramEndpointState Copy() => (TelegramEndpointState)MemberwiseClone();
}

/// <summary>Append-only secret-free endpoint transition receipt; has no financial side effects.</summary>
public sealed class TelegramEndpointHistory
{
    /// <summary>Database-generated increasing receipt identity.</summary>
    public long Id { get; set; }
    /// <summary>Exact internal bot identifier.</summary>
    public string BotId { get; set; }
    /// <summary>Immutable numeric BotFather identity.</summary>
    public long TelegramBotId { get; set; }
    /// <summary>Optional migration operation identity.</summary>
    public string OperationId { get; set; }
    /// <summary>Optional global operator Telegram user id.</summary>
    public long? ActorTelegramUserId { get; set; }
    /// <summary>Desired endpoint before the transition.</summary>
    public TelegramEndpointType FromDesiredEndpoint { get; set; }
    /// <summary>Desired endpoint after the transition.</summary>
    public TelegramEndpointType ToDesiredEndpoint { get; set; }
    /// <summary>Effective endpoint before the transition.</summary>
    public TelegramEndpointType FromEffectiveEndpoint { get; set; }
    /// <summary>Effective endpoint after the transition.</summary>
    public TelegramEndpointType ToEffectiveEndpoint { get; set; }
    /// <summary>Durable migration phase after the transition.</summary>
    public TelegramEndpointMigrationState MigrationState { get; set; }
    /// <summary>Closed internal transition reason; never an HTTP body or exception message.</summary>
    public string Reason { get; set; }
    /// <summary>Closed migration-state name for successful/requested transitions, or the validated secret-free failure category for failed/refused/uncertain/admission/safe-retry receipts. Legacy state-only outcomes have no inferred precise failure stage.</summary>
    public string Outcome { get; set; }
    /// <summary>UTC commit observation time.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Committed state CAS revision.</summary>
    public long Revision { get; set; }
}

/// <summary>At-most-once delivery phases for logger-channel endpoint incidents.</summary>
public enum TelegramEndpointAlertStatus
{
    /// <summary>Durable unsent intent awaiting configured transport or safe retry.</summary>
    Pending,
    /// <summary>Claimed by one worker; SendStartedAtUtc determines replay safety.</summary>
    Processing,
    /// <summary>Telegram acknowledged delivery, including a legacy private delivery proven by a compact/pruned receipt; never replayed into the logger.</summary>
    Delivered,
    /// <summary>The send may have happened; automatic replay is prohibited.</summary>
    DeliveryUncertain,
    /// <summary>Real safe attempts exhausted or a definitive send rejection requires operator review.</summary>
    ManualReview
}

/// <summary>One durable logger-channel intent per incident without stored message bodies, private recipients, or secrets.</summary>
public sealed class TelegramEndpointAlert
{
    /// <summary>Database-generated notification identity.</summary>
    public long Id { get; set; }
    /// <summary>Stable bot/identity/incident/category deduplication identity.</summary>
    public string IncidentKey { get; set; }
    /// <summary>Exact internal bot identifier affected by the incident.</summary>
    public string BotId { get; set; }
    /// <summary>BotFather identity affected by the incident.</summary>
    public long TelegramBotId { get; set; }
    /// <summary>Verified negative channel chat id frozen at the first send boundary; retained across definitive 429 retries.</summary>
    /// <remarks>Null for unsent intents and migrated legacy delivery fences; a legacy Delivered row is not proof of channel delivery.</remarks>
    public long? DestinationChatId { get; set; }
    /// <summary>Closed safe notification category.</summary>
    public string Category { get; set; }
    /// <summary>Incident migration phase, frozen at creation.</summary>
    public TelegramEndpointMigrationState MigrationState { get; set; }
    /// <summary>Incident desired endpoint.</summary>
    public TelegramEndpointType DesiredEndpoint { get; set; }
    /// <summary>Incident effective endpoint.</summary>
    public TelegramEndpointType EffectiveEndpoint { get; set; }
    /// <summary>Incident route generation.</summary>
    public long Generation { get; set; }
    /// <summary>UTC intent creation time.</summary>
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>Current durable delivery phase.</summary>
    public TelegramEndpointAlertStatus Status { get; set; }
    /// <summary>32-hex exclusive worker claim, cleared only on terminal or safe retry transitions.</summary>
    public string ClaimId { get; set; }
    /// <summary>UTC pre-send claim deadline.</summary>
    public DateTime? LeaseUntilUtc { get; set; }
    /// <summary>UTC durable non-replayable send boundary, persisted before calling Telegram.</summary>
    public DateTime? SendStartedAtUtc { get; set; }
    /// <summary>UTC send deadline used to reconcile interrupted workers.</summary>
    public DateTime? SendDeadlineAtUtc { get; set; }
    /// <summary>Count of budgeted claims; missing sender/channel prerequisites refund their pre-send claim.</summary>
    public int Attempts { get; set; }
    /// <summary>UTC next bounded retry time.</summary>
    public DateTime NextAttemptAtUtc { get; set; }
    /// <summary>Closed diagnostic category; raw errors are never persisted.</summary>
    public string ErrorCategory { get; set; }
    /// <summary>Observed UTC acknowledgment time; null for unacknowledged sends and legacy pruned acknowledgments whose timestamp no longer exists.</summary>
    public DateTime? DeliveredAtUtc { get; set; }
}

/// <summary>Compact permanent incident deduplication receipt retained after acknowledged outbox history expires.</summary>
/// <remarks>Contains no message body, recipient, transport identity, token, or error payload. Never cascades with bot deletion.</remarks>
public sealed class TelegramEndpointAlertReceipt
{
    /// <summary>Stable affected-bot identity, incident, and category key.</summary>
    public string IncidentKey { get; set; }
}

/// <summary>Shared endpoint-only EF mapping with one logger intent and permanent receipt per incident; never configures financial entities.</summary>
public static class TelegramEndpointModelConfiguration
{
    /// <summary>Adds endpoint state, append-only history, and incident-only logger outbox mappings.</summary>
    /// <param name="modelBuilder">Required users.db runtime or migration model builder.</param>
    /// <remarks>IncidentKey alone uniquely identifies an alert and its compact permanent receipt. DestinationChatId is nullable until the verified negative channel is frozen. No foreign keys or cascading deletes exist; historical identities and dedupe survive bot replacement.</remarks>
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TelegramEndpointState>(b =>
        {
            b.ToTable("TelegramEndpointStates");
            b.HasKey(x => new { x.BotId, x.TelegramBotId });
            b.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            b.Property(x => x.DesiredEndpoint).HasDefaultValue(TelegramEndpointType.Cloud);
            b.Property(x => x.EffectiveEndpoint).HasDefaultValue(TelegramEndpointType.Cloud);
            b.Property(x => x.MigrationState).HasDefaultValue(TelegramEndpointMigrationState.Cloud);
            b.Property(x => x.Generation).HasDefaultValue(1L);
            b.Property(x => x.Revision).IsConcurrencyToken().HasDefaultValue(0L);
            b.Property(x => x.ControlRevision).HasDefaultValue(0L);
            b.Property(x => x.AutoFailoverEnabled).HasDefaultValue(true);
            b.Property(x => x.OperationId).HasMaxLength(32);
            b.Property(x => x.OutageId).HasMaxLength(32);
            b.Property(x => x.LastFailureCategory).HasMaxLength(64);
            b.Property(x => x.Trigger).HasMaxLength(32);
            b.HasIndex(x => new { x.MigrationState, x.NextAttemptAtUtc });
        });
        modelBuilder.Entity<TelegramEndpointHistory>(b =>
        {
            b.ToTable("TelegramEndpointHistory");
            b.HasKey(x => x.Id);
            b.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            b.Property(x => x.OperationId).HasMaxLength(32);
            b.Property(x => x.Reason).IsRequired().HasMaxLength(64);
            b.Property(x => x.Outcome).IsRequired().HasMaxLength(64);
            b.HasIndex(x => new { x.BotId, x.TelegramBotId, x.Id });
        });
        modelBuilder.Entity<TelegramEndpointAlert>(b =>
        {
            b.ToTable("TelegramEndpointAlerts");
            b.HasKey(x => x.Id);
            b.Property(x => x.IncidentKey).IsRequired().HasMaxLength(240);
            b.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            b.Property(x => x.Category).IsRequired().HasMaxLength(64);
            b.Property(x => x.ClaimId).HasMaxLength(32);
            b.Property(x => x.ErrorCategory).HasMaxLength(64);
            b.HasIndex(x => x.IncidentKey).IsUnique();
            b.HasIndex(x => new { x.Status, x.NextAttemptAtUtc, x.Id });
            b.HasIndex(x => new { x.Status, x.LeaseUntilUtc });
        });
        modelBuilder.Entity<TelegramEndpointAlertReceipt>(b =>
        {
            b.ToTable("TelegramEndpointAlertReceipts");
            b.HasKey(x => x.IncidentKey);
            b.Property(x => x.IncidentKey).IsRequired().HasMaxLength(240);
        });
    }
}
