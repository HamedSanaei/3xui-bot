# Immutable production deployment

## Incident finding

The outage could not be reproduced from either known clean commit. At the start of this repair,
`8ef69f87281b237998cf596741df8eec56872eb7` passed the EF 10.0.8 pending-model check for both contexts, and its diff
from known-good `1e19198f0e019d3395bb9425b0c5326742cc4e74` contains no entity, property, index, constraint, migration, or model
snapshot change. Consequently there is no honest schema member to name as the mismatch from committed source. The
available evidence identifies the old production checkout's hybrid/dirty source composition as the root deployment
integrity failure: the compiled current model and copied migration/snapshot files did not come from one provable commit.
The guards below reject both real model drift and that hybrid-source condition.

No production artifact may be activated unless its source commit is known, its source tree is clean, the complete
test suite passes, both EF contexts have no pending model changes, real historical migrations succeed, and the exact
published executable passes migration preflight against fresh databases and online-backup copies of production-shaped
databases.

Release artifacts live under `/opt/vpnetiran/releases/<commit-sha>/`. Persistent state lives under
`/opt/vpnetiran/shared/Data/`, and every release's `Data` entry is a symlink to that directory. Persistent state includes
`users.db`, `credentials.db`, `telegram-log-outbox.db`, `configuration.json`, production certificates, and the runtime
`xui-v3-service-plans.json`. These files are excluded from publish and must never be populated or overwritten from its
output. Compiled binaries, runtime libraries, and static application assets are immutable release artifacts.

Prepare the service once with `WorkingDirectory=/opt/vpnetiran/current` and
`ExecStart=/opt/vpnetiran/current/Adminbot`. Use `Restart=on-failure`, `RestartSec=10`, `StartLimitIntervalSec=300`, and
`StartLimitBurst=3` so a programming error cannot restart indefinitely. Keep `/opt/vpnetiran/previous` and do not delete
the prior known-good release during activation. Before using the automated deploy script for the first time, bootstrap
`current` to the already running known-good release; activation refuses to stop systemd without a valid rollback target.

From a clean clone at the requested SHA, run:

```bash
bash scripts/deploy-release.sh /path/to/clean/clone <40-character-commit-sha> /opt/vpnetiran vpnetiranbot.service
```

The script refuses a dirty or mismatched checkout, restores the pinned EF tool, builds, runs all tests and both pending
model checks, publishes into a new staging directory, runs the published executable's fresh and production-copy
preflights, then creates the immutable release. Only after every validation succeeds does it stop the healthy service,
atomically switch `current`, and restart. A failed preflight never calls systemd or changes `current`; a failed health
check restores `current` to the previous validated release and restarts it.

The application logs `[Build] Commit=<sha>` and `[Build] Configuration=Release` from assembly metadata. The migration
preflight starts no web server, Telegram receiver, hosted worker, or remote logger:

```bash
/opt/vpnetiran/releases/<sha>/Adminbot --migration-check \
  --users-source /opt/vpnetiran/shared/Data/users.db \
  --credentials-source /opt/vpnetiran/shared/Data/credentials.db
```

## Synchronized-deployment gate (`scripts/deploy-production.sh`)

The workflow verifies every push, pull request and manual dispatch on an isolated Linux Actions runner.
Production activation is **manual only**: choose `main` and set `verify_only=false`. The default is verification
without deployment; feature branches and pull requests never receive production secrets or run SSH/systemd.

`scripts/verify-release.sh <40-character-sha> <fresh-output-directory-outside-checkout>` rejects dirty or mismatched
source and runs the complete gate: pinned EF tool restore, solution restore/build, full test discovery and execution,
explicit completed TRX validation, both EF pending-model checks, application-only `linux-x64` publish, tutorial-asset
validation, and the exact published executable's fresh-database `--migration-check`.
Every discovered test must execute and pass; a failed, empty, skipped, incomplete or aborted run cannot approve an
artifact even if its command returns zero. The failure-propagation fix from `45fc9c5` remains in the bounded command
orchestration; Bash errexit alone is not relied on inside conditional subshells.

The gate packages only runtime files into `release.tar.gz`, with an exact commit/runtime manifest and per-file
SHA-256, size and executable-mode checks. It rejects Data/configuration/databases/tokens/private keys, test assemblies,
source files, links and unsafe archive paths. Actions uses the immutable artifact ID and independent archive checksum,
not a mutable artifact name or a production build. The verification job downloads that exact ID and revalidates its
checksum/manifest, exercising the Actions transport even on feature branches without Production access.
Discovery and TRX evidence are retained for seven days, including failed gates. A failed verification job prevents
the deployment job from starting.

Production receives fixed files over bounded SCP/SSH, verifies the archive and manifest again, and runs only the
published executable's **copy-only** preflight against online backups of the live
`/root/vpnetiran/bin/Release/net10.0/linux-x64/publish/Data/users.db` and `credentials.db`.
No SDK, build, test or source-tree synchronization occurs on Production. Before preflight and synchronization succeed,
systemd is untouched. Runtime synchronization preserves Data, configurations, token/key files and databases/WAL,
including historical files outside Data. Service and canonical runtime paths remain unchanged.

Production prerequisites: the .NET/ASP.NET runtime used by `Adminbot`, Python 3, GNU `timeout`, `rsync`, `flock`,
coreutils, systemd and working SCP/SFTP. Commands have process-group deadlines: restore 3–5 minutes, build/publish
15 minutes each, full tests 30 minutes with a five-minute hang detector, EF/migration preflights three minutes each,
artifact validation two minutes, deployment lock one minute and remote activation fifteen minutes.
The deploy job is bounded at twenty minutes. Archive or preflight failure never reaches synchronization/restart;
failed synchronization never reaches restart.

`bash scripts/deploy-production.tests.sh` exercises fail-closed gate orchestration, aborted/empty test proof,
artifact tampering/traversal/link rejection, real timeout and rsync preservation behavior without Production access.
For rollout, review the branch's green verification and merge it; merging alone does not deploy. Take the normal
verified production backup, confirm the prerequisites, then manually run the workflow on `main` with
`verify_only=false` and any required production-environment approval. Keep the previous known-good runtime artifact
for rollback. Do not bulk-reset inbox, payment, order or uncertain-delivery rows.

## SQLite scan contention and receipt acknowledgement recovery

The 2026-10-07 incident reported simultaneous receipt, funding-alert, discount and payment-notification scan failures
with SQLite error 5 (`database is locked`) and a Kestrel thread-pool starvation warning. Read-only server inspection
found WAL already enabled, one application process and mostly terminal notification queues. The exact long-held
writer at the incident time is not identified by the available stack; a cleanup-time exception does not prove the
preceding statement was unapplied.

Two code paths amplified contention. Even a zero-row UPDATE/DELETE acquires SQLite's single writer, and the provider's
async command API performs synchronous native work. Idle lease/expiry/retention writes therefore blocked otherwise
empty scans. AtlasPay tenant audit submission also waited for the separate durable logger outbox while retaining the
users.db writer inside a retryable transaction.

The corrected payment, tenant-receipt, funding-alert and discount scans first perform a read-only eligibility check
and retain the full predicate on the eventual mutation. Work becoming eligible after an empty read waits for the
next cycle. Funding retention deletes at most one 100-row batch without replaying an ambiguously applied DELETE.
Eligible writes can still report genuine contention; worker error reporting, WAL, pooling and native timeouts are
unchanged. Increasing Telegram concurrency or retrying entire scans is not a substitute for short write boundaries.

AtlasPay audits now materialize eligible payment/order snapshots, dispose the read context, and admit the log without
a users.db transaction. A per-payment gate serializes callers in the single application process. Only the idempotent
marker update can retry; logger admission cannot repeat inside a database retry. The logger outbox and users.db are
not crash-atomic: a process crash or exhausted marker write after admission can still produce an audit replay during
recovery. No account, balance, owner profit or ledger operation is repeated by audit recovery.

If Telegram accepts a tenant receipt but its message-id acknowledgement fails to persist, the worker quarantines
only its original Processing/claim-token pair as DeliveryUncertain. If that write also fails, it retains the lease;
expiry also becomes DeliveryUncertain, never Pending. An acknowledgement already applied before provider cleanup
failed remains Delivered. Verify the Sales Assistant's actual message before any manual retry; do not bulk-reset
Processing or DeliveryUncertain rows, because doing so can resend accepted receipts.

The October 10 reliability change additionally arbitrates users/credentials SQLite writers **before** native busy
waits. Reads remain concurrent; a bounded cancellable process-local queue gives inbox and active-handler persistence
up to eight turns before a waiting background write. Explicit transaction ownership is reentrant and retained through
reader/transaction cleanup. SQLite still enforces actual atomicity, constraints and cross-process isolation; this is
not a replacement transaction manager or permission to replay financial/external effects.
Queue-slot and writer-turn waits share the existing command/connection timeout, rather than waiting indefinitely.
Expiry remains SQLite BUSY (5) under the existing three-attempt policy; caller cancellation is not relabelled as BUSY.
An explicitly configured zero timeout preserves SQLite's unlimited-wait convention.
Inbox duplicates/full-capacity checks are read-only; capacity/deduplication are rechecked inside the short insertion
transaction. Payload serialization precedes writer admission, deserialization and wake/telemetry publication follow
context disposal. Idle inbox/order maintenance avoids zero-row writes. Synchronous handler prologues run off the
scheduler coordinator so independent lanes can dispatch; same-user FIFO is intentionally unchanged.

Historical 66–84 second queue waits can include multiple predecessor executions and persistence/dispatch delays.
Attribution reports clipped overlap, lane occupancy (claim through terminal receipt, **not handler-only time**) and
the wait outside the selected predecessor separately. It does not blame all waiting time on one handler or fabricate
a blocker when overlap is absent. Keep the detailed correlated telemetry when diagnosing any remaining long wait.

No new schema migration, database repair, pool change or manual WAL switch is required by this fix. The tenant
underfunding central-gateway fallback and personal-card funding admission rules remain unchanged. Use the normal
production deployment gates above; do not copy standalone smoke tooling or test assemblies into the server publish.

## Shared owned and tenant installation tutorial assets

`Assets/tutorials/{android_v2rayng,windows_v2rayn,ios_android_v2box}/` is application content, not persistent
`Data` state. Publish and synchronize all three directories with the release. The original numbered PNG slides
are retained without lossy recompression. Use a clean publish directory: old JPEG substitutes left beside the
original PNGs would make the image resolver send duplicate steps, because it accepts both formats.

Owned customers open `⚙️ مدیریت اکانت` → `📚 آموزش‌های متفرقه` → `💡راهنما نصب`, then select Android, iOS or Windows.
Tenant customers retain their existing installation entry and platform chooser. Both routes upload the same original
files; changing legacy per-bot tutorial URLs does not change these albums. Viewing guides preserves pending
purchase/APN input and does not create a purchase or change a wallet.

An album upload has a bounded 24-second foreground deadline; ordinary Telegram interaction still has eight seconds.
If `Installation tutorial album delivery failed` reports `send_media_group` timing out, check Telegram connectivity and the
number/size of shipped slides before changing any bot configuration. A timeout is ambiguous (Telegram may already
have received the group), so the application never re-sends it automatically. No database row stores tutorial progress
or proves recipient delivery.

## Foreground account latency and uncertain creation

Account-id selection, renewal preview, search-proof reload and pre-mutation snapshot reads use the existing
`xuiV3ForegroundReadTimeoutSeconds` budget: **12 seconds** when absent, at most **15 seconds**. Direct email lookup
and full-list fallback share one window; cancellation does not start another lookup. My-accounts renders one fresh
panel response, not a second full download. These limits do not change per-user FIFO or background read retries.

Ordinary, free-colleague and paid-colleague test creation uses the same configured duration as a **separate overall
creation network lifetime**, beginning after durable identity reservation. One POST, immediate GET-only recovery and
optional detail/config-link retrieval share that lifetime. This is not a foreground-read retry policy: **add POST
is still sent at most once**. Local receipt/cooldown/financial persistence and subsequent bounded Telegram delivery
are outside that network lifetime, so it is not a promise that the complete handler ends in twelve seconds.

- Without positive proof, deadline expiry stays ambiguous. Re-entering the same business key reads the reserved
  identity only; never delete/reset its receipt to issue a replacement POST.
- Accepted POST or exact-identity/inbound read-back persists Applied independently of the expired HTTP token.
  Optional configuration-link failure cannot downgrade that proof, refund a paid trial, or release its quota.
- `XuiMutation` identifies the creation POST wait; `BusinessRecovery` identifies read-back. `Stage=none` is an
  uninstrumented instant, not evidence that Telegram caused the delay.
- Owned/admin/paid-trial create/update website mirrors now persist locally and use the existing deferred sync worker.
  Website owner lookup/send no longer holds the interactive lane. Website-wallet debit remains synchronous and
  receipt-backed; explicit historical sync still reports completed remote results.

October 5 receipts show three slow free trials reached Applied despite their failed TLS response: roughly
32–41 seconds passed between POST authorization and `record layer failure`, then positive proof arrived within
0.9–1.5 seconds. Client cancellation cannot prove the server stopped creating the account. Investigate the panel,
reverse proxy/TLS terminator and network at **18:08:16.921, 18:17:49.822 and 19:00:58.819 UTC** in the supplied logs.
Those logs already show HTTP/1.1 and no connection reuse; they do not identify the physical TLS fault. Never disable
certificate verification, guess a TLS downgrade, or replay a business POST to diagnose it.

## Configuration change safety

`Data/configuration.json` is persistent runtime state, not a build artifact. It is excluded from publish and from
release synchronization, it is the only file that carries real panel credentials and deployment-specific values, and
no script in this repository writes, prunes, or regenerates it.

An outage was caused by a human pruning this file against a `configuration.example.json` taken from a different branch.
That procedure is now explicitly forbidden:

- **Never prune a real configuration against the example.** The example is an illustrative template, not a schema, and
  it deliberately omits deployment-specific sections. Pruning against it deletes keys production depends on; in the
  observed incident it removed `xuiV3ApiBaseUrl` and the per-bot `loggerChannel` / `backupChannel` values, which left
  the durable Telegram log outbox with unusable destinations.
- **Never rebuild the file from a key list.** `jq '{known keys}' production/configuration.json > new-file` silently
  drops every key that is not in the list. `jq` also rejects `//` comments, so it cannot be pointed at a documented
  template at all.
- **Make changes additive and targeted.** Edit only the keys you intend to change and leave every other line untouched.

`Data/configuration.example.json` states these rules in its own `_readme` block, which is the first key in the file. That
block is documentation rather than configuration: the configuration binder ignores keys it does not know, and the
application never reads the example at runtime.

Owned-bot colleague test allowance uses the additive `colleagueDailyFreeTrialLimit` key (default **3**).
Only nonnegative whole account counts are valid; **0** disables free admission but retains paid test purchase.
The allowance is global per active colleague's Telegram user across owned bots and both test types, resetting
at Tehran calendar midnight. Ordinary customers and tenant-bot trial cooldown/phone checks are unchanged.
After exhaustion, the same normal **1 GiB** or national **100 MiB**, **three-day** test is quoted using ordinary
colleague traffic/day rates, rounded upward to whole toman; an amount-bound inline approval is required before
wallet debit. Repricing does not authorize a higher debit through an old button. Refund recovery is receipt-linked,
independent of menu resets, and requires definitive non-creation; ambiguous creation remains held.
This option is validated and captured at startup: editing JSON alone does not activate the new behavior.
Deploy the matching application release/migration and restart through the normal release procedure.
Apply the key to the existing production JSON and source example without reconstructing either file.

Startup validation in `Domain/ConfigurationPreflight.cs` covers the failure class this incident exposed:

- `ValidateEnabledFeatures` is **fatal** and runs beside the per-feature validators in `Program.Main`, before dependency
  injection and before any database migration. It rejects an explicitly enabled feature whose required panel URL is
  missing or unusable, a structurally invalid `xuiV3ApiBaseUrl`, and `userDatabasePath` / `credentialsDatabasePath`
  resolving to the same file.
- `DescribeStartupReport` is **warn-only** and runs after bot hydration. It reports a missing Telegram logger channel, a
  missing backup channel, and a missing panel URL. These are warnings by design: the process must keep settling
  payments, running XUI work, and answering customers even when logging is misconfigured.

The validator never reads, rewrites, prunes, or backfills a configuration file, and it never rejects a key it does not
know: unknown keys are ignored by the configuration binder, so a newer configuration stays usable by an older build and
an older configuration stays usable by a newer build. Every message it produces names a configuration key and never
echoes a configured value, a chat id, a bot token, or a panel secret.

## Test-account logger channel controls

Configured super-admins use **🗽 Admin → 🔔 لاگ اکانت تست** in an owned bot to turn test-account
acquisition reports on or off globally for owned and tenant bots, including free and paid colleague tests.
The additive root key `trialAccountLoggingEnabled` defaults to **false** when absent, so deploying this
release stops new trial acquisition reports by default without requiring a production configuration rewrite.
The panel saves the exact boolean before applying the change immediately; the preference survives restart.
Manual JSON changes take effect at startup. Failed saves leave the current state unchanged and display an alert.

This controls only trial acquisition audits entering the Telegram logger channel. Trial issuance and eligibility,
ordinary purchases, other logs, local diagnostics and paid-trial financial backup intents are unchanged.
Previously queued reports may still arrive; disabling does not purge the durable outbox. Buttons expire after
ten minutes and reject stale revisions; use **🔄 تازه‌سازی** after another administrator changes the state.

## Global service sale and renewal controls

Configured super-admins can use **🗽 Admin → 📊 کنترل فروش و تمدید** in an owned bot to close or reopen
**sale** and **renewal** independently for normal, national and unlimited services. Every owned and tenant
storefront in the application reads the same live policy; a tenant owner or colleague cannot override it.
Changes take effect without restarting and persist through restart.

The six additive root keys are `normalSaleEnabled`, `normalRenewalEnabled`, `nationalSaleEnabled`,
`nationalRenewalEnabled`, `unlimitedSaleEnabled`, and `unlimitedRenewalEnabled`. Missing keys default to `true`.
Use the panel for live changes; manual JSON changes are startup values until the application restarts.
The configuration file must be writable by the application account. A failed save leaves live permissions
unchanged and reports failure. Target-state buttons expire after ten minutes; refresh after another admin changes policy.

Closing a category stops new unpaid work, including old buttons, restored input and unfunded pending orders.
It does **not** strand an issued provider invoice, a committed customer-wallet debit or a started/ambiguous panel
mutation: original inquiry, settlement and exactly-once recovery remain available. Incoming personal-card receipt
images are still retained as evidence of an external transfer; no new courtesy client is created while sales are closed.
Free trial allowances and explicit admin compensation issuance remain separate from paid sale admission.

Commercial, payment-gateway and download controls use serialized, byte-preserving root-boolean edits in the existing
`Data/configuration.json`; unrelated settings, secrets, Persian text and formatting are not regenerated.
Do not replace production configuration with the example, and do not run an external manual rewrite concurrently.


## AtlasPay callback activation

`POST /atlaspay-webhook` is the public HTTPS receiver. AtlasPay section 3.8 requires a merchant
webhook signing secret: register the receiver once with `POST /webhook` (or use the mini-app settings),
store the one-time `webhookSecret` as `atlasPayWebhookSecret` in the **existing** persistent
`Data/configuration.json` or a protected secret source, and keep it out of logs/source control.
Registration rotates/returns the secret; do not repeat it on each startup or order. `GET /webhook`
can confirm the registered account URL without exposing the secret.

Set `atlasPayWebhookUrl` to the externally reachable `https://<payment-host>/atlaspay-webhook` to
send that URL as the per-order `webhookUrl` in section 3.1 for **new** AtlasPay wallet and tenant
orders. This override uses the same registered merchant secret. If the account-wide URL already
points here, the override is optional; leave it empty to use the account URL. Add only the intended
keys to the persistent configuration—do not rebuild it from the example—and deploy the same values
on each payment-server instance. Invalid HTTPS routes or a missing secret reject startup when
AtlasPay is enabled. Allow inbound HTTPS POST through the reverse proxy to this route.

The receiver checks HMAC against the raw body, stores the hint, and returns quickly; the worker
fetches `GET /orders/{id}` before applying any payment/fulfillment change. AtlasPay sends no retry
after a failed callback; bounded polling and customer/super-admin checks still recover missed
notifications. Existing invoices keep their original account-wide destination.

### Callback signature rejection (HTTP 401)

An `invalid signature` warning means the receiver could **not authenticate the request**. It
does not establish whether the sender was an attacker or AtlasPay using a different signing
secret, and the receiver must not accept it based on its source address, order id, or apparent
timing. The rejection leaves no durable webhook receipt and has no payment effect. Compare
the warning times with provider inquiry outcomes and local payment timestamps using the
correct server/UTC time-zone offset; correlation is a diagnostic clue, not signature proof.
Do not copy callback bodies, signature headers, customer identifiers, or secret values into
logs, tickets, or chat.

New receiver warnings include `SignatureHeaderPresent=false` when the header was absent or
blank; `true` means only that a nonblank value was supplied, **not** that AtlasPay sent it or
that its bytes were well-formed. Both cases still return HTTP 401 without storing a receipt
or settling an order.

Check the current registered account URL through AtlasPay's authenticated `GET /webhook`
(section 3.8; it does not disclose the signing secret), and compare its destination with the
public receiver URL and any per-order `webhookUrl` override. Verify that the reverse proxy
forwards the `X-Webhook-Signature` header and the original request body bytes unchanged;
AtlasPay signs the body with the merchant's one-time **webhook** secret, not the API key.
The configured `atlasPayWebhookSecret` must be the secret from the same merchant account
currently signing these orders, on **every** receiver instance. The registration-status GET
cannot validate that secret. If its provenance cannot be established, deliberately
re-register the intended HTTPS callback using authenticated `POST /webhook`, securely
capture the newly returned secret once, update the existing protected configuration/secret
source on every instance, and restart/reload the receivers together. This rotates the
signing secret: coordinate the change to avoid a split deployment, and never repeat
registration per order or on startup. Confirm a **new** signed provider callback writes
`WebhookReceivedAtUtc` for a matching local order and that settlement still requires a
separate authoritative `GET /orders/{id}` inquiry. Keep HTTP 401 for unverifiable callbacks;
polling and explicit customer/admin checks cover callbacks AtlasPay does not retry.

### Delayed payment after an AtlasPay order expires

A valid signed `order.confirmed` arriving after the local invoice became `expired` now triggers a fresh official
`GET /orders/{id}` automatically. The expired cache cannot discard or complete that new hint. Full-payment,
provider identity, total, manual-delivery and one-credit checks still apply to both wallets and tenant orders.
Temporary inquiry failures leave the receipt pending within the existing retry budget; an official response that
still says `expired` completes the hint without credit. Rejected/cancelled and manual-review orders are not reopened.
This does not restart periodic polling of every expired invoice or restore an absent/rejected historical callback.

For diagnosis, inspect the matching row's `WebhookReceivedAtUtc`, `WebhookEvent`, `WebhookProcessedAtUtc`,
`LastInquiryAtUtc`, `ProviderStatus`, `SettlementState`, and `IsAddedToBalance`, together with the payment host's
POST access/error logs at the provider approval time. An empty receipt proves no callback was durably accepted in
that database snapshot, not whether AtlasPay sent one. A `settled` provider inquiry proves provider payment, not
local wallet credit; an `atlaspay_status_verified` activity event alone is not settlement evidence.

From the **owned bot's configured global super-admin** payment-status screen, enter the exact local
`AP:<id>` or the full `AtlasPay-...` merchant reference. A cached `expired` status no longer prevents
a new authenticated `/orders/{id}/verify` call. If AtlasPay now reports full payment with the
original order id, merchant reference and total, the normal idempotent settlement runs: **do not**
use manual approval. A signed `order.confirmed` webhook is only a hint; it cannot replace verification.

For a **tenant storefront order**, its active colleague owner can instead use the selected store's
**✅ تایید دستی پرداخت درگاه** and enter the public tenant `OrderId`. This officially rechecks AtlasPay,
UniquePay, HooshPay, Tetraminator or NOWPayments and resumes the exact order's existing idempotent fulfillment,
even if new sales/gateway admission is now closed. It is not provisional approval, does not accept `AP:<id>` or
provider hashes, and cannot settle sibling-store orders. Uncertain financial/XUI claims still require global
operator review; only a proven rejected provisioning attempt permits the existing durable explicit-owner retry.
See [the storefront recovery procedure](multiple-storefronts.md#store-owner-official-gateway-recovery-by-orderid).

If a fresh response still says `expired`, the bot may offer **two-stage provisional wallet credit**
only for an uncredited owned-bot `wallet_charge`. Before pressing either approval button, the
super-admin must independently confirm receipt of the **entire customer-facing total in toman**,
the matching provider order/reference/tracking code, and the actual bank/provider settlement.
Neither the expired API response nor the button supplies that proof. The status screen shows the
immutable base amount that will be credited; the service stores the approving admin/time and a
receipt-backed wallet/ledger audit. An unknown provider-received amount requires manual bank
evidence; a known short payment is ineligible. Do not approve a disputed or refunded transfer.

The final button repeats the official verification under the payment gate. A new paid result uses
official settlement instead; if still expired, only that explicit decision may add the saved
base amount once. A repeated callback or later official confirmation cannot add another credit,
ledger entry, referral or notification. Rejected/cancelled orders, tenant orders or tenant-origin
wallet charges, identity/amount mismatches, provider errors, and ambiguous claims stay blocked.
For a previously expired provisional charge, a later signed confirmation or **manual super-admin status check**
can record official confirmation without a second credit; ordinary periodic polling of terminal rows remains
disabled. Never hand-edit `users.db`, `credentials.db`, or a customer's balance to emulate approval.

## Test schema policy

The general concurrency fixture in `Adminbot.Tests/ConcurrencyTests.cs` uses `EnsureCreated` only for short-lived unit
and concurrency databases that need the current schema and do not claim migration coverage. Every migration,
startup-compatibility, deployment, historical-upgrade, and artifact-preflight test uses `Database.Migrate` and the real
migration assembly. No migration guard uses `EnsureCreated` or `EnsureCreatedAsync`.

## Tenant weekly dashboards through Sales Assistant

Deploy the normal single application artifact; this feature adds no schema migration or configuration flag.
The registered tenant worker is independent of `WeeklyUsageReportEnabled` and begins the completed-week cycle
at **Saturday 00:00 Tehran** (midnight ending Friday); the global logger report remains at 00:01.
Keep Sales Assistant enabled with its existing protected token, and have each storefront owner start it.
Reports use only that assistant transport, never tenant/owned-bot fallback. Startup considers only the latest
completed week and configured stores created before its boundary, including disabled stores.

Retain the daily activity JSONL files for both comparison weeks. Missing/malformed files produce explicit
interaction-data warnings rather than fabricated data; successful gross sales come from fulfilled users.db
orders matching both store and current owner. See [report semantics](multiple-storefronts.md#automatic-weekly-owner-reports).

`UsageReportDispatches` in users.db retains one bounded `tenantweekly:` key per week/store/owner/identity.
`SendStarted` is a durable non-reclaimable send barrier, not an expiring lease. `Sent` and
`DeliveryRecordedWithError` include a known Telegram acknowledgement; `DeliveryUncertain` or a stranded
`SendStarted` requires checking the owner's conversation before any manual resend. Never delete/reset these
rows just to replay a cycle. `Rejected` means a definitive permanent Telegram rejection; `Failed` permits
retry only for a definite no-send condition or 429. Structured warnings retain sanitized codes/types, not
bot tokens or raw API messages. No report operation changes balances, profit, ledger entries or orders.

## Per-bot Telegram Cloud / Local routing

Existing bots remain Cloud after deployment; no automatic production migration runs. The additive users.db migration
`20261009120000_AddTelegramEndpointRouting` stores exact BotFather-bound endpoint state, history and durable independent
operator-alert receipts. Existing deployment preserves these rows/configuration with the application databases.
Run the normal published `--migration-check` on isolated backup copies before activation.

Global Super Admins use **🗽 Admin → 🌐 مدیریت Telegram API** or `/telegram_api` in another healthy owned bot.
The inventory/detail headline now shows actual **Cloud / Local request admission**, separately from desired and last-activated
routes; current-operation migration success requires committed destination activation evidence. A fenced Cloud wait is not
shown as an active route. Confirmed **bulk Local / Cloud** controls cover the entire frozen inventory and report per-bot outcomes;
bulk Local explicitly retains one eligible independent owned Cloud administration bot. This panel change adds no database migration or
configuration and never automatically migrates existing bots. Read-only reports last one hour and can be reopened in another
healthy owned host; ten-minute controls and durable per-bot state/history retain their existing security/restart semantics.

Endpoint incidents now go directly to the existing `loggerChannel` through an existing enabled owned bot whose active
route is Cloud and whose channel posting permission is verified. No dedicated bot/private Super Admin chat or notifier-id
settings are needed. Missing logger/sender prerequisites retain Pending without consuming attempts; uncertain sends never replay.
Migration `20261009130000_RouteEndpointIncidentsToLoggerChannel` consolidates legacy recipients into one incident receipt
and preserves old acknowledgment/uncertainty fences. Only endpoint alert/receipt tables change; its Down path refuses unsafe
private-recipient restoration. An older private-recipient worker is incompatible; keep the schema-aware release and receipts.
Before explicit Local migration, configure a validated read-only mapping from the existing downloader container's Local file
directory to its existing host volume. Keep port 8081 loopback; do not install, restart, stop or reconfigure the container.
Missing mapping blocks migration before Cloud logout.

Cloud logout has a ten-minute Cloud reuse restriction; acknowledged Local logout also receives the conservative recorded
rollback wait. Unreachable/uncertain Local cleanup is pending/intervention, never a successful Cloud failover.
Default failback is manual. Disabling management retains saved effective routes rather than logging Local bots into Cloud.
Do not roll back to an unaware Cloud-only binary until every Local bot has safely returned to validated Cloud operation.
See [protocol, logger-channel alerts, staged rollout and safe rollback](telegram-api-endpoints.md).
