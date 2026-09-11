using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Regression coverage for the durable step state that makes the tenant card-to-card provisional finalize and revoke
/// sagas restart-safe.
/// </summary>
/// <remarks>
/// The saga mutates a live XUI client in several steps: disable, official traffic reset, re-disable, zero the shared
/// usage row, write the exact purchased quota, prove the final state. Each step is a real external effect, so the naive
/// alternatives are both wrong: replaying a step after an ambiguous response can double-apply it, and skipping a step
/// based on in-memory progress loses the effect when the process restarts mid-saga.
///
/// The invariant these tests pin is that the durable row, and only the durable row, decides what may happen next:
/// steps move strictly forward, an already-recorded step refuses to advance again, a terminal manual review can never be
/// left behind, and a fresh store instance over the same database sees exactly the same progress as the one that wrote
/// it.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Builds a saga operation key exactly as the finalize path does.</summary>
    /// <param name="kind">One of the <see cref="TenantCardProvisionalOperationKinds" /> values.</param>
    /// <param name="publicOrderId">Public tenant order id.</param>
    /// <returns>The stable operation key for that order and kind.</returns>
    private static string ProvisionalOperationKey(string kind, string publicOrderId)
        => $"tenant-card-{kind}:{publicOrderId}";

    /// <summary>
    /// Proves a saga claim is durable and idempotent: the first claim creates the row at the first step, and a repeated
    /// claim returns the same in-flight row instead of authorizing a second mutation sequence.
    /// </summary>
    [Fact]
    public async Task Claim_creates_once_and_repeated_claim_resumes_the_same_row()
    {
        using var databases = new Databases();
        var store = new TenantCardProvisionalOperationStore(databases.Users);
        var key = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Finalize, "T-1001");

        var first = await store.ClaimAsync(key, 7, "T-1001", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        Assert.True(first.Created);
        Assert.Equal(TenantCardProvisionalSteps.Claimed, first.Operation.Step);

        var second = await store.ClaimAsync(key, 7, "T-1001", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        Assert.False(second.Created);
        Assert.Equal(TenantCardProvisionalSteps.Claimed, second.Operation.Step);

        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(1, await db.TenantCardProvisionalOperations.CountAsync());
    }

    /// <summary>
    /// Proves one operation key cannot be rebound to a different order, so a historical row can never authorize a
    /// mutation against another order after a data import or a mistaken key construction.
    /// </summary>
    [Fact]
    public async Task Claim_rejects_a_key_rebound_to_another_order_or_kind()
    {
        using var databases = new Databases();
        var store = new TenantCardProvisionalOperationStore(databases.Users);
        var key = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Finalize, "T-1002");
        await store.ClaimAsync(key, 8, "T-1002", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClaimAsync(key, 9, "T-1002", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClaimAsync(key, 8, "T-1002", TenantCardProvisionalOperationKinds.Revoke, CancellationToken.None));
    }

    /// <summary>
    /// Proves steps advance strictly forward and that replaying an already-recorded step reports failure, which is what
    /// lets the saga skip a mutation whose effect is already durably proven.
    /// </summary>
    /// <remarks>
    /// The rewind assertion is the important one: if a restart could move the row backwards, a replayed step would be
    /// issued again after an ambiguous outcome.
    /// </remarks>
    [Fact]
    public async Task Steps_advance_forward_only_and_replays_are_refused()
    {
        using var databases = new Databases();
        var store = new TenantCardProvisionalOperationStore(databases.Users);
        var key = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Finalize, "T-1003");
        await store.ClaimAsync(key, 10, "T-1003", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);

        Assert.True(await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, null, CancellationToken.None));
        Assert.True(await store.AdvanceAsync(key, TenantCardProvisionalSteps.Reset, null, CancellationToken.None));
        Assert.True(await store.AdvanceAsync(key, TenantCardProvisionalSteps.Zeroed, null, CancellationToken.None));

        // Replaying an already-recorded step must not advance and must not rewind.
        Assert.False(await store.AdvanceAsync(key, TenantCardProvisionalSteps.Reset, null, CancellationToken.None));
        Assert.False(await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, null, CancellationToken.None));

        var row = await store.FindAsync(key, CancellationToken.None);
        Assert.Equal(TenantCardProvisionalSteps.Zeroed, row.Step);
        Assert.NotNull(row.ResetAtUtc);
        Assert.NotNull(row.ZeroedAtUtc);
        // The finalize saga never disables the client, so the revoke-only disable timestamp stays unset.
        Assert.Null(row.DisabledAtUtc);
    }

    /// <summary>
    /// Proves a restart re-derives the next action from durable evidence: a brand-new store instance over the same
    /// database observes the furthest proven step rather than starting over.
    /// </summary>
    [Fact]
    public async Task Restart_resumes_from_the_furthest_durably_proven_step()
    {
        using var databases = new Databases();
        var key = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Finalize, "T-1004");

        var first = new TenantCardProvisionalOperationStore(databases.Users);
        await first.ClaimAsync(key, 11, "T-1004", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        await first.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, "{\"enable\":\"true\"}", CancellationToken.None);
        await first.AdvanceAsync(key, TenantCardProvisionalSteps.Reset, null, CancellationToken.None);
        await first.AdvanceAsync(key, TenantCardProvisionalSteps.Zeroed, "{\"up\":0,\"down\":0}", CancellationToken.None);

        // Simulates a process restart: a new store over the same migrated database.
        var restarted = new TenantCardProvisionalOperationStore(databases.Users);
        var row = await restarted.FindAsync(key, CancellationToken.None);

        Assert.Equal(TenantCardProvisionalSteps.Zeroed, row.Step);
        Assert.True(TenantCardProvisionalSteps.HasReached(row.Step, TenantCardProvisionalSteps.Reset));
        Assert.False(TenantCardProvisionalSteps.HasReached(row.Step, TenantCardProvisionalSteps.QuotaWritten));
        // The sanitized evidence for the recorded step survives the restart.
        Assert.Contains("\"up\":0", row.EvidenceJson);
    }

    /// <summary>
    /// Proves manual review is terminal, so an ambiguous panel outcome can never be resolved by blind replay.
    /// </summary>
    [Fact]
    public async Task Manual_review_is_terminal_and_blocks_further_advances()
    {
        using var databases = new Databases();
        var store = new TenantCardProvisionalOperationStore(databases.Users);
        var key = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Finalize, "T-1005");
        await store.ClaimAsync(key, 12, "T-1005", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, null, CancellationToken.None);

        Assert.True(await store.MarkManualReviewAsync(key, "provisional_identity_ambiguous", CancellationToken.None));
        // Recording manual review twice is a no-op rather than an error, so a retried saga cannot double-write.
        Assert.False(await store.MarkManualReviewAsync(key, "provisional_identity_ambiguous", CancellationToken.None));

        // No later saga step may run once a human is required.
        Assert.False(await store.AdvanceAsync(key, TenantCardProvisionalSteps.Reset, null, CancellationToken.None));
        Assert.False(await store.AdvanceAsync(key, TenantCardProvisionalSteps.Proven, null, CancellationToken.None));

        var row = await store.FindAsync(key, CancellationToken.None);
        Assert.Equal(TenantCardProvisionalSteps.ManualReview, row.Step);
        Assert.Equal("provisional_identity_ambiguous", row.ErrorCode);
        Assert.NotNull(row.CompletedAtUtc);
    }

    /// <summary>
    /// Proves two concurrent claims for the same order produce exactly one mutation authorizer.
    /// </summary>
    /// <remarks>
    /// A duplicate Telegram approval or a replayed receipt can drive two claims at once. Exactly one may proceed to the
    /// panel sequence; the loser must observe <c>Created == false</c> and resume instead of racing a second saga.
    /// </remarks>
    [Fact]
    public async Task Concurrent_claims_produce_exactly_one_authorizer()
    {
        using var databases = new Databases();
        var key = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Revoke, "T-1006");
        var storeA = new TenantCardProvisionalOperationStore(databases.Users);
        var storeB = new TenantCardProvisionalOperationStore(databases.Users);

        var results = await Task.WhenAll(
            storeA.ClaimAsync(key, 13, "T-1006", TenantCardProvisionalOperationKinds.Revoke, CancellationToken.None),
            storeB.ClaimAsync(key, 13, "T-1006", TenantCardProvisionalOperationKinds.Revoke, CancellationToken.None));

        Assert.Equal(1, results.Count(r => r.Created));

        await using var db = databases.Users.CreateDbContext();
        Assert.Equal(1, await db.TenantCardProvisionalOperations.CountAsync());
    }

    /// <summary>
    /// Proves the step ordering helper is total and fail-closed for unknown values.
    /// </summary>
    /// <remarks>
    /// An unrecognized stored step must sort above every mutable step and therefore report as having reached all of them.
    /// The consequence is deliberate: corrupted or future state makes the saga skip replays and escalate to a human
    /// rather than treating the row as an early step and reissuing panel mutations it should never repeat.
    /// </remarks>
    [Fact]
    public void Unknown_steps_sort_last_and_never_look_reachfuly_early()
    {
        Assert.True(TenantCardProvisionalSteps.OrderOf("not_a_real_step") == int.MaxValue);
        // Fail-closed direction: an unknown step reports every mutable step as already reached.
        Assert.True(TenantCardProvisionalSteps.HasReached("not_a_real_step", TenantCardProvisionalSteps.Claimed));
        Assert.True(TenantCardProvisionalSteps.HasReached("not_a_real_step", TenantCardProvisionalSteps.QuotaWritten));
        Assert.True(TenantCardProvisionalSteps.HasReached("not_a_real_step", TenantCardProvisionalSteps.ManualReview));
        Assert.True(TenantCardProvisionalSteps.HasReached(
            TenantCardProvisionalSteps.Proven, TenantCardProvisionalSteps.QuotaWritten));
        Assert.True(TenantCardProvisionalSteps.HasReached(
            TenantCardProvisionalSteps.ManualReview, TenantCardProvisionalSteps.Proven));
        // A terminal proof step is not silently treated as satisfying manual review.
        Assert.False(TenantCardProvisionalSteps.HasReached(
            TenantCardProvisionalSteps.Proven, TenantCardProvisionalSteps.ManualReview));
    }

    /// <summary>
    /// Proves a rejected provisional revoke and a finalize saga for the same order are independent durable rows, so the
    /// claim on one kind never authorizes the other.
    /// </summary>
    [Fact]
    public async Task Finalize_and_revoke_are_independent_operations_for_one_order()
    {
        using var databases = new Databases();
        var store = new TenantCardProvisionalOperationStore(databases.Users);
        var finalizeKey = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Finalize, "T-1007");
        var revokeKey = ProvisionalOperationKey(TenantCardProvisionalOperationKinds.Revoke, "T-1007");

        Assert.True((await store.ClaimAsync(finalizeKey, 14, "T-1007", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None)).Created);
        Assert.True((await store.ClaimAsync(revokeKey, 14, "T-1007", TenantCardProvisionalOperationKinds.Revoke, CancellationToken.None)).Created);

        // Advancing the revoke must not move the finalize row.
        await store.AdvanceAsync(revokeKey, TenantCardProvisionalSteps.Disabled, null, CancellationToken.None);
        await store.AdvanceAsync(revokeKey, TenantCardProvisionalSteps.Revoked, null, CancellationToken.None);

        var finalize = await store.FindAsync(finalizeKey, CancellationToken.None);
        var revoke = await store.FindAsync(revokeKey, CancellationToken.None);
        Assert.Equal(TenantCardProvisionalSteps.Claimed, finalize.Step);
        Assert.Equal(TenantCardProvisionalSteps.Revoked, revoke.Step);
    }
}
