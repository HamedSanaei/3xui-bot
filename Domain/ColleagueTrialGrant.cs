namespace Adminbot.Domain;

/// <summary>Durable receipt for one owned-bot colleague trial delivery request and its global daily quota slot.</summary>
/// <remarks>
/// The positive Telegram user id and Tehran calendar date define the quota across all owned bots and both trial
/// services; BotId records origin, not a separate allowance. Identifiers, service, grant date, request key and
/// operation key are immutable after insertion. No foreign key requires a bot conversation or credentials.db
/// profile to survive. The operation key logically links to XuiV3CreationOperations when creation is attempted.
/// New receipts begin at rollout with no historical backfill. Retain all receipts, including Released and Denied,
/// indefinitely: deleting them would permit an old Telegram event to reserve and create again. These rows carry
/// no account credentials and have no wallet, ledger, order, or partner-profit effects.
/// </remarks>
public sealed class ColleagueTrialGrant
{
    /// <summary>Immutable users.db receipt id, a random 32-character GUID unrelated to Telegram identifiers.</summary>
    public string Id { get; internal set; }

    /// <summary>Positive global Telegram sender id sharing one daily cap across every owned bot and trial service.</summary>
    public long TelegramUserId { get; internal set; }

    /// <summary>Internal originating owned-bot runtime id; neither a Telegram bot id nor a tenant quota boundary.</summary>
    public string BotId { get; internal set; }

    /// <summary>Immutable selected trial service key, exactly <c>national</c> or <c>normal</c>.</summary>
    public string ServiceKey { get; internal set; }

    /// <summary>Globally unique credential-free delivery identity including bot/chat/message or durable inbox event.</summary>
    public string DeliveryRequestKey { get; internal set; }

    /// <summary>Immutable free creation identity, <c>colleague-trial:{Id}</c>; paid fallback has a separate deterministic key.</summary>
    public string OperationKey { get; internal set; }

    /// <summary>Immutable Tehran-local Gregorian date at unspecified-kind midnight, never a rolling UTC interval.</summary>
    public DateTime GrantDateIran { get; internal set; }

    /// <summary>False until the sole free executor claims this request; true is immutable even after release.</summary>
    /// <remarks>Claim before progress notification or panel work. Started with absent/Reserved creation after restart remains held for review, not replay.</remarks>
    public bool FreeCreationStarted { get; internal set; }

    /// <summary>Nullable positive whole-toman colleague price snapshot for the paid fallback of a Denied receipt.</summary>
    /// <remarks>Previewed before wallet debit, then frozen to the committed debit receipt amount by the sole paid executor claim.</remarks>
    public long? PaidQuoteToman { get; internal set; }

    /// <summary>Paid-only executor and creation outcome; independent of the Denied free quota state.</summary>
    /// <remarks>Only one NotStarted-to-Started claim may create; Started and Uncertain forbid replay/refund until durable evidence resolves them.</remarks>
    public ColleagueTrialPaidCreationState PaidCreationState { get; internal set; }

    /// <summary>UTC proof-recording time after the caller completed the exact debit-linked refund credit and ledger for Rejected paid creation.</summary>
    /// <remarks>Set once independently of bot conversation state; null keeps the definitive rejection eligible for bounded refund recovery.</remarks>
    public DateTime? PaidRefundRecordedAtUtc { get; internal set; }

    /// <summary>Quota lifecycle; Reserved, Uncertain and Consumed occupy capacity, while Released and Denied do not.</summary>
    public ColleagueTrialGrantState State { get; internal set; }

    /// <summary>UTC timestamp when this request was first admitted or denied; never changed by redelivery.</summary>
    public DateTime CreatedAtUtc { get; internal set; }

    /// <summary>UTC timestamp of the latest durable lifecycle transition; duplicate settlement does not refresh it.</summary>
    public DateTime UpdatedAtUtc { get; internal set; }

    /// <summary>UTC time proven panel creation consumed the slot, persisted before Telegram notification.</summary>
    public DateTime? ConsumedAtUtc { get; internal set; }

    /// <summary>UTC time definitive non-creation freed capacity; the request and operation key remain terminal.</summary>
    public DateTime? ReleasedAtUtc { get; internal set; }
}

/// <summary>Durable colleague trial quota lifecycle independent of Telegram delivery and bot conversation state.</summary>
public enum ColleagueTrialGrantState
{
    /// <summary>Slot held before provisioning; the creation-operation store alone can authorize its single POST.</summary>
    Reserved = 0,

    /// <summary>POST was authorized or its outcome is ambiguous; holds capacity without timeout or lease expiry.</summary>
    Uncertain = 1,

    /// <summary>Authoritative creation proof consumes capacity even when subsequent Telegram notification fails.</summary>
    Consumed = 2,

    /// <summary>Definitive non-creation frees capacity, but this delivery request can never reserve or POST again.</summary>
    Released = 3,

    /// <summary>Capacity was exhausted or configured zero; redelivery never reconsiders this terminal request.</summary>
    Denied = 4
}

/// <summary>Durable paid-test executor lifecycle protecting the debit, sole creation attempt and safe refund boundary.</summary>
/// <remarks>The quota store records local authorization/outcomes only; callers own wallet receipts, ledger and Telegram notifications.</remarks>
public enum ColleagueTrialPaidCreationState
{
    /// <summary>No paid executor has claimed this denied request; a live paid quote may still be updated.</summary>
    NotStarted = 0,

    /// <summary>Exactly one executor claimed the committed debit amount; absence or Reserved after restart is review-only.</summary>
    Started = 1,

    /// <summary>Proven paid account creation; no refund or repeated POST, even when Telegram delivery fails.</summary>
    Applied = 2,

    /// <summary>Authorized or ambiguous paid POST; no refund or replay until authoritative creation evidence resolves it.</summary>
    Uncertain = 3,

    /// <summary>The quiesced winning attempt definitively did not create; permits one exact receipt-linked refund, never replay.</summary>
    Rejected = 4
}
