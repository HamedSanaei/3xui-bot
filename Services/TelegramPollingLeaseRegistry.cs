using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Process-local single-flight registry that guarantees at most one long-polling loop per bot and per Telegram token.
/// </summary>
/// <remarks>
/// <para>
/// Why this type exists:
/// the receiver manager previously protected only per-bot serialization, so two different internal bot ids that had been
/// configured with the same Telegram token could each start a <c>getUpdates</c> loop. Telegram answers that situation with
/// its 409 conflict ("terminated by other getUpdates request"), which the affected receivers then reported as unrelated
/// polling failures. This registry makes the in-process invariant explicit and rejects a duplicate attempt with a reason
/// instead of relying on the absence of that configuration mistake.
/// </para>
/// <para>
/// Scope and lifetime:
/// state is in-memory only and dies with the process, which is exactly the guarantee it offers. It cannot detect a second
/// process or a webhook, so a 409 reported by Telegram remains a real conflict even when this registry is empty, and the
/// conflict handling in the receiver manager stays authoritative.
/// </para>
/// <para>
/// Ownership:
/// a lease is identified by the generation that acquired it. Only that generation can release it, so a late shutdown of a
/// previous polling loop can never release the lease its replacement is already using.
/// </para>
/// <para>
/// Privacy:
/// no token text is stored. Leases carry the truncated SHA-256 fingerprint prefix produced by
/// <see cref="TelegramBotTokenIdentity.FingerprintPrefix" />, which is safe to log and cannot be reversed into a credential.
/// </para>
/// </remarks>
public sealed class TelegramPollingLeaseRegistry
{
    /// <summary>Guards every dictionary below; all operations are short and never await while held.</summary>
    private readonly object _syncRoot = new();

    /// <summary>Active lease per internal bot id (case-insensitive, matching registry semantics).</summary>
    private readonly Dictionary<string, TelegramPollingLease> _byBotId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Active lease per token fingerprint, used to reject a token shared by two internal bots.</summary>
    private readonly Dictionary<string, TelegramPollingLease> _byTokenFingerprint = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attempts to become the only poller for one internal bot and one Telegram token.</summary>
    /// <param name="botId">
    /// Internal runtime bot id, for example <c>tenant-8161518157</c>. This is a database identity, never a Telegram id, and
    /// it is the key the receiver manager already uses for per-bot serialization.
    /// </param>
    /// <param name="tokenFingerprint">
    /// Truncated SHA-256 fingerprint prefix from <see cref="TelegramBotTokenIdentity.FingerprintPrefix" />. An empty value
    /// is rejected, because a lease that cannot identify its credential cannot prevent a duplicate poller.
    /// </param>
    /// <param name="generation">
    /// Monotonic process-local generation number of the receiver attempting to start. It becomes the lease owner and is the
    /// only value that may later release it.
    /// </param>
    /// <param name="processId">
    /// Operating-system process id recorded with the lease so an operator can tell an in-process duplicate from a stale
    /// lease reported by another process.
    /// </param>
    /// <param name="reason">Non-secret caller description such as <c>startup</c> or <c>owner_start</c>; used for diagnostics only.</param>
    /// <returns>
    /// An acquisition result whose <see cref="TelegramPollingLeaseResult.Acquired" /> is <c>true</c> only when the caller now
    /// owns the lease. Otherwise the result carries a closed-vocabulary
    /// <see cref="TelegramPollingLeaseResult.RejectionReason" /> and the identity of the current holder so the caller can log
    /// exactly which bot is already polling.
    /// </returns>
    /// <remarks>
    /// The check and the insert happen under one lock, so two concurrent start attempts for the same bot or the same token
    /// cannot both succeed. Rejection is immediate and never waits: the caller must not queue behind a running poller, since
    /// the correct reaction is to report the duplicate rather than to start a second loop.
    /// </remarks>
    /// <example>
    /// <code>
    /// var lease = registry.TryAcquire(bot.Id, fingerprint, generation, Environment.ProcessId, "startup");
    /// if (!lease.Acquired)
    ///     logger.LogWarning("Polling attempt rejected. {Reason} heldBy={HeldBy}", lease.RejectionReason, lease.HeldByBotId);
    /// </code>
    /// </example>
    public TelegramPollingLeaseResult TryAcquire(
        string botId,
        string tokenFingerprint,
        long generation,
        int processId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return TelegramPollingLeaseResult.Rejected(
                TelegramPollingLeaseRejection.MissingBotId, TelegramPollingLease.Empty);

        if (string.IsNullOrWhiteSpace(tokenFingerprint))
            return TelegramPollingLeaseResult.Rejected(
                TelegramPollingLeaseRejection.MissingTokenFingerprint, TelegramPollingLease.Empty);

        lock (_syncRoot)
        {
            if (_byBotId.TryGetValue(botId, out var existingBotLease))
            {
                // Same internal bot already polling: this is the classic double-start that produced two getUpdates loops
                // over one token, and it must be refused rather than queued.
                return TelegramPollingLeaseResult.Rejected(TelegramPollingLeaseRejection.BotAlreadyPolling, existingBotLease);
            }

            if (_byTokenFingerprint.TryGetValue(tokenFingerprint, out var existingTokenLease))
            {
                // A different internal bot holds the same credential. Telegram would answer one of the two loops with a 409
                // conflict, so the duplicate is reported here instead of being discovered as unrelated polling noise.
                return TelegramPollingLeaseResult.Rejected(TelegramPollingLeaseRejection.TokenAlreadyPolling, existingTokenLease);
            }

            var lease = new TelegramPollingLease(
                BotId: botId,
                TokenFingerprint: tokenFingerprint,
                Generation: generation,
                ProcessId: processId,
                Reason: reason ?? string.Empty,
                AcquiredAtUtc: DateTime.UtcNow);

            _byBotId[botId] = lease;
            _byTokenFingerprint[tokenFingerprint] = lease;
            return TelegramPollingLeaseResult.Grant(lease);
        }
    }

    /// <summary>Releases the lease owned by one generation, after that generation's polling loop has ended.</summary>
    /// <param name="botId">Internal bot id whose lease should be released.</param>
    /// <param name="generation">Generation that must still own the lease for the release to be accepted.</param>
    /// <param name="reason">Non-secret release reason such as <c>receiver stopped</c> or <c>host shutdown</c>.</param>
    /// <returns>
    /// The lease that was released, or <c>null</c> when the caller no longer owns it (already released, or replaced by a
    /// newer generation).
    /// </returns>
    /// <remarks>
    /// The generation check is what prevents a late-exiting predecessor from freeing the lease that its replacement is
    /// already polling under, which would silently reopen the duplicate-poller window this registry exists to close.
    /// </remarks>
    public TelegramPollingLease Release(string botId, long generation, string reason)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return null;

        lock (_syncRoot)
        {
            if (!_byBotId.TryGetValue(botId, out var lease) || lease.Generation != generation)
                return null;

            _byBotId.Remove(botId);
            if (_byTokenFingerprint.TryGetValue(lease.TokenFingerprint, out var tokenLease) &&
                tokenLease.Generation == generation)
            {
                _byTokenFingerprint.Remove(lease.TokenFingerprint);
            }

            return lease with { Reason = reason ?? lease.Reason, ReleasedAtUtc = DateTime.UtcNow };
        }
    }

    /// <summary>Reads the lease currently owned by one internal bot, if any.</summary>
    /// <param name="botId">Internal bot id to inspect.</param>
    /// <returns>The active lease, or <c>null</c> when the bot is not polling in this process.</returns>
    /// <remarks>Used by startup diagnostics and tests; never used to make a delivery or authorization decision.</remarks>
    public TelegramPollingLease Current(string botId)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return null;

        lock (_syncRoot)
        {
            return _byBotId.TryGetValue(botId, out var lease) ? lease : null;
        }
    }

    /// <summary>Marks a generation's lease as stopping because its owner has begun an intentional stop.</summary>
    /// <param name="botId">Internal bot id whose lease is being stopped.</param>
    /// <param name="generation">Generation that must still own the lease for the mark to be accepted.</param>
    /// <returns>
    /// <c>true</c> when the lease belonged to that generation and is now marked as stopping; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A lease is held for the whole lifetime of its polling loop, so a legitimate restart briefly observes the
    /// predecessor's lease while that loop is still leaving <c>getUpdates</c>. This mark is what lets the restarter tell
    /// that window apart from a genuinely live duplicate poller: only a lease marked as stopping may be waited for, and a
    /// lease that is not marked stays a hard rejection.
    /// </para>
    /// <para>
    /// The mark never permits two loops to run at once. It only advertises intent; the lease itself is still released by
    /// the predecessor's own generation after its loop has actually terminated.
    /// </para>
    /// </remarks>
    public bool MarkStopping(string botId, long generation)
    {
        if (string.IsNullOrWhiteSpace(botId))
            return false;

        lock (_syncRoot)
        {
            if (!_byBotId.TryGetValue(botId, out var lease) || lease.Generation != generation)
                return false;

            var stopping = lease with { Stopping = true };
            _byBotId[botId] = stopping;
            if (_byTokenFingerprint.TryGetValue(stopping.TokenFingerprint, out var tokenLease) &&
                tokenLease.Generation == generation)
            {
                _byTokenFingerprint[stopping.TokenFingerprint] = stopping;
            }

            return true;
        }
    }

    /// <summary>Lists every active lease so a startup or shutdown diagnostic can prove the one-loop-per-token invariant.</summary>
    /// <returns>A point-in-time copy ordered by internal bot id; empty when nothing is polling.</returns>
    /// <remarks>The copy is safe to log outside the lock and contains no credential, only fingerprint prefixes.</remarks>
    public IReadOnlyList<TelegramPollingLease> Snapshot()
    {
        lock (_syncRoot)
        {
            return _byBotId.Values
                .OrderBy(lease => lease.BotId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Number of active leases; equals the number of polling loops this process believes it owns.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _byBotId.Count;
            }
        }
    }
}

/// <summary>
/// One acquired polling lease: the identity that owns the only long-polling loop for a bot token.
/// </summary>
/// <param name="BotId">Internal runtime bot id that owns the loop.</param>
/// <param name="TokenFingerprint">Truncated SHA-256 fingerprint prefix of the token being polled; never the token.</param>
/// <param name="Generation">Process-local generation number that owns the lease.</param>
/// <param name="ProcessId">Operating-system process id that acquired the lease.</param>
/// <param name="Reason">Non-secret acquire or release reason used for diagnostics.</param>
/// <param name="AcquiredAtUtc">UTC instant the lease was acquired; used for incident timelines.</param>
/// <param name="ReleasedAtUtc">UTC instant the lease was released, or <c>null</c> while it is still active.</param>
/// <param name="Stopping">
/// Whether the owning generation has begun an intentional stop and its loop is expected to end. A restarter may wait for
/// such a lease; a lease that is not stopping is a hard duplicate rejection.
/// </param>
/// <remarks>
/// This is a value record so it can be copied safely while the registry keeps mutating its own dictionaries under lock.
/// </remarks>
public sealed record TelegramPollingLease(
    string BotId,
    string TokenFingerprint,
    long Generation,
    int ProcessId,
    string Reason,
    DateTime AcquiredAtUtc,
    DateTime? ReleasedAtUtc = null,
    bool Stopping = false)
{
    /// <summary>An empty lease used to describe a rejection that has no holder, such as a missing identity.</summary>
    public static TelegramPollingLease Empty { get; } =
        new(string.Empty, string.Empty, 0, 0, string.Empty, default);
}

/// <summary>Closed vocabulary describing why a polling attempt was refused.</summary>
public static class TelegramPollingLeaseRejection
{
    /// <summary>The requested internal bot id was null, empty, or whitespace.</summary>
    public const string MissingBotId = "missing_bot_id";

    /// <summary>The token fingerprint was empty, so the credential could not be deduplicated.</summary>
    public const string MissingTokenFingerprint = "missing_token_fingerprint";

    /// <summary>This internal bot is already polling in this process.</summary>
    public const string BotAlreadyPolling = "bot_already_polling";

    /// <summary>A different internal bot is already polling the same Telegram token.</summary>
    public const string TokenAlreadyPolling = "token_already_polling";

    /// <summary>No rejection; the attempt was accepted. Used only as the default for accepted results.</summary>
    public const string None = "none";
}

/// <summary>
/// Outcome of one single-flight polling lease acquisition.
/// </summary>
/// <param name="Acquired">Whether the caller now owns the lease and may start polling.</param>
/// <param name="Lease">The acquired lease, or the conflicting holder's lease when refused.</param>
/// <param name="RejectionReason">Closed-vocabulary reason, or <see cref="TelegramPollingLeaseRejection.None" /> when accepted.</param>
/// <remarks>
/// Callers must treat a refusal as permanent for that attempt and never retry in a loop; the receiver manager reports it
/// so the duplicate token or duplicate bot remains visible instead of being retried into a conflict storm.
/// </remarks>
public sealed record TelegramPollingLeaseResult(bool Acquired, TelegramPollingLease Lease, string RejectionReason)
{
    /// <summary>Internal bot id that currently holds the conflicting lease; empty when no holder applies.</summary>
    public string HeldByBotId => Lease?.BotId ?? string.Empty;

    /// <summary>Token fingerprint held by the conflicting lease; empty when no holder applies.</summary>
    public string HeldByTokenFingerprint => Lease?.TokenFingerprint ?? string.Empty;

    /// <summary>Generation that holds the conflicting lease; zero when no holder applies.</summary>
    public long HeldByGeneration => Lease?.Generation ?? 0;

    /// <summary>Process id that holds the conflicting lease; zero when no holder applies.</summary>
    public int HeldByProcessId => Lease?.ProcessId ?? 0;

    /// <summary>Creates an accepted result that owns the supplied lease.</summary>
    /// <param name="lease">Lease the caller now owns.</param>
    /// <returns>An accepted result whose <see cref="Acquired" /> flag is <c>true</c>.</returns>
    /// <remarks>
    /// Named <c>Grant</c> rather than <c>Acquired</c> because a record property and a static factory cannot share one
    /// member name; the property <see cref="Acquired" /> remains the value callers read.
    /// </remarks>
    public static TelegramPollingLeaseResult Grant(TelegramPollingLease lease)
        => new(true, lease, TelegramPollingLeaseRejection.None);

    /// <summary>Creates a refused result naming the holder that caused the refusal.</summary>
    /// <param name="reason">Closed-vocabulary rejection reason.</param>
    /// <param name="lease">Current holder's lease, or <see cref="TelegramPollingLease.Empty" /> when none applies.</param>
    /// <returns>A refused result.</returns>
    public static TelegramPollingLeaseResult Rejected(string reason, TelegramPollingLease lease)
        => new(false, lease ?? TelegramPollingLease.Empty, reason);
}
