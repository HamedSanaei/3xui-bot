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

The GitHub production workflow streams `scripts/deploy-production.sh` to the host, which clones the exact pushed SHA and
synchronizes it into `/root/vpnetiran` before restarting `vpnetiranbot.service`. That path must clear the same gates as
the immutable-release path; `dotnet publish` alone is explicitly **not** sufficient, because it compiles the application
but never runs the suite or the EF model checks.

Against the freshly cloned staging checkout, and before any source or publish synchronization and before systemd is
touched, the script now runs, in order:

```bash
dotnet tool restore
dotnet restore Adminbot.sln
dotnet build Adminbot.sln -c Release --no-restore "/p:SourceRevisionId=<sha>"
dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build
dotnet ef migrations has-pending-model-changes --no-build --context UserDbContext
dotnet ef migrations has-pending-model-changes --no-build --context CredentialsDbContext
```

It then publishes with the same `SourceRevisionId` stamp, and runs the published executable's migration preflight twice:
once against fresh databases, then against online-backup copies of `.../publish/Data/users.db` and
`.../publish/Data/credentials.db`. Only after every gate passes does it synchronize source and publish and restart the
service; a failing gate exits before the protected `Data` directory or systemd is touched.

`scripts/deploy-production.tests.sh` asserts the *ordering* structurally (restore/build/tests/EF checks and the
preflight all precede synchronization, and synchronization precedes the restart) so the gate cannot be dropped or moved
by a later edit on a machine without rsync or systemd.

## Tenant installation tutorial assets

`Assets/tutorials/{android_v2rayng,windows_v2rayn,ios_android_v2box}/` is application content, not persistent
`Data` state. Publish and synchronize all three directories with the release. The original numbered PNG slides
are retained without lossy recompression. Use a clean publish directory: old JPEG substitutes left beside the
original PNGs would make the image resolver send duplicate steps, because it accepts both formats.

An album upload has a bounded 24-second foreground deadline; ordinary Telegram interaction still has eight seconds.
If `Tenant tutorial album delivery failed` reports `send_media_group` timing out, check Telegram connectivity and the
number/size of shipped slides before changing any bot configuration. A timeout is ambiguous (Telegram may already
have received the group), so the application never re-sends it automatically. No database row stores tutorial progress
or proves recipient delivery.

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
