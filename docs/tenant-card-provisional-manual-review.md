# Safe recovery of provisional tenant-card purchases

## Scope and operator workflow

Only global Super Admins in a private, enabled **owned** bot can open
`🛠 بررسی دستی سفارش‌های موقت`. Tenant owners, colleagues and customers cannot use it.
The screen lists unresolved `tenant_card` purchase finalizations and accepts an exact public OrderId search.
It is separate from renewal Manual Review and from missing-receipt recovery.

1. Select `🔍 بررسی مجدد پنل`. This performs database reads and authenticated GETs only.
2. Correct the original panel problem through normal authorized administration if necessary.
   A failed GET, mismatched email/UUID/SubId, mismatched active-service inbounds or uncertain payment refuses recovery.
3. `✅ بازیابی و تکمیل سفارش` appears only after safe proof. It prepares a durable ten-minute confirmation,
   bound to the administrator, private chat, owned host, exact order/receipt/identity, frozen entitlement and snapshot.
4. A **separate second confirmation** repeats live proof under the existing order fulfillment gate.
5. Check the actual durable outcome. `fulfilled` requires panel proof plus the normal financial/order commit.
   A panel-success/settlement-failure result is **not** fulfilled; prepare another fresh explicit review.

Ordinary `Verify payment` never clears manual review or manufactures another receipt approval for these orders.
No production order id, client id or inbound list is hardcoded in this implementation.

## Safety boundaries

- An approved, finally confirmed receipt must uniquely match the order, storefront, owner, customer and amount.
  The saved provisional creation must be `Applied` on the configured panel with the same credentials.
- Initial recovery refuses existing owner wallet receipts, tenant/wallet ledgers, funding routes or website debit artifacts.
  Revocation, ordinary creation, unresolved renewal and link-change operations also refuse admission.
- Reviewed authorization freezes receipt evidence: linked receipt versions change in the claim transaction, stale workflow
  writes fail optimistic validation, and subsequent owner approval/rejection or customer replacement is refused.
  Resolving a disputed payment after this point requires support review, not rewriting receipt evidence mid-finalization.
- Only a proven **before-first-mutation placement failure** may reopen at `claimed`; the exact client must still carry
  its 1 GiB temporary quota. Frozen purchased quota and absolute expiry are preserved, not extended from today.
- Partial/ambiguous mutations never rewind. Complete read-only frozen-target proof can enter `proven`; otherwise
  manual review stays locked. A timeout never grants permission to POST again.
- Reviewed dispatch records `MutationPending` before each mutation and uses `NoAutomaticRetry` transport.
  A crash/timeout after intent only permits GET reconciliation. A new explicit review is required after interruption.
- Already-proven reviewed delivery is rechecked without clearing legitimately accrued paid usage. Settlement resumes
  only through the central tenant fulfillment path and its existing local/site wallet keys, unique tenant ledger and
  notification intents. Sending a notification never grants another financial effect.
- Changing the live feature flag cannot send reviewed recovery into ordinary new-account creation.
- Administrator review records retain original reason, actor, UTC timestamps, sanitized proof and outcome. No UUID,
  SubId, panel credentials, raw response or comment is copied into review callbacks/audits.
- The runtime retains its existing single-process deployment assumption and in-process per-order fulfillment gate.
  Do not run multiple production poller/application instances against the same databases.
- Existing finalization does not promote the provisional plan metadata in the panel comment. This focused recovery
  patch preserves that behavior; reminder exclusion by the retained provisional PlanKey is a separate follow-up concern.

## Migration, deployment and validation

Migration `20261030113439_AddTenantCardProvisionalReviewedRecovery` adds four saga fields and an **empty** review table
with order/time and status/expiry indexes. Existing steps, frozen quota/expiry, receipt/payment, wallet and ledger rows
are not rewritten. No backfill, automatic recovery, debit or panel request runs during migration/startup.

Before deploying, stop/drain the single application and create verified, matching SQLite online backups of **both**
`users.db` and `credentials.db` (SQLite backup API / `.backup`, or copy only after a clean stop/checkpoint).
Never copy just a live main `.db` file while ignoring its WAL. Retain protected configuration and the previous binary.
Validate backup integrity and record the commit/version before resuming the normal deployment/migration pipeline.

After deployment, verify the migration receipt and unchanged wallet/order totals, then GET-reprobe the affected order.
Recovery is an explicit operator action, never part of deployment. Recheck the same email/UUID/SubId, exact frozen
quota/expiry, fulfilled order, one owner debit receipt, one tenant ledger and queued notifications after approval.

For rollback, stop new recovery admissions and drain/stop the application first. **Do not downgrade the schema or restore
old financial databases after any recovery effect.** A historical binary does not understand reviewed mutation markers
and may reset paid usage if Verify payment is retried. Prefer a forward fix; if rolling back binaries is necessary,
keep affected orders quarantined and prohibit their fulfillment until a reviewed-compatible version is restored.
Only a pre-effect rollback may restore the matching stopped backups; externally applied panel effects cannot be undone
by restoring SQLite. Never manually clear an error/step/marker to force a retry.

## Verification

`Adminbot.Tests/TenantCardProvisionalManualReviewTests.cs` uses isolated SQLite and `FakeUpstreamPanel`, including
corrected placement, GET-only reprobe, central settlement, concurrent/replayed approval, forged/private-host controls,
stale/expired confirmations, payment/identity/financial/competing-operation refusals, additive migration,
ambiguous committed update and restart after panel success/ledger failure with paid usage preserved.

Run the existing test project, Release application build, Linux framework-dependent publish, migration pending-model
check and source-text diff checks. No live production database or panel is used by these tests.
