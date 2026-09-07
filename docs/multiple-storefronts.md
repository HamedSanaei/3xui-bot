# Multiple storefronts with shared owner accounts

## Owner suspension and automatic debt repayment

All messages and callbacks are gated by the fresh shared owner profile. A blocked owner takes priority and produces
`ربات به علت تخلف مسدود است. به پشتیبانی پیام دهید.` without a support id/link. Receivers stay alive to answer;
manual store Enabled remains unchanged and therefore still applies after the owner is unblocked.

For an unblocked owner, positive local wallet balance allows access. Otherwise a readable usable website wallet of
at least 1,000,000 toman is required (exactly 1,000,000 qualifies). No connection or a failed read means local wallet
balance alone decides. Restricted customers receive `ربات به علت بدهی غیرفعال است. به پشتیبانی پیام دهید.`
Owned payment/charge flows and already-paid webhook/recovery work are not gated.

On customer interaction, negative local balance triggers repayment of min(local debt, usable website funds), including
partial repayment. The immutable owner-wide transfer is reserved before website I/O. Website admission rechecks funds
and local debt; no SQLite transaction spans the network. Successful website receipts authorize a separate idempotent
local credit, not a purchase or profit. The ledger reason is `owner_debt_settlement`.

One pending transfer per owner prevents sibling stores from starting another. Missing/sending website receipts never
authorize credit or an automatic retry. Review such transfers against website evidence; never mark applied based on
current balances alone. Confirmed receipts resume local credit through the existing periodic wallet recovery worker,
including after restart. A deposit arriving during remote I/O may make the resulting local wallet positive; confirmed
transferred funds are still credited exactly once. This is not a distributed transaction with the website.

Before deployment, stop the single polling process, take a coordinated backup of credentials.db and users.db using
the procedure below, and apply migrations before receivers start. `20260907120000_TenantDebtTransfers` creates empty
storage and does not replay historical debt. Its downgrade refuses to erase any transfer history. Monitor pending
transfers, credit recovery warnings, and website contention. No automatic deployment is part of this change.

Owned and tenant menus now display `🌟اکانت تست`; old free-account buttons remain input aliases only. Trial eligibility,
volumes, duration and bot/user cooldown remain unchanged. This increment is reviewed statically and with one Release
build only: no tests added/run, no executable migration validation, no publish, commit, push or deployment.
This increment's `dotnet build Adminbot.csproj -c Release --no-restore -v q` passed: zero warnings and errors.
Static diff/UTF-8 checks passed with no corruption markers. These checks do not establish runtime financial or
migration behavior; the earlier test/publish results below predate the owner-access/debt-transfer change.

`TenantMaxStoresPerOwner` is positive and defaults to 5. Every allocated row counts, even when disabled or reset.
Lowering the limit never disables an existing store. Reuse a disabled store instead of deleting its history.
The owner menu lists stores; every panel/prompt identifies its selected store. Gateway enable flags, support,
mandatory channels, welcome, markup, card details and tutorials are local to that store. Online API keys and
global gateway switches remain central. All stores share the owner's credentials.db wallet and Gozargah account.

## Financial recovery

An order retains its owner, originating tenant and customer separately. Online gateway profit goes to the owner
wallet. Card base cost uses local funds first, then an eligible website wallet, then the existing local overdraft
policy. `TenantWalletRoute` persists the source before the first effect so a retry cannot switch wallets merely
because balances changed. Local wallet receipts and ledger keys remain order-specific.
Historical unfinished card orders with saved panel-success evidence receive route `review` during migration.
A matching committed local debit receipt can resolve that route to `bot` without charging again. Otherwise the
exact order requires operator reconciliation; no new source is guessed from current balances.

`SiteWalletDebitOperation` records a business key before the single `deduct_wallet` call. `applied` contains the
confirmed before/after receipt. `sending` means the result is unresolved after a timeout, malformed response,
failed local receipt commit or process loss. It does **not** prove that the website failed to debit.

- Inspect unresolved rows by id, owner, amount and creation time; never dump bot tokens or message payloads.
- Compare the exact event against website transaction records. Do not infer absence from today's balance,
  repeat POST, refund automatically, debit another wallet, or delete the marker to make retries run.
- When the website authoritatively proves the debit, preserve that evidence in the operator audit, then record
  the verified before/after balances and `applied` status for that exact row. Retry the linked order's existing
  settlement/recovery path, not XUI creation. There is no automatic operator-resolution UI in this change.
- If the result cannot be proved, keep the operation for review. Other stores and Telegram menus remain available.
  Historical ambiguous operations must also be reconciled before enabling new financial traffic.

The application coordinates website balance checks and debits by owner in one process. It does not control
website-side writers or provide a distributed transaction with the website or between the two SQLite databases.

## Deployment (manual)

1. Run full tests, Release build and both `dotnet ef migrations has-pending-model-changes --context ...` checks.
   Publish to a new release directory with
   `dotnet publish -c Release -f net10.0 -r linux-x64 --self-contained false`.
2. Run the published migration preflight on fresh databases and backup copies of the current databases:
   `Adminbot --migration-check --users-source <copy-users.db> --credentials-source <copy-credentials.db>`.
   This mode starts no Telegram receiver or web server. Follow the immutable-release procedure in `deployment.md`.
3. Stop the polling service and all webhook/background writers before the final coordinated backup. Capture
   **both** users.db and credentials.db from this stopped state using SQLite online backup, and retain the current
   release/configuration. Do not copy only the main files while WAL writers are active. Keep the Telegram log outbox
   database with the backup set as described in `backup-recovery.md`.
4. Keep exactly one polling process. Run migrations before receivers start (normal startup already migrates both
   contexts before host workers). Existing tenant ids and balances stay intact; legacy owner conversations reopen
   the list because old input lacks a store target. A duplicate historical Telegram identity fails migration/startup
   for review rather than choosing a winning owner silently.
5. Verify each existing store, then allocate another store and configure its own token/channels/payment switches.
   Check shared owner balance display, store-specific orders, receiver status, queue pressure, SQLite retries and
   unresolved website debit markers. No deployment is performed by the implementation task.

Do not downgrade while new storefronts or wallet operations exist. The migration's Down guard refuses to erase
those identities or receipts. Resolve financial uncertainty and prepare a deliberate compatible recovery release;
never restore only one database or replay historical financial effects to force a rollback.

## Verification coverage

Tenant purchase and renewal audit messages display a fixed readable label from `PaymentProvider`, separately
from owner settlement funding. Unknown provider values are never echoed. Storefronts expose `🌟اکانت تست`
through the shared owned v3 trial handler: non-colleagues, verified sender-owned Iranian mobile, national 100 MiB
or normal 1 GiB, three days, thirty days per trial type and bot/user. `TelegramPhoneVerification` is shared;
rejection support and menus belong to the receiving store. Trials retain recipient, store and owner metadata
through the existing durable XUI creation boundary, with no financial order, owner debit or profit.

Before this incremental gateway/trial change, 146 tests, Release build, Linux publish, both model-drift checks
and migration preflight passed. Those results do not validate the incremental change. Its requested verification
is limited to source/diff and UTF-8 review plus one Release build; no new tests or repeated suite are requested.
The incremental `dotnet build Adminbot.csproj -c Release --no-restore -v q` passed with zero warnings/errors.
Diff whitespace checks and strict UTF-8/visible Persian review passed; no mojibake markers were found.
No incremental test run, publish, migration preflight, commit, push or deployment was performed.

`MultiStoreTests.cs` uses real temporary SQLite databases and the production handler/service graph. It covers
concurrent cap enforcement, add redelivery, lower limits, upgrade identity/balance preservation, input restart,
stale/wrong-owner callbacks, UTF-8 callback limits, customer state/broadcast scope, token identity uniqueness,
store reset and receiver independence. A loopback fake website exercises two orders with one owner, duplicate
settlement, insufficient funds and an applied-but-ambiguous response. Barrier tests prove independent database
writes and other owners progress while a website debit waits. Existing XUI recovery and scheduler tests remain active.
