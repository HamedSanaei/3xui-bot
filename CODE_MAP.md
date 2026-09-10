# CODE_MAP.md

- Website outbox: `GozargahSyncSemantics` compares canonical payload plus ownership, excluding tracking_code.
  `QueueAndSendAsync` and retries share UUID/email admission; unchanged updates create no event or HTTP mutation.
  `Sync Gozargah Site` reports aggregate changed/unchanged counts. Retry cycles compact <=100 terminal rows older
  than `GozargahSiteSyncRetentionDays` (30 default), retaining latest success/deletion state; unresolved rows never purge.
  No schema/index change; see `docs/gozargah-sync-retention.md` for audit and optional manual VACUUM guidance.

- Owner access/debt: `TenantAccessService` gates every tenant message/callback before business actions, without stopping
  receivers or changing manual Enabled. Owner IsBlocked takes priority; otherwise local balance >0 or readable usable
  website balance at or above the configurable `tenantMinimumSiteWalletToman` (default 200000) allows access.
  Website unavailability uses local balance alone.
  Negative local wallets trigger owner-wide partial/full website repayment through `TenantDebtTransfer` and existing
  website debit receipts; only confirmed debit authorizes unique local credit. One pending transfer per owner, no
  uncertain replay. `WalletOperationReconciliationService` recovers local credits after restart without remote calls.
  Migration `20260907120000_TenantDebtTransfers` creates empty audit storage; never downgrade with transfers present.
  Menus display `🌟اکانت تست`; the former label remains input-only for old keyboards. Trial policy is unchanged.

- Tenant gateway/trial completion: `TenantBotService` logs readable customer gateways separately from owner funding
  and routes `🌟اکانت تست` to `XuiV3BotFlowService.TryHandleFreeTrialAsync`. The shared v3 policy remains
  non-colleagues/verified phone, 100 MiB national or 1 GiB normal, three days, thirty days per type and bot/user.
  `TelegramPhoneVerification` shares owned/tenant Contact validation with each bot's own support/menu.
  `XuiV3PurchaseService` retains store, owner and recipient in trial metadata; no wallet/order/profit effects.
  Incremental verification is diff/UTF-8 plus Release build only; the prior 146-test result predates these changes.

## Purpose

Adminbot is a multi-brand Telegram sales bot for XUI/3x-ui VPN accounts. It supports owned bots, colleague-owned tenant storefront bots, wallet payments, payment gateways, card-to-card tenant receipts, broadcast jobs, XUI v3 account management, and optional sync with `gozargah.network`.

## Entry Points

- Payment-log backup intent is now a global `DatabaseBackupState` watermark in `telegram-log-outbox.db`.
  `TelegramLogOutbox.EnqueueAsync` atomically increments it with Payment insertion; delivery/ACK/retry never
  requests a backup. The tracked dispatcher backup worker captures Requested before snapshotting and advances
  Covered only after both SQLite online backups upload. Historical rows bootstrap one generation once, and
  requests arriving during upload coalesce into one follow-up. Production uses the configured central backup
  destination. See `docs/backup-recovery.md` and `Adminbot.Tests/BackupRecoveryTests.cs`.
  Backup wakeups occur only after durable Payment commit; idle recovery scans default to ten seconds.
  Blank global destinations fall through to default-owned then persisted fallback. Logging classification is
  explicit: `LogPayment` (EventId 1000/Payment) = financial audit only (settlement, wallet credit/debit/refund,
  purchase, renewal, referral reward, admin wallet adjustment) = durable Telegram HTML + requests DB backup.
  `LogTelegramHtml` (EventId 1001/TelegramHtml) = important non-financial operational/security/admin audit
  (tenant lifecycle, admin phone verification, admin role changes, colleague/cooperation requests, XUI link
  changes, account deletion, XUI operation outcomes) = durable Telegram HTML, no DB backup. Ordinary
  `LogInformation` = plain memory-only best-effort logs. See `docs/backup-recovery.md`.

- `Program.cs`: ASP.NET host, DI registration, EF migration startup, controller mapping, bot runtime registration, hosted services.
- `Services/BotRuntimeServices.cs`: bot registry, bot context accessor, bot client provider, and multi-bot receiver startup.
  Every receiver generation first proves that no webhook remains. Transient webhook GET/delete/verification failures
  leave the bot enabled and enter host-lifetime recovery with capped exponential backoff; runtime webhook conflicts use
  the same bot-scoped recovery. Per-bot lifecycle gates plus the receiver registry remain the single-receiver boundary,
  while a genuine second-process `getUpdates` conflict is stopped for operator review rather than auto-restarted.
- `Services/TelegramBotService.cs`: main dispatcher for owned bots and legacy/admin/customer flows.
- `Services/TenantBotService.cs`: tenant owner panel and tenant customer storefront flows.
- `Controllers/PaymentController.cs`: payment IPN endpoints and gateway callbacks.
- `Services/TelegramUpdateScheduler.cs` + `Services/TelegramUpdateInboxStore.cs` + `Services/TelegramUpdateExecutor.cs`:
  durable bounded Telegram update inbox in `users.db`. Receivers only persist updates; the scheduler claims and
  executes them with per-bot/per-user FIFO lanes, weighted round-robin, bounded concurrency, startup recovery, and
  shutdown drain (see the Durable Telegram Inbox section below).

## Build and Publish

- Restore/build: `dotnet tool restore`, `dotnet restore`, then
  `dotnet build Adminbot.sln --configuration Release --no-restore`. The repo-local manifest pins `dotnet-ef` to the
  same 10.0.8 release as EF runtime/design packages.
- Before publish, CI and deployment run `dotnet ef migrations has-pending-model-changes` independently for
  `UserDbContext` and `CredentialsDbContext`. Publish then targets a new directory and the exact published `Adminbot
  --migration-check` executable validates fresh databases and SQLite online-backup copies; this mode starts no host,
  HTTP listener, Telegram receiver, hosted worker, or remote logger.
- Production deployment uses immutable `/opt/vpnetiran/releases/<commit>/` directories, shared persistent Data,
  atomic `current`/`previous` symlinks, and rollback-aware systemd activation through `scripts/deploy-release.sh`.
  See `docs/deployment.md`. A dirty or SHA-mismatched checkout is rejected before build, and systemd is untouched until
  every build/test/EF/artifact check succeeds. Release assemblies log their embedded commit and build configuration.
- Server publish: `dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false`.
  `Data/**` is excluded because databases, production configuration, certificates, and the runtime plan catalog are
  shared state rather than release artifacts.
- The solution contains the production `Adminbot` project plus `Adminbot.Tests` (xunit, added explicitly for the
  Telegram concurrency/reliability task). Tests never ship: `Adminbot.Tests` is `IsPublishable=false`, the app does not
  reference it, and the Release publish output contains no test assemblies.
- `Adminbot.csproj` excludes `Adminbot.Tests/**` from default SDK items so stale nested `bin/obj` files in an existing server checkout cannot be compiled into the application.
- `Adminbot.csproj` explicitly pins `SQLitePCLRaw.bundle_e_sqlite3` to patched 2.1.12 so EF Core's native
  SQLite library, provider, and core packages resolve as one compatible family instead of vulnerable 2.1.11.

## Data Stores

- `Data/UserDbContext.cs`: bot state, tenant bot settings, payment records, broadcast jobs, wallet ledger, global referral relationships/events/rewards, tenant orders, Gozargah sync outbox, owned-wallet settlement-notification outbox, weekly usage-report dispatch leases, durable XUI volume-reminder cycles/claims, the durable Telegram update inbox (`TelegramUpdateInbox`), and XUI creation reservations (`XuiV3CreationOperations`).
- `Data/CredentialsDbContex.cs`: shared user wallet/profile data plus the append-only durable `WalletOperations`
  receipt table (balance mutation + receipt commit atomically in one credentials.db transaction; see the Wallet
  Operations section). Referral must not add tables, columns, or models to this database.
- `Data/configuration.json`: app-level settings and owned bot configs. Secrets live here locally and must not be copied into docs.
- `Data/configuration.example.json`: sanitized configuration example including referral and four-gateway enable/readiness settings; all gateway switches and secret placeholders default to off/empty.
- `Data/xui-v3-service-plans.json`: XUI v3 service catalog, inbounds, metered per-GB/per-day/lifetime pricing,
  duration availability, unlimited fair-usage plans, per-storefront unlimited audience/price policies, and
  `minimumTrafficGb`.
- Historical migration filenames and `[Migration("timestamp_name")]` ids are immutable database history. Their CLR
  types use PascalCase to keep current compilers warning-free; never rename the files or attribute ids as cleanup.

## Core Services

- `Services/XuiV3PurchaseService.cs`: resolves service selections, validates plan rules, centralizes owned/tenant
  unlimited-plan audience checks separately from role pricing, builds XUI v3 account metadata, and creates accounts.
  `NormalizeOptionalUserComment` converts empty/skip input to null and omits it from new panel metadata, while keeping
  technical ownership/Telegram fields intact.
- `Services/XuiOperationTiming.cs`: ambient monotonic timing scope for XUI audits. The v3 transport sums complete
  logical panel calls (including retries/backoff); legacy v2 routes wrap their panel calls explicitly. Central create,
  renew, delete, activation, comment/link edit, trial, bulk-admin, and tenant fulfillment logs show both
  `MM:SS.mmm` panel API time and end-to-end execution time. Total minutes are unbounded; total time includes local
  persistence, settlement, Telegram delivery, and bulk pacing performed after execution starts, but excludes customer
  confirmation/payment waiting. Link-change recovery preserves durable total time while API time covers only the
  current process attempt.
- `Services/XuiV3BotFlowService.cs`: shared customer account flows for owned and tenant bots: purchase, renewal,
  search, account list, link/comment changes, delete, state callbacks, and owner-checked configuration delivery. All
  owned account cards use one source-aware action keyboard; `x3:acfg:{clientId}` reloads ownership, reads
  `subLinks/{subId}` with `links/{email}` fallback, deduplicates URLs ordinally, and sends short results as escaped HTML
  or long results as an in-memory UTF-8 file. The API adapter accepts raw arrays and standard envelopes and disables
  automatic URI-logging retries for these identifier-bearing paths. SubId, URLs, response bodies, tokens, and private
  request URIs must never enter callbacks or logs; non-owner exact-identifier results retain a restricted renewal-only
  menu and never receive usage details, configuration delivery, or management actions.
- `Services/TelegramNavigationCommandParser.cs`: validates bot-addressed `/start` commands and the owned-only `/refresh`
  alias, including `@BotUsername`, optional start payloads, and the legacy `/start=payload` form. The main dispatcher
  clears both the current `BotId + TelegramUserId` conversation row and bot-scoped in-memory XUI purchase selection
  before blocked-user, forced-join, tenant, regular-user, or super-admin flows. Access gates still control menu display;
  payment/referral payloads keep their existing side effects, and no wallet, ledger, order, payment, account, referral,
  long-lived counter, or other bot's state is deleted. Tenant bots accept only `/start`; owned command menus publish
  both `/start` and `/refresh` whenever their runtime starts.
- Owned purchase/renewal insufficient-balance messages expose `wallet:charge`. The dispatcher trusts only the callback sender, clears that bot's persisted state plus its in-memory XUI selection, edits the source message, and opens the same live-gateway charge menu as `💰شارژ حساب کاربری`; tenant storefronts never receive this shortcut.
- `Services/XuiV3RenewalPolicy.cs`: central renewal payload calculation for metered, national, and unlimited accounts.
- `Services/XuiV3RenewalOperationStore.cs` + `Domain/XuiV3RenewalOperation.cs`: durable exactly-once renewal saga in
  `users.db`. `MutationStartedAtUtc` is persisted immediately before the only permitted `UpdateClient` POST; processing
  or ambiguous operations are never replayed, even when GET temporarily shows the target absent. A filtered unique
  `AccountLockKey` prefers normalized panel UUID and falls back to normalized email, and remains held for pending,
  processing, ambiguous/manual-review, and applied-but-unsettled work. It is cleared only by definitive panel rejection
  or successful settlement. `Services/XuiV3RenewalRecoveryService.cs` claims durable recovery leases, performs GET-only
  detailed reconciliation with bounded exponential backoff, settles recovered owned/tenant operations through their
  existing idempotency boundaries, and escalates unavailable evidence after 12 attempts/24 hours. Migration
  `20260820061906_AddXuiV3RenewalRecoverySafetyBoundary` sets `RecoveryEligible=false` and parks every historical
  pending/processing/ambiguous/applied-unsettled operation in manual review. Only post-migration inserts explicitly set
  eligibility true; every worker claim is eligibility-filtered, and the migration never debits wallets, repairs orders,
  settles history, or calls XUI. `20260820102235_AddXuiV3RenewalReconciliationEvidence` adds only nullable comparison/
  pre-snapshot evidence and a zero observation counter; it performs no status or financial backfill. See Current
  Gotchas for the five-state comparator, identity-safe direct/list read, ten-minute proven-pre-state unlock, and the
  exact renewal-controlled fields. New operations still use a unique full-list identity snapshot before mutation, but
  inbound attachment membership is valid when empty and is not changed or compared by renewal.
- `Services/XuiV3RenewalTargetParser.cs` + `XuiV3RenewalTargetResolver.cs`: shared side-effect-free exact renewal lookup
  for email, raw SubId/full subscription link, UUID, and VLESS/VMess/Trojan/Shadowsocks/Hysteria configurations. One
  fresh `clients/list` snapshot must produce exactly one client; configuration matching trusts only embedded UUID or
  protocol password, never host/fragment/display label. Password matching is ordinal and duplicate matches fail closed.
- `Services/XuiV3ClientPlanEligibility.cs`: checks whether an XUI client belongs to active service inbounds.
- `Services/XuiV3ClientUsageResolver.cs`: shared null-safe XUI list-response interpretation for consumption, quota,
  separate client/traffic/extension expiry evidence, `createdAt`, `updatedAt`, origin bot, and renewal metadata. Volume
  eligibility is a detailed sanitized result; time expiry is definitive only when every present panel source agrees.
  A missing nested `traffic` object falls back to top-level/extension fields.
- `Services/XuiV3VolumeExpirationReminderService.cs` + `XuiV3VolumeReminderStateStore.cs`: one-list-request
  30-minute 80/90/99 traffic reminder worker and users.db cycle/claim idempotency. Notifications are bot-scoped,
  separate per account, rate-limit-aware, and require a matching `BotUserState`. Contradictory list expiry versus
  current bot metadata is resolved by one identity-checked `GET clients/get/{email}` only; failures use durable
  per-client backoff and never consume a threshold. Migration `20260821203515_AddXuiV3VolumeReminderEligibilityDiagnostics`
  adds nullable sanitized decision/probe fields without backfill or changes to existing cycles.
- `Services/XuiV3ReminderCommentResolver.cs`: shared GET-only, identity-checked extraction of
  `XuiV3ClientMetadata.UserComment` for due time/volume reminders. Raw panel comments are never exposed; missing user
  comments omit the line, while unavailable/mismatched/malformed detail responses defer only that account before
  time dedup or volume claim. Legacy tenant-sale audit text stored in `UserComment` is treated as internal/absent. A
  detail GET already used for expiry verification is reused for comment extraction.
- `Services/XuiV3AdminFlowService.cs`: super-admin XUI v3 management flows. Historical sync, admin renewal, and
  expired-account deletion resolve tenant-created clients back to the storefront owner's Gozargah account while
  preserving the buyer's panel `tguserid`; missing tenant ownership fails closed.
- `Services/XuiV3LinkChangeOperationStore.cs`: per-operation users.db contexts, atomic confirmation, active-client uniqueness, leases, and bounded recovery state for link changes.
- `Services/XuiV3LinkChangeRecoveryService.cs`: hosted worker that resumes the exact persisted email/UUID/subId after ambiguous XUI responses or process restarts.
- `Services/BroadcastManager.cs`: queued broadcast engine with progress/status tracking and retry behavior.
- `Services/SalesAssistantService.cs`: central assistant bot for tenant sale notifications and manual receipt approval.
  Receipt captions/details include an HTML-safe Telegram name link, username, numeric id, mapped payment provider, and
  catalog-aware plan label with historical-key fallback; photo-delivery fallback messages never expose raw exceptions.
- `Services/WalletLedgerService.cs`: append-only wallet ledger for credits/debits.
- `Services/ReferralService.cs`: global owned-bot relationship registration, reward calculation, users.db state/ledger idempotency, user stats, notifications, and startup reconciliation.
- `Domain/PaymentGatewayAvailability.cs`: process-wide live snapshot for HooshPay, Tetraminator, UniquePay, AtlasPay, and NOWPayments. Super-admin target-state callbacks use this service; only root `enabled` booleans are persisted through the byte-preserving atomic JSON editor. API credentials remain restart-loaded and are never displayed or logged.
- `Domain/UniquePay.cs`: UniquePay Bearer/form-urlencoded bot-gateway client, owned-wallet settlement, fail-closed authoritative verification for both official toman fee-payer contracts, durable settlement claims, restricted provisional OWNED credits, callback coordination, and bounded recovery polling. New invoice creation is single-attempt; inquiry is read-only. `CreationState` independently records `attempting`, `created`, `ambiguous`, `failed`, or `manual_review`: the one POST reservation is saved before network I/O, while HTTP 5xx/timeouts/disconnects/malformed success remain GET-only recoverable and can never authorize another create call.
- `Services/UsageAnalyticsService.cs`: completed Tehran-day aggregation of JSONL messages/callbacks, successful owned sales, and fulfilled tenant sales; excludes global super-admin ids and supports tenant bot filtering.
- `Services/UsageReportChartRenderer.cs`: cross-platform SkiaSharp high-resolution line-chart PNG renderer with
  explicit Y scales, every weekly/monthly date, adaptive value labels, point markers, and current-versus-previous weekly comparison. It uses
  the embedded OFL-licensed `Assets/Fonts/NotoSans-Regular.ttf`; never fall back to `SKTypeface.Default`, because
  minimal Linux hosts can silently render every chart label blank.
- `Services/WeeklyUsageReportHostedService.cs`: Saturday 00:01 Tehran report scheduler, catch-up behavior, users.db claim/lease idempotency, and direct central logger delivery through the default owned bot.
- `Services/PaymentSettlementNotificationWorker.cs` + `Domain/PaymentSettlementNotification.cs`: delivery-only owned-wallet
  success notification outbox. The first settlement credit and unique outbox row share one users.db save; a 15-second
  worker claims two-minute leases and performs bounded transient Telegram retries without any credentials.db, wallet,
  provider-settlement, tenant, or XUI dependency. Expired claims become `delivery_uncertain` and are never resent
  automatically because Telegram may already have accepted the message.
- `Domain/GozargahSite.cs`: Gozargah site API client, typed owner/buyer sync ownership, sync event models, mapping,
  and retry helpers. Tenant website events use the colleague owner as the site account and retain the buyer separately;
  successful-event dedupe includes site owner and tenant bot. `GozargahSiteApiClient.SendAsync<T>` validates HTTP status,
  Content-Type, and body shape before deserializing: HTML/error-page bodies (`<...`), explicit non-JSON content types,
  empty bodies, and invalid JSON all become unsuccessful `GozargahSiteApiResponse<T>` values with a bounded,
  whitespace-collapsed preview (never a raw `JsonReaderException`), so a temporary website failure cannot crash the
  Telegram update/purchase flow.

## Tenant Bot Rules

- Each tenant bot is a `BotInstance` with `Type = tenant`. `TenantStoreStore` allocates up to
  `TenantMaxStoresPerOwner` (positive, default 5) in an immediate SQLite transaction, including disabled/reset rows.
  Existing ids remain unchanged and get owner-local store number 1; new ids are `tenant-{ownerId}-{storeNumber}`.
  The unique owner/number pair and add-button nonce prevent concurrent owned bots or redelivery exceeding the limit.
  Settings are independent defaults; no wallet or website account is created during allocation.
- Owner management starts with a store list. `TenantOwnerCallback` wraps actions with store number/revision/expiry
  (at most 64 UTF-8 bytes), and `TenantOwnerPanelClient` labels every owner text prompt and addresses every keyboard.
  All reads/writes recheck the authenticated owner against the exact selection. `BotUserState.OwnerStoreId` persists
  the input target per owned bot/user; changing stores cancels pending input. Legacy callbacks show the fresh list only.
  Orders, manual confirmations, stats and broadcast audiences use that selected tenant id. Reset retains ids/history.
  `TelegramBotId`, verified against getMe during registration, is unique across persisted bots; configured owned and
  assistant tokens (including disabled configurations) are also checked. Runtime token removal releases only that identity.
- Owner finances remain shared across stores. `TenantWalletRoute` pins each order's source before debit; local receipts
  remain in credentials.db and reconcile ledger writes after a crash. Online payments credit owner profit; card base cost
  prefers the local wallet, then sufficient website funds, then the existing local overdraft rule. `SiteWalletDebitStore`
  gates fresh website eligibility/debit by owner across all callers, outside SQLite transactions and XUI provisioning.
  Its users.db `SiteWalletDebitOperation` marker commits before POST; only an authoritative receipt marks it applied.
  Sending/ambiguous results never repeat POST or authorize another wallet debit, even after a top-up/restart.
  Uncertain financial operations require review, never block Telegram user lanes. Owned renewal site keys now use the
  renewal operation id, not reusable account email. No cross-database/website transaction is claimed.
  Migration `20260907031653_MultipleOwnerStorefronts` changes no balances; duplicate historical bot identities require review.
  Unfinished historical card orders with panel-success evidence get funding source `review`; only a matching local wallet
  receipt resumes automatically. Unknown website funding remains operation review without blocking Telegram lanes.
  See `docs/multiple-storefronts.md` for rollout/reconciliation and `Adminbot.Tests/MultiStoreTests.cs` for regression coverage.
- Tenant runtime state is scoped by `BotId + TelegramUserId` in `BotUserStates`; never key tenant customer flow only by Telegram user id.
- Tenant customers reuse shared XUI account flows where possible, but tenant payments and fulfillment go through `TenantBotOrder`.
- Unlimited sub-plans carry catalog-only storefront policies: `OwnedColleagueOnly` restricts owned customer purchase
  and renewal to colleague profiles, `TenantVisible` controls tenant display and authorization, and
  `TenantUsesUserPrice` fixes tenant sale to the public/user amount while keeping colleague price as owner base cost.
  Tenant markup remains authoritative for every plan where `TenantUsesUserPrice=false`; no policy is inferred from a
  key prefix, display name, traffic quota, or user limit.
- Tenant account-card renewal callbacks are intercepted before the shared owned-wallet handler and enter the tenant
  order flow. Renewal entry no longer filters ownership: email, SubId/subscription link, UUID, or supported config may
  select another user's account. A non-owner target pauses at state `renew-confirm-external-target` and fixed callback
  `x3:rgo`; the callback carries no identity and reloads bot-scoped email+UUID state before plan selection. Migration
  `20260812000000_AddRenewTargetUuidProofs` adds bot-scoped `RenewTargetUuid` and nullable order-level
  `TargetAccountUuid`. Every safely lockable new renewal stores the panel UUID, while null legacy/UUID-less owned targets
  retain owner-based settlement. Tenant pricing comes from the storefront used to renew, not the account's origin bot.
- Tenant account metadata stored in XUI comments must preserve `CreatedByBotId`, `TenantBotId`, buyer Telegram id, service key, service kind, inbounds, and last action.
- Normal and unlimited services may share the same public inbounds. Renewal/search must trust metadata first; if metadata is missing, negative expiry means unlimited, otherwise shared public inbounds should resolve to normal metered service.
- Tenant support contacts should be stored as canonical `@username`; `t.me` links are normalized before display so customers never see `@https://...`.
- Tenant operational logs, lifecycle notifications, and payment audit logs are delivered through the default owned bot to the central logger channel. Tenant storefront bots are not expected to be members of the private logger channel.
- Tenant owner-panel reset clears only owner-configured storefront settings and disables the tenant bot; it must preserve orders, receipts, payments, ledger entries, and customer state. Invalid-token cleanup on panel refresh clears only `Token`, `Username`, and `Enabled`, leaving card/support/tutorial settings intact.
- Tenant owner toggle uses bounded runtime startup retries for transient Telegram/network timeouts. If the receiver still cannot start, the tenant row is rolled back to `Enabled=false` and no central tenant failure notification is sent for the transient timeout.

## Payment and Ledger Rules

- NOWPayments and HooshPay payment records live in `users.db` and can be linked to tenant orders.
- UniquePay payment records live in `users.db` (`UniquePayPaymentInfos`) and link owned wallet charges or tenant orders through `HashId`, optional provider `RefId`, and `TenantBotOrder.UniquePayPaymentInfoId`. New tenant orders persist that payment FK after the local payment insert and before the provider POST, so an ambiguous provider response cannot orphan the reserved attempt. Migration `20260826152240_AddUniquePayCreationLifecycle` adds the explicit create lifecycle/index, classifies historical rows without provider or financial work, preserves inquiry counters/schedules, and fills an order FK only when exactly one payment row matches. It never replays create, settles a payment, credits a wallet, fulfills an order, or backfills a notification. `credentials.db` is unchanged.
- UniquePay amounts are Iranian toman and the authoritative API may label the currency `IRT` or `toman`. Settlement remains fail-closed: `check-invoice` must return `status=true`, `code=200`, `isPaid=true`, the saved provider reference must match `invoice.id`, and any returned root hash must match the saved merchant hash. Production inquiry responses can omit that root hash echo. The live buyer alias is `feePayer=user`: `invoice.amount` must equal the stored base and `payableAmount` must equal `base + fee + uniqueAmount`; owner-paid responses use `base + uniqueAmount`. Legacy `feePayer=buyer` responses without payable fields retain the documented `invoice.amount - invoice.fee = base` rule. The fee must match the snapshotted percentage within one toman. `isVerified` is informational and not a settlement requirement.
- UniquePay invoices use `/api/ddbot/create-invoice` so each OWNED/TENANT invoice carries separate configured return and callback URLs. The unsigned `POST /uniquepay-callback`, browser return, and customer check are lookup triggers only; every financial change still requires authoritative `/api/check-invoice`. Recovery polling uses `NextInquiryAtUtc`, exponential backoff, a configurable hard attempt cap (default 12), and independent factory-created EF contexts; an ambiguous create reaching the cap moves once to create-level `manual_review` and releases no mutation replay. Explicit callback/return/customer/admin inquiry remains GET-only and may still prove the same invoice exists. Repeated inquiry-attempt details remain in local structured logs but are suppressed from the Telegram logger; the first create failure and the terminal manual-review transition remain visible. Disabling the global switch blocks new invoices only. Explicit provider lifecycle hints map to `expired`, `cancelled`, or `failed`; absent/unknown hints remain pending, and no undocumented provider response is guessed to mean invoice-not-found.
- UniquePay settlement uses an atomic users.db claim (`pending -> processing -> settled`) before wallet/XUI side effects. Because wallet/tenant fulfillment crosses users.db, credentials.db, and XUI, a process crash after the claim is ambiguous; claims stale for 30 minutes move to `manual_review` and are never automatically replayed, preventing duplicate wallet credit, owner profit, or account delivery.
- Migration `20260801000000_AddUniquePayProvisionalApproval` adds provisional audit fields and safely requeues only unsettled rows failed by the former currency/fee-payer validator. Data-only migration `20260801220000_RequeueUniquePayOptionalHashFailures` requeues uncredited rows rejected by the former mandatory hash-echo rule; all are freshly verified before settlement. Super-admin `Verify payment` accepts `UP:<internal-id>`, Hash ID, or Ref ID; it always performs an official inquiry first. A still-unpaid, valid OWNED wallet invoice can be credited provisionally through two confirmation stages using only its stored base amount. TENANT invoices, terminal/mismatched responses, and provider/network failures cannot be provisionally approved. No referral is awarded; later official confirmation records audit only, while later terminal failure is logged for human review without automatic clawback.
- Data-only migration `20260802000000_RequeueUniquePayUserFeePayerFailures` requeues uncredited rows rejected before the live `feePayer=user` buyer alias/payable contract was supported; the worker still requires a fresh fully matching paid response before settlement.
- Data-only migration `20260803180000_RequeueUniquePayVerifiedUnsettled` schedules provider-paid but uncredited rows for a fresh authenticated inquiry after the stale-context settlement fix. `UniquePaySettlementService` owns a factory-created users.db context per attempt so reconciliation's newly persisted `paid` state cannot be hidden by the legacy singleton change tracker; no migration directly credits a wallet.
- UniquePay `feePayer` is controlled by the business-level `gatewayFee`/`feePayer` settings in the provider panel; the documented create-invoice form has no fee-payer field. Keep verification support for `user`/`buyer` and `owner` so existing invoices remain settleable; the provider currently reports `user` when the customer bears the configured 12% fee.
- Tenant UniquePay availability is `global UniquePay enabled && TenantUniquePayEnabled`; the tenant owner panel shows `سراسری خاموش` when the global switch is off and refuses local enabling until global configuration is ready.
- AtlasPay is a toman card-to-card provider (`Domain/AtlasPay.cs`) using `X-API-Key` over HTTPS with `POST /orders`, `GET /orders/{id}`, `POST /orders/{id}/verify`; the customer-visible reference is `trackingCode` and the charged amount is `totalAmountToman`, while settlement always credits/stores the immutable `BaseAmountToman`. Migration `20260910012628_AddAtlasPayGateway` adds `AtlasPayPaymentInfos`, `TenantBotOrders.AtlasPayPaymentInfoId`, and `BotInstances.TenantAtlasPayEnabled` (default true); no other table changes. The API key is restart-loaded only (never persisted or logged); full provider card numbers are never stored, only `CardNumberMasked`. Invoice creation is single-attempt: the local payment row and order FK are persisted before `POST /orders`, and HTTP 400/401 are definitive while 5xx/timeout/transport/malformed-success are ambiguous and never auto-retried. Reconciliation/verify only inquire (`GET`/`verify`) and never re-create. Settlement is `IsVerifiedForAutomaticSettlement` fail-closed on identity (provider order id, merchant ref, tracking code, total amount), known status (`confirmed`/`settled` eligible), and `requiresManualDelivery` (never auto-settles; moves to `manual_review`). Owned wallet credits use operation key `payment:atlaspay:{id}:credit`; tenant fulfillment reuses the common purchase/renewal pipeline with an atomic `pending -> processing -> settled` claim and tenant/order linkage checks. Tenant AtlasPay availability is `global AtlasPay enabled && TenantAtlasPayEnabled`; the owner panel refuses local enabling while the global switch is off, and both switches govern creation only (existing payments keep settling). Referral eligibility includes `atlaspay`.
- Super-admin `⚙️ مدیریت درگاه‌ها` displays all four live gateway states, root key names, and configuration readiness without exposing secrets. Enabling a gateway with missing token/URL is rejected. Target-state callbacks carry a revision and short expiry, and are restricted to configured super-admin ids.
- New HooshPay invoices require the live global `hooshPayEnabled` switch and, for tenant storefronts, the
  per-tenant `TenantHooshPayEnabled` preference. Disabling either switch hides and blocks only new invoices, including
  stale Telegram callbacks; existing rows remain eligible for status checks, IPN processing, and settlement. A missing
  global key is disabled, while the tracked operational configuration explicitly keeps the gateway enabled.
- Provider invoice amount limits are enforced both at owned/tenant UI boundaries and inside the provider clients before
  request construction. HooshPay accepts inclusive 50,000 through 1,000,000 toman. UniquePay requires strictly more
  than 50,000 toman, so 50,000 is rejected and 50,001 is valid. Invalid tenant purchase callbacks create no order or
  payment row; invalid tenant renewal callbacks preserve the existing pending order but create no provider/payment row.
- NOWPayments creation uses the same live global snapshot and, for tenant storefronts, `TenantNowPaymentsEnabled`;
  IPN validation and settlement of existing crypto invoices continue when new creation is disabled.
- `Utils/DollarPriceHelper.cs` is the single market-unit normalization boundary for NOWPayments: every accepted quote is
  **IRT/Toman per 1 USDT**. The active Nobitex priority is `v3/orderbook/USDTIRT`, then API v2 IRT stats, then API IRT
  stats; ambiguous RLS-labelled markets are excluded. Multiple live IRT sources use scale consensus so a factor-of-ten
  outlier is rejected. `Domain/NowPayments.cs` never infers Rial/Toman from numeric magnitude; legacy fallback values are
  normalized once using explicit `nowpaymentUsdIrtFallbackPriceUnit` (missing unit remains backward-compatible `rial`).
  If neither a canonical live quote nor a valid configured fallback exists, invoice/payment creation fails before HTTP POST.
- Tetraminator is the second rial gateway for owned wallet charges and direct tenant purchase/renew orders. Its
  `TetraminatorPaymentInfos` rows live only in `users.db`; `OrderId` and non-null `PayId` are unique. The public GET
  callback is unsigned and therefore only triggers an authoritative `GET /payment/inquiry/{pay_id}`. Settlement
  requires provider `status=true`, exact `payment_status=paid`, exact saved `PayId`, and exact toman amount.
- New Tetraminator invoices require both the global `tetraminatorEnabled` switch and, for tenant storefronts, the
  per-tenant `TenantTetraminatorEnabled` preference. Disabling either switch stops only new tenant invoices; saved
  invoices remain inquiry/settlement eligible so paid customers are not stranded. Invoice creation is never retried
  automatically because the provider API has no merchant idempotency key. Owned charge state is consumed before the
  create call, while tenant orders claim one local payment row and either reuse its known link or block an ambiguous
  prior create. Read-only inquiry retries transient 429/5xx and transport failures within configured bounds.
- The owned-wallet Tetraminator button stays visible for manually entered amounts whenever the gateway is globally
  enabled. Amounts below `tetraminatorMinimumAmountToman` are rejected after selection with a clear message and never
  reach `POST /invoice/create`.
- Official owned-wallet Tetraminator settlement writes provider=`tetraminator` ledger entries and participates in the
  existing global owned-bot referral engine. Super-admin provisional approval is two-stage and limited to non-terminal
  owned wallet charges; it uses provider=`tetraminator_provisional_admin`, never rewards referrals, and later official
  confirmation records audit only. The financial service revalidates provisional eligibility and repairs a missing
  unique provisional ledger row on retry without crediting the wallet again. Tenant orders never permit provisional approval.
- Tenant fulfillment across all gateways is serialized and reloads the order under the gate before checking
  `IsFulfilled`; concurrent callback, customer check, IPN, and admin retry paths cannot create another XUI account,
  owner-wallet mutation, or tenant ledger row for an already fulfilled order.
- HooshPay wallet charges may receive a two-stage, super-admin-only provisional credit while the provider remains
  pending. The row keeps its provider status, writes one `hooshpay_provisional_admin` ledger credit, and stores the
  approving admin/time. A later official `paid` IPN/manual check writes one reconciliation audit timestamp/log only;
  it must never create a second wallet credit or ledger entry. Tenant orders and terminal HooshPay failures are never
  eligible for provisional approval.
- Super-admin manual NOWPayments checks are provider re-checks only: local code must not set `finished` or credit balances unless NOWPayments returns a paid status (`finished`, `confirmed`, or `sending`).
- Unlimited renewal no longer infers a target fair-usage quota from the final duration. While active, it adds the
  selected plan's exact traffic to `TotalGB` and adds the exact plan days while preserving positive absolute-expiry or
  negative first-connection-expiry mode. When expired, it replaces `TotalGB`, resets counters, and writes only the
  selected plan duration as a negative first-connection expiry. Owned, tenant, and super-admin flows share this rule.
- Tenant platform-gateway sales credit owner profit; tenant card-to-card fulfillment debits owner base cost and can allow negative owner balances if configured by business rules.
- Tenant card-to-card base cost settlement tries the owner's bot wallet first, then the owner's Gozargah website wallet when connected and sufficient, then allows the bot wallet to go negative with an owner warning. This does not auto-disable the customer account in the current phase.
- Tenant platform-gateway sales credit profit to the owner's bot wallet and include a live Gozargah website wallet snapshot in the private sale log; the site wallet is not mutated for gateway profit.
- Every wallet movement should have a matching `WalletLedgerEntry`.
- Global owned-bot referral relationships are unique by referred Telegram id; `BotId` is attribution only. The first
  relationship is immutable, self-referral and all tenant activity are excluded, and `/start ref_<base36-code>` does
  not interfere with payment return payloads.
- The owned-bot `🎁 دعوت از دوستان` main-menu command is routed centrally for regular users and super-admins after
  blocked-user, tenant, mandatory-join, and `/start ref_...` handling but before arbitrary-text customer flows. It
  clears only the current bot/user conversation state and `XuiV3PurchaseSessionStore` selection before displaying the
  existing global dashboard; tenant bots remain excluded.
- Referral dashboard delivery uses a strict plain-text Telegram sender: `parse_mode` is omitted, the returned
  `Message` is required before `dashboardSent=true`, and Telegram/API exceptions propagate into structured route logs.
  Keep the legacy exception-swallowing sender out of this path because `ref_...` is unsafe under its default Markdown.
  Routine successful dashboard opens are debug-only and must not be forwarded to the private logger channel; referral
  database or Telegram delivery failures remain error-level operational logs.
- Final real-provider owned wallet charges from NOWPayments, HooshPay, and Zibal can create referral rewards only after
  the original credit and ledger succeed. Below-minimum, provisional, partial, manual, site-wallet, failed/refunded,
  trial, gift, and tenant payments do not create an event and do not consume first-payment eligibility.
- Referral persistence and idempotency live only in `users.db`: source/reward uniqueness, reward
  `crediting`/`credited` states, and unique `WalletLedgerEntry.IdempotencyKey`. A credited reward can repair its ledger
  without another wallet change. A process interruption left at `crediting` is failed closed for manual review rather
  than automatically risking a duplicate credit; referral never changes the credentials.db schema.
- Referral startup validation requires every documented `referral` JSON key explicitly, even when disabled; numeric
  zero is never silently inferred from a missing business setting.
- Admin manual wallet credits/debits and colleague role promotions/demotions must be mirrored to the private logger channel with clickable actor and target identities.
- Owned-bot super-admins can manually verify an existing regular or colleague user's phone number by Telegram user id.
  This override accepts virtual and non-Iranian numbers, requires explicit final confirmation, writes the shared
  `CredUser.PhoneNumber`, notifies the user's previously started owned bots, and logs only masked phone values. Dynamic
  user identity fields in this flow must use encoded HTML, never the legacy default Markdown sender.
- Automatic owned-bot contact verification accepts only a contact belonging to the sender and normalizes Iranian mobile
  forms (`09`, `98`, `+98`, `0098`) to `+989...`. Own foreign contacts are rejected with the active owned bot's
  clickable support account; the manual super-admin override remains intentionally international-capable.
- Payment/order fulfillment paths must be idempotent: duplicate IPNs, repeated checks, or repeated assistant confirmations must not create another account or ledger entry.
- Official or provisional first-time owned-wallet credits for NOWPayments, HooshPay, Tetraminator, and UniquePay enqueue
  one `PaymentSettlementNotifications` row keyed by provider + local payment id. Callback replay/`AlreadyAdded` creates
  no second row. Telegram timeout cannot roll back or repeat credit, and retry delivery never invokes settlement.
  Migration `20260825230309_AddPaymentSettlementNotifications` creates an empty table and indexes only: it performs no
  historical backfill, notification, provider call, wallet mutation, tenant fulfillment, or XUI work.
- Tenant fulfillment must reload the order and treat an existing `TenantBotLedgerEntry` for the same `TenantBotOrderId` as already fulfilled; this protects against stale singleton EF tracking and duplicate "check status" clicks.
- If XUI account creation times out after a tenant card-to-card receipt is approved, keep the order unfulfilled but retryable and leave Sales Assistant approval controls available. Do not mark timeout as a definitive failed payment.
- If Sales Assistant cannot relay a tenant card-to-card receipt photo, it must send a text-only fallback with the same approve/reject/detail callbacks so the owner can still confirm the receipt.
- When a tenant order later fulfills successfully after an earlier timeout/failure, clear stale `TenantBotOrder.ErrorMessage` and linked receipt errors before saving so successful order details and audit logs do not keep showing old timeout text.
- Super-admin `Verify payment` accepts tenant storefront `OrderId` values. It retries the same tenant fulfillment path and resends stored account details for fulfilled orders instead of creating another account.

## Telegram Logger

- Durable Telegram log outbox (Payment EventId 1000 and TelegramHtml EventId 1001 only): outbox SQLite file printed at startup as `[TelegramOutbox] path:` — `<contentRoot>/Data/telegram-log-outbox.db` (production: `<publish>/Data/telegram-log-outbox.db`, resolved against the content root, never the shell cwd). WAL journal, `synchronous=FULL` re-applied per pooled connection, 5s busy timeout, parameterized statements, short single-purpose transactions, and per-message WAL checkpoint never run inside transactions; `PRAGMA wal_checkpoint(TRUNCATE)` + `PRAGMA optimize` run every 10 minutes, DeadLetter rows older than 90 days are purged then.
- Schema: `TelegramLogOutbox(Id, CreatedAtUtc, Priority, DeliveryKind, BotId, LoggerChannelId, BackupChannelId, Message, AttemptCount, NextAttemptAtUtc, LastError, Status, LeaseUntilUtc, LastAttemptAtUtc)` plus index `(Status, NextAttemptAtUtc, Priority, Id)`. ParseMode is derived deterministically from DeliveryKind (Payment/Html → HTML, Plain → none) and is not stored; legacy dev-iteration DBs keep an unused `ParseMode` column and are migrated by adding the lease columns. Only identifiers are persisted — `BotId` is resolved to a live `ITelegramBotClient` via `BotClientProvider` at delivery time, so no client object must survive a restart.
- Durable pipeline (`Domain/Logging/TelegramLogOutbox.cs` + `Domain/Logging/TelegramLogDispatcher.cs`): producer snapshot → synchronous SQLite INSERT/COMMIT (the logger call blocks only for the commit and returns `false` locally if it fails; failure is counted and console/file-logged, never re-logged through Telegram and never allowed to crash payment settlement) → wake signal → single serialized worker drains directly from SQLite (signal wake-up for latency plus a 5s periodic scan for lost wake-ups: even a crash between COMMIT and signal cannot lose a record). Atomic claim is a conditional UPDATE (`Status=Pending AND NextAttemptAtUtc<=now` → `Status=Sending`, `AttemptCount+1`, `LeaseUntilUtc=now+2min`, `LastAttemptAtUtc=now`) so two workers/restarts cannot double-own a row. Startup force-resets every Sending row without honoring the lease (single-instance assumption: the documented production unit `/etc/systemd/system/vpnetiranbot.service` in `comands.txt` runs exactly one `ExecStart` process against this DB, so a fresh process proves the previous owner is gone; two processes sharing one outbox DB is deliberately NOT supported — no distributed locking). The periodic scan additionally expires `LeaseUntilUtc<now` rows for multi-worker safety. `dotnet publish` (the documented deploy command, same `--no-restore` run) does not delete or rewrite `publish/Data/*` — verified byte-identical across republish — so the outbox and `users.db`/`credentials.db` survive application upgrades; only a deploy step that manually `rm -rf`s the publish directory could lose them. Telegram I/O never runs inside an outbox transaction and never inside a SQLite transaction.
- Retry/classification (all numbers in `Domain/Logging/TelegramRateLimitPolicy.cs`): success → DELETE row (no Delivered history; at-least-once duplicate window is the only cost). 429 → `Status=Pending`, `NextAttemptAtUtc=now+RetryAfter(+1s buffer, capped 61s)`; a restart mid-cooldown still waits. Transient (HttpRequestException, TaskCanceledException/timeout, 5xx, IO) → exponential backoff 5s,10s,20s,… capped at 5 minutes, persisted as `NextAttemptAtUtc`. Permanent (400 bad request/can't parse entities/chat not found, 401, 403 bot blocked, 410) → retried with the same short backoff and DeadLettered on the 3rd failed attempt: `Status=DeadLetter` + `LastError` + `AttemptCount` retained for inspection, never deleted implicitly, never retried forever. Sends are paced ≥350ms; shutdown mid-send leaves the row Sending for lease recovery (never ack, never lose).
- Dispatcher fairness and memory: one drain round claims at most 2 Payment + 1 Html rows (2:1 weighted, starvation-free) and then at most 1 Normal item; candidates load in pages of 96, so a 1000-row backlog never loads into RAM. Normal (plain-text) logs stay non-durable: bounded Channel capacity 256 with deterministic drop+count on overflow, so a Telegram outage cannot grow disk for informational noise. Backlog warning `[TelegramOutbox] backlog high: pending=…` prints to console/file (never Telegram) at most once per minute above 200 pending. Shutdown prints enqueued/delivered/rateLimited/transient/deadLettered/backup stats.
- Payment database backups are a coalesced side effect, decoupled from log acknowledgement: after a Payment row's Telegram delivery succeeds the dispatcher requests a backup, and a single-flight loop with a 1.5s burst debounce (5s hard cap under continuous traffic) runs at most one backup at a time against `users_backup.db`/`credentials_backup.db` (temp files adjacent to the source DBs; users.db/credentials.db paths come from the runtime AppConfig). A 20-payment burst therefore produces 1-2 backup runs, never 20 and never two concurrent copies. Backup failures stay fail-soft per database.
- Invariants enforced by the outbox harness (outside the repository): a durable Payment/Audit record is never silently lost due to process restart, server reboot, Telegram outage, 429 cooldown, full in-memory queue, or lost wake-up; crash between Telegram success and local DELETE is the accepted at-least-once duplicate window; 1000-record outage keeps RAM bounded (single-digit MB growth) and drains fully after recovery.

## Durable Telegram Update Inbox and Scheduler

**USER AVAILABILITY INVARIANT:** A failed, interrupted, or uncertain side effect may freeze only its exact durable
business operation. It must never freeze `BotId + TelegramUserId`. Provisioning and renewal are verified against XUI
before wallet debit; business recovery remains asynchronous and never blocks Telegram interaction, including `/start`
and the main menu.

- Lifecycle: `TelegramUpdateScheduler.EnqueueAsync` (called by each bot receiver) durably persists the full update in
  `users.db` (`TelegramUpdateInbox`) BEFORE the receiver moves on; if the row already exists for `BotId + UpdateId` it
  is recognized as a duplicate and accepted without capacity. Admission is bounded by unfinished rows
  (`telegramUpdateQueueCapacity`, default 1000) and waits with backpressure rather than dropping. The scheduler then
  claims rows with an atomic `queued -> running` conditional update and executes them. Every handler exit is terminal:
  `completed`, `completed_with_error`, or `completed_with_review`; all erase the private payload immediately and retain
  only coarse metadata needed for seven-day deduplication and review correlation.
- Ordering: lanes are `BotId + TelegramUserId` (zero = the separate anonymous/fallback lane). Only lane heads are
  eligible (`ReadReadyAsync` excludes rows whose lane has earlier `queued` or `running` work), so one user's live updates are
  strict FIFO while different users and different bots run concurrently. Round-robin across bots with per-bot user
  cursors prevents starvation; global active-handler concurrency is bounded by `telegramUpdateMaxConcurrency`
  (  default 16). Idle keyed state is removed; no per-user workers or in-memory queue is required for correctness.
- Wake model: the coordinator waits on a coalesced `SemaphoreSlim` signal (released after durable admission commits,
  handler completion, business-review changes, and bot availability changes) OR a 2s recovery scan (`RecoveryInterval`),
  whichever fires first; `Task.Delay(25)` is gone. SQLite durable state remains the source of truth: a lost signal is
  harmless because the periodic scan re-queries `ReadReadyAsync`. Ready-query, wake, and recovery-scan counters plus
  pending-recovery count/age instruments are exposed as operational metrics; tests prove ~0.5 ready queries/second idle
  (was ~40/s) and that direct durable admission without any signal still executes.
- Execution and BotContext: `TelegramUpdateExecutor` resolves the EXACT bot by the stored `BotId` (never the default
  bot), refuses disabled/unavailable bots (their queued work stays deferred), pushes the bot runtime context through
  `BotContextAccessor` inside `using` semantics (restored even on exceptions), and runs each update in its own DI
  scope (`TelegramBotService` is scoped; a regression test asserts no singleton captures a scoped context).
  `TelegramUpdateExecutionScope` exposes the inbox sequence via AsyncLocal so wallet receipts and creation
  reservations can store an audit reference; it never stores payloads or clients.
- Restart/shutdown: startup `RecoverAsync` converts leftover `running` claims and legacy `uncertain` rows to payload-free
  `completed_with_review` receipts without replay, keeps queued work, and expires all terminal dedup receipts after 7
  days. Shutdown closes admission, drains runnable work for `telegramUpdateShutdownDrainSeconds` (default 90), then
  cancels handlers and terminalizes active receipts; `HostOptions.ShutdownTimeout` is drain + 30s. Linked XUI, renewal,
  link-change, wallet, payment, and tenant-order records retain recovery state independently while later user updates run.
- `Services/AsyncKeyedGate.cs`: short-lived per-resource in-process mutex (payment id, order id, sync-event id,
  invoice id) with idle-key removal; correctness across restarts always comes from durable database state, not gates.
- `Services/UserWorkflowStore.cs`: scoped per execution; every read returns detached snapshots (context disposed
  immediately), `SaveAsync` reloads each changed row, validates its original scalars, applies only changed fields in
  one short transaction, and fails with `DbUpdateConcurrencyException` instead of overwriting newer state. Multi-row
  batches commit atomically and never attach the input snapshot. Reload after conflict; never retry external work.
- `Data/SqliteOperation.cs`: retries ONLY SQLite BUSY/LOCKED (5/6), at most 3 attempts, fresh context per attempt,
  bounded 50/150ms + 0-50ms jitter backoff, never network inside the delegate. Both databases run WAL with a 5s busy
  timeout and private cache; connections are per-operation and short.

## Wallet Operations (durable receipts)

- Every balance mutation (debit, credit, refund, purchase, renewal, referral reward, provisional payment, settlement,
  admin adjustment) goes through `CredentialsStore.MutateWalletAsync`: the `WalletOperation` receipt and the balance
  change commit in ONE credentials.db transaction; the receipt records before/after balance, signed toman amount,
  `OperationKey` (a business key such as `payment:hooshpay:{id}:credit` or `purchase:{orderId}:debit`, never a
  Telegram update id), approval evidence (`official`/`provisional`/`partial` + admin id), `BotId`, and inbox sequence.
  Reusing a key with the same user+amount returns the original receipt (idempotent, survives restart); reusing it
  with different financial parameters throws loudly. No bot client or gateway object is ever stored.
- `users.db` ledger (`WalletLedgerService.RecordAsync`) is written after the credentials commit, keyed by the same
  operation key. A crash between the two commits is repaired by `WalletOperationReconciliationService`: it reads
  committed-but-unreconciled receipts older than one minute, writes the missing users.db ledger entry idempotently,
  completes payment/referral metadata, and only then marks `ReconciledAtUtc`. It never changes balances, never calls
  providers, and never infers history from current balances. A receipt whose payment target is missing stays pending
  for operator review (never falsely reconciled). There is deliberately NO distributed transaction between the two
  databases.## XUI Creation Reservations

- `Services/XuiV3CreationOperationStore.cs` + `Domain/XuiV3CreationOperation.cs`: before the non-idempotent XUI
  `addClient` POST, the first caller persists a stable client identity and requested plan parameters in users.db
  (`OperationKey`, owner, `PanelKey`, inbound ids, business parameters JSON). A reservation alone NEVER authorizes
  POST: the caller must also win the atomic `TryStartPostAsync` (`Reserved -> PostStarted`, exactly one concurrent
  winner; a crash after this commit is uncertain even if HTTP was never sent). After a timeout/ambiguous response the
  operation is resolved by read-back only (GET by generated email; `MatchesReservedCreation` requires exact email,
  subscription id, Telegram owner, and UUID when present). `AppliedAtUtc` is written only after proven creation or
  identity-safe read-back. Reusing a reservation with conflicting plan parameters fails loudly. A handled-but-unproven
  creation leaves its operation `PostStarted`/`Ambiguous`; the linked inbox becomes a payload-free
  `completed_with_review` receipt and later messages run immediately.
- Creation outcome state machine (`XuiV3CreationOutcome` on `XuiV3CreationOperations`): `Reserved` (identity
  persisted, no POST) -> `PostStarted` (the ONLY durable claim that authorizes POST; atomic conditional
  `Reserved->PostStarted` update, affected-rows == 1 wins) -> `Applied` (authoritative success or identity-safe
  read-back), `DefinitiveRejected` (only explicit pre-mutation validation errors such as "empty payload"/"client
  email is required"/"at least one inbound is required"; terminal, retry needs a new business key), or `Ambiguous`
  (transport timeout/lost response/unknown failure; GET-only recovery, never a second POST). `PostStarted` and
  `Ambiguous` freeze only that creation key and allow GET-only recovery; no outcome freezes the Telegram user. Migration
  `20260906040843_AddCreationOutcomesAndInboxReview` maps historical `AppliedAtUtc != null` to `Applied` and NULL to
  `Ambiguous` (never `DefinitiveRejected`).
- Operator control plane (`Services/TelegramInboxAdminService.cs`, `Services/TelegramInboxReviewStore.cs`):
  private-chat global super-admins only (default-owned non-tenant bot; tenant owners/customers get fixed "Denied."),
  handled by the receiver BEFORE normal admission so it stays reachable when customer capacity is full.
  `/inbox_uncertain [SEQUENCE|page N]` lists sanitized metadata (sequence, bot, user, update type, coarse failure
  code, ages; never payloads/keys/links); `/inbox_reconcile SEQUENCE` performs identity-safe GET read-back of linked
  creation reservations against the configured panel (never a POST, never the original handler) and persists positive
  proof; `/inbox_resolve SEQUENCE review-N` closes a terminal review receipt only when the conservative evidence evaluator
  (`CanResolveAsync`) finds no unresolved creation, unreconciled wallet receipts, pending renewal/link/order/settlement
  evidence. Ordinary non-financial failures become `completed_with_error` and require no admin action. Review metadata
  retains the reference and operator id but never gates scheduler work. Recovery receipts do not consume admission
  capacity; periodic scans record local metrics without repeated Telegram warnings. `/inbox_reject_creation
  SEQUENCE review-N` proves only the exact persisted reserved email is absent through an authenticated XUI v3 GET
  whose successful envelope is exactly `success=false,msg=Obtain (record not found)`; it atomically transitions only
  exact linked `PostStarted`/`Ambiguous` rows to `DefinitiveRejected` and records reviewer audit fields. `/inbox_retry_tenant_order
  SEQUENCE review-N` is a generic paid/unfulfilled purchase recovery that calls the central attempt coordinator with a
  durable `ReviewedRecovery` authorization (it no longer hardcodes `retry:1`). Legacy nullable renewal/link rows
  correlate only within a bounded window (five minutes before inbox acceptance through two hours after start/acceptance);
  modern rows require exact `InboxSequence`.

## Tenant purchase provisioning attempts (generational)

- A `TenantBotOrder` is the COMMERCIAL transaction (owner, customer, service, traffic, duration, prices, paid evidence).
  An `XuiV3CreationOperation` row is one IMMUTABLE PROVISIONING ATTEMPT for that order: `tenant-create:{orderId}` for
  the first attempt and `tenant-create:{orderId}:retry:N` for explicit retries. Attempts are never rewritten or reused
  with changed parameters, and the historical row is never copied into a retry.
- `Services/TenantProvisioningAttemptCoordinator.cs` is the single attempt-resolution engine used by every tenant
  purchase fulfillment path (all flow through `FULFILLPAIDTENANTORDERASYNC`). State rules: no attempt allocates the
  base key; `Reserved` reuses and may compete for its one POST; `PostStarted`/`Ambiguous` reuse for GET-only
  read-back/reconciliation only; `Applied` resumes idempotent settlement (no addClient, no retry:N) so a crash before
  `IsFulfilled=true` continues from the same account; only `DefinitiveRejected` PLUS a NEW explicit durable retry
  authorization allocates `retry:(highest+1)` with the CURRENT operational inbound/panel topology.
- Explicit retry authorization is typed (`OwnerExplicit`, `SuperAdminExplicit`, `ReviewedRecovery`) with a durable
  restricted key (`tenant-retry:{orderId}:tg:{inboxSequence}` or `tenant-retry:{orderId}:review:{reviewReference}`)
  persisted as `XuiV3CreationOperation.AuthorizedByKey` (migration `20260906081225_AddTenantCreationAttemptRetryAuthorization`).
  Automatic paths (duplicate provider callbacks, reconciliation workers, startup, customer checks, ordinary re-entry)
  pass no authorization and can never advance a generation; one authorization event grants at most one generation, so
  replaying the same key after its attempt is rejected never allocates the next one.
- The routine owner flows that authorize retries: Sales Assistant final confirmation, owner OrderId re-entry, and the
  super-admin OrderId confirm flow (including the gateway `ApplyPaidTenantOrderAsync` overloads). `/inbox_retry_tenant_order`
  supplies the reviewed-recovery authorization. No retry creates a payment, invoice, order, or price change.

## Gozargah Site Sync

- Site sync is optional and controlled by `GozargahSite*` config flags.
- Successful create/update/delete/link-change operations enqueue or send sync events through the outbox in `users.db`.
- Website records for tenant purchases belong to the tenant owner while preserving buyer Telegram id for audit. This
  owner/buyer split applies to create, renew/update, rename, delete, expired-delete, historical sync, and admin
  recovery; it never rewrites the XUI client's `tguserid` or `XuiV3ClientMetadata.TelegramUserId`.
- Gozargah enqueue/send is best-effort after a durable panel/order/ledger effect in tenant and shared customer flows;
  website failures are logged and retried from the outbox without turning a valid account operation into failure.
- Queue admission and remote send use separate per-account gates: `QueueGate` protects only the short database-only
  unresolved/latest-state lookup, semantic dedupe, and insert/reuse decision, while `AccountGate` serializes the actual
  website I/O inside `SendEventCoreAsync`. Deferred enqueues (`deferSend: true`) therefore never wait behind
  get_user/create_order/update_order/delete_order of the same account; they persist the row, wake the retry worker,
  and return.
- `get_user` failures are classified: the documented HTTP 404 missing-user result and plain business not-found/banned
  messages become terminal Skipped; HTTP 5xx, invalid JSON, HTML/non-JSON bodies, undocumented 4xx, and empty
  responses become Failed with a bounded LastError so the retry worker can recover later instead of silently losing
  the website mirror event.
- Pending sync events may need to re-read fresh XUI panel data before a super-admin retry.
- `get_user` HTTP 404 from the Gozargah website means the Telegram user has no website account; wallet-button checks treat it as expected and must not spam the Telegram logger channel.
- A `delete_order` that hits a missing website order is the desired end state (the order is already absent). `TrySendEventAsync` marks such a delete as skipped instead of leaving it `Failed`, and `SendAsync` suppresses the warning for expected `delete_order`/`update_order` 404 "not found" responses (mirroring the `get_user` exemption). Without this, a stuck/duplicate delete stays `Failed` and the two-minute `GozargahSiteSyncRetryService` resends and re-logs it forever, flooding the logger channel with repeated `Order not found.` messages.
- Owned-bot profile/status messages should display Gozargah `get_user` 404/not-found as `متصل نشده`, not as the raw HTTP/API error.
- A successful non-banned `get_user` lookup means the owned-bot buyer should be promoted to `CredUser.IsColleague=true` before tariffs, purchases, or renewals are priced.
- Tenant renewal fulfillment mirrors the purchase `fulfillmentCommitted` boundary: after the atomic users.db commit
  (order fulfilled + ledger + notification intents) any post-commit failure (operation `MarkSettledAsync` or optional
  website mirror) is logged as a bounded warning and still returns Applied; the renewal operation stays locked and the
  recovery worker settles it later from its durable Applied state. `MarkSettledAsync` is virtual only as a test seam.
- `TenantOrderNotifications` (users.db outbox) retention is configurable via `tenantOrderNotificationRetentionDays`
  (positive, default 30). The worker deletes at most 100 rows per scan that are Delivered, older than the cutoff, and
  carry no claim/lease; Pending/Processing/DeliveryUncertain/ManualReview/FailedPermanent rows are never removed.
  Migration `20260909204045_HardenTenantOrderNotificationOutbox` adds `SendStartedAtUtc` and the
  (Status, DeliveredAtUtc) index.
- Notification worker durable send phase: after the atomic claim the row is Processing with `SendStartedAtUtc=null`;
  the phase is persisted immediately before the Telegram transport call. An expired lease with a null phase is
  recycled to Pending (send never started, retryable); an expired lease with a set phase becomes DeliveryUncertain
  (remote outcome ambiguous, never replayed).
- Tenant storefront funding alerts (users.db `TenantStorefrontFundingAlerts`/`...States`) are edge-triggered on `TenantAccessDecision.InsufficientFunding` only; `underfunded_transition` fires once per underfunded episode, `customer_attempt` obeys `tenantUnderfundedCustomerAttemptNotificationCooldownMinutes` (default 15). Owner delivery always goes through the owned/default bot, never the tenant bot transport; the customer lane only persists/queues rows.
- Funding-alert worker two-phase delivery mirrors the notification outbox: claim sets Processing + `SendStartedAtUtc=null`, the phase is persisted immediately before Telegram transport via a conditional update (Id + Processing + ClaimToken + null phase; 0 rows updated => no send). Expired lease + null phase => recycled to Pending/retryable; expired lease + set phase => DeliveryUncertain, never replayed. `Cancelled` is terminal.
- On funding recovery (`Allowed`) the same transaction clears the state episode and cancels Pending rows plus Processing rows whose send never started; Processing rows with `SendStartedAtUtc != null` stay conservative. Pre-send, the worker re-validates the storefront state: must exist, `IsUnderfunded`, and `EpisodeNumber` must match the alert (0 = legacy row, accepted while underfunded); otherwise the alert is cancelled without any Telegram call. Migration `20260910001835_HardenTenantStorefrontFundingAlertDelivery` adds `SendStartedAtUtc`, `EpisodeNumber`, and the (Status, DeliveredAtUtc) index.
- Delivered funding-alert retention is configurable via `tenantStorefrontFundingAlertRetentionDays` (positive, default 30); the worker deletes at most 100 Delivered rows per scan that are older than the cutoff and carry no claim/lease. Pending/Processing/DeliveryUncertain/ManualReview/Cancelled rows are never auto-deleted.
- `TenantStorefrontFundingMonitorHostedService` (interval `tenantStorefrontFundingMonitorIntervalMinutes`, positive, default 5) proactively detects external Gozargah wallet drops between customer interactions. It uses the read-only `TenantAccessService.EvaluateFundingSnapshotAsync` (no debt repayment, no site-wallet debit, no wallet/order/payment/XUI mutation), dedupes owners to one get_user per owner per cycle, processes a rotating bounded batch of 20 storefronts, and skips overlapping cycles.
- Optional Gozargah `get_user` lookups for owned-bot pricing and wallet-button visibility are fail-soft with a short timeout; a slow website API must not block tariff or purchase menus.
- Owned-bot renewal with a selected Gozargah website wallet falls back to a local bot-wallet debit if website
  eligibility or post-XUI debit fails; the local balance may become negative and a dedicated ledger provider records
  the compensation. Explicit bot/site bans still block service and never use this fallback.
- Central owned-bot purchase and renewal logs include the wallet that was actually debited (`کیف پول ربات`,
  `کیف پول سایت گذرگاه`, or the bot-wallet fallback after a site-wallet failure). This audit value comes from the
  completed settlement result, not merely the payment button selected by the colleague.

## Current Gotchas

- Persian/RTL Telegram text and emoji are production UI; edit surgically and verify diffs for mojibake.
- Super-admin `📊 آمار هفتگی` and `📈 آمار ماهانه` use only the latest 7/30 completed Tehran days through yesterday.
  Daily users are globally distinct across all owned/tenant bots, interactions include messages and callbacks, and
  configured super-admin ids are excluded. Both commands send a readable PNG line chart plus a concise caption rather
  than a text-only daily list. Tenant owner stats reuse the same parser with a strict tenant `BotId` filter.
- Scheduled usage reporting is controlled by optional `weeklyUsageReportEnabled`; a missing old config key is false and
  must not fail startup. Saturday 00:01 Tehran delivery compares the completed Sat-Fri week with its predecessor. Sales
  include only structured successful owned account purchase/renew events and fulfilled tenant order `SalePriceToman`.
- Owned wallet-charge and tenant online-payment buttons show their instant-settlement label and customer-facing fee
  policy: NOWPayments 0%, Tetraminator 12%, and HooshPay 15%. Owned amount entry explicitly supports either a suggested
  amount button or a manually typed toman amount; legacy gateway button labels remain routable after deployment.
- `UsageReportDispatches` exists only in `users.db`; unique `ReportKey`, atomic claims, and leases prevent concurrent
  workers. Failed generation or Telegram delivery releases the same row for retry; successful delivery is terminal.
  If Telegram returns a valid message but final sent-state persistence fails, the worker records a non-retryable
  reconciliation state instead of deliberately sending a duplicate. The report sender bypasses payment logging, so it
  does not trigger database backups.
- `credentials.db` is shared wallet/profile state and is intentionally kept stable.
- Financial `LogPayment` backup sends both `credentials.db` and `users.db` to the configured backup channel; backup failures must stay fail-soft and must not break settlement.
- XUI v3 panel responses may omit `Traffic`; helpers must use null-safe access and fallback to top-level fields or `Extra`.
- The daily 08:00 time-expiry reminder must exclude finite accounts whose bytes reached quota or whose traffic row is
  disabled at/above 99%; volume consumption messaging belongs only to the independent volume worker. Its optional
  config defaults are disabled/30 minutes, enabled interval must be 5-1440, and production config explicitly enables
  30 minutes. The full clients list supplies normal usage/`updatedAt`; direct client GETs are limited to persisted,
  backoff-controlled expiry verification or newly due reminder-comment enrichment and must never become general
  per-client polling. Reminder messages show only the verified customer `UserComment`, never raw metadata JSON.
- `XuiV3VolumeReminderStates` is unique by credential-free `PanelKey + ClientId`. A cycle resets for counter drop,
  quota increase, client recreation, newer bot renewal metadata, or successful owned/admin/tenant renewal hook;
  `updatedAt` alone never resets it. Only the highest crossed threshold is sent, stale ambiguous claims are suppressed
  to prevent duplicates, and a successful renewal must never be rolled back when reminder-state persistence fails.
  Volume eligibility persists a sanitized reason and per-expiry-source categories. A list/metadata expiry conflict is
  verified read-only by numeric client id plus normalized email; GET failure/mismatch is fail-closed with backoff, does
  not claim or advance 80/90/99, and no volume-reminder path may mutate an XUI account.
- Metered XUI pricing is centralized in `XuiV3PurchaseService.ResolvePurchase`: finite durations charge
  `trafficGb * rolePricePerGb + days * rolePricePerDay`, while zero-day lifetime durations charge
  `trafficGb * rolePricePerGb * lifetimePriceMultiplier` and round upward to whole toman. Missing daily prices,
  lifetime multipliers, and duration `isEnabled` values default to zero, one, and true for catalog compatibility.
  Disabled durations must be omitted and rejected across owned purchase/renewal, super-admin creation, and tenant
  purchase/renewal; the separate fixed-price `kind=unlimited` plans do not use these metered fields. The resolver also
  returns the authoritative metered component breakdown, and owned-bot final purchase/renewal previews must format
  that stored breakdown rather than recalculate rates or subtotals in the Telegram presentation layer.
- Unlimited audience and role price are intentionally separate. Generic `ResolvePurchase(selection, isColleague)`
  remains capable of resolving both public and colleague prices for tenant calculations; owned and tenant wrappers
  revalidate their respective audience policy before preview, callbacks/state consumption, payment/order creation,
  and final fulfillment. The Eco unlimited plans are owned-colleague-only but tenant-visible. They use ordinary tenant
  markup pricing: zero markup keeps the configured user price, while positive markup applies to the colleague base
  cost; owner base cost remains the colleague price and profit is never negative.
  Missing policy fields preserve legacy behavior (`false`, `true`, `false` respectively), so no database migration or
  callback/state format change is required. Super-admin manual creation remains an administrative `IsEnabled` flow.
- Owned XUI purchase state restored from `BotUserStates` is revalidated against the live catalog before count,
  comment, preview, or confirmation is consumed. Removed/disabled service, low traffic, disabled/custom-out-of-range
  duration, and disabled unlimited sub-plan lazily return only that bot/user to the earliest valid selection step;
  valid service/traffic choices are preserved, the triggering text is not reused, and no wallet/order/XUI effect occurs.
  Service/traffic/duration/plan/count callbacks repeat the same checks, while final resolution remains fail-closed.
  `BotUserState.ApplyPartial` deliberately means `null = preserve`; use an explicit empty string to clear one string or
  `UserDbContext.ResetUserStatus` to atomically clear all transient fields and install a replacement state. Recovery
  audit events contain only bot/user identity, previous step, coarse target, and safe service key.
- The normal metered service may enable `customDurationDays` with an inclusive `minimumDays`/`maximumDays` range capped
  at 365. Typed Latin, Persian, or Arabic-Indic whole numbers are persisted in callbacks, `users.db`, and tenant orders
  as canonical `days-N` keys. Missing policy means disabled. Custom days are independent of preset `isEnabled` flags,
  but are revalidated against the live policy before owned/tenant confirmation, payment activation, and fulfillment;
  `days-` is reserved and cannot prefix configured duration keys. Tenant purchase/renewal summaries show the effective
  storefront traffic/day calculation without exposing colleague base rates. This behavior needs no database migration.
- XUI v3 request timeout is controlled by `xuiV3RequestTimeoutSeconds` in `Data/configuration.json`; slow panels can otherwise time out during `/panel/api/clients/add`.
- Owned and tenant renewal entry uses explicit account-selection state and always clears that bot/user state before a
  terminal lookup/service/panel result. Entry, success selection cards, and terminal results expose `x3:home`, so reply
  keyboard text cannot keep being consumed as an account name. Non-owner targets require `x3:rgo`; after confirmation
  the payer gains renewal only, never configuration/change/delete/state access. The panel UUID lock is stored separately
  from `PaymentMethod`, never transported in callbacks/logs, and exact email+UUID matching is repeated before preview,
  order creation, payment settlement, or XUI mutation. The payer is audit actor only; existing `TgId`, metadata owner,
  UUID, password, SubId, and protocol identity are preserved.
- `ApiServicev3.GetClientAsync` normalizes both legacy direct `obj` and current 3x-ui `obj.client` detail envelopes,
  merging wrapper `inboundIds` without synthesizing identity. Tenant renewal classification uses that identity-checked
  detail comment before inbound/expiry inference because active normal and unlimited accounts share inbounds and an
  unlimited first-use expiry becomes positive after connection. `ServiceKey` plus `ServiceKind` must agree with one
  enabled catalog service. A transient detail failure may use readable list metadata but never legacy inference.
- After a successful detail read with no usable metadata, national inbound and negative first-use expiry remain
  deterministic. A positive/zero-expiry legacy account matching multiple categories must preserve its exact email+UUID
  lock and let the customer explicitly choose among live tenant-compatible services; quota, duration, display text, and
  free-form comments are never heuristics. `BotUserState` and `TenantBotOrder.RenewalServiceResolutionMode` persist the
  evidence. Migration `20260901234439_AddTenantRenewalServiceResolutionMode` adds nullable columns only, with no backfill
  or financial/XUI work. Null historical unpaid ambiguous orders must be recreated; null paid ambiguous orders stop for
  support review before `UpdateClient`. Payment activation and fulfillment revalidate the current candidate set.
- Tenant metered tariffs show effective storefront per-GB/per-day rates, active/custom durations, and exactly one sample
  whose total comes from authoritative tenant pricing. Purchase and renewal payment-choice messages explain that verified
  online payments fulfill automatically, while card-to-card waits for tenant-owner approval and may take longer.
- Owned and tenant renewals use the same account-level unresolved lock around their session/order operation key. A new
  renewal is rejected before mutation when UUID (or legacy email fallback) has pending, processing, ambiguous,
  manual-review, or applied-unsettled work. GET-only recovery uses `CompareRenewalState` with Applied,
  DefinitelyPreMutation, PartiallyApplied, Drifted, and Unavailable outcomes and persists a credential-free per-field
  summary. UUID, quota, expiry, enabled state, semantically compared renewal metadata, and an intentionally changed
  owner are authoritative; inbound membership, traffic-row enable, LimitIp, password, SubId, and preserved protocol
  extras are not renewal-controlled success criteria. Direct `clients/get/{email}` results are identity-checked because
  affected panels can return an unrelated client with HTTP 200; mismatch falls back to exactly one UUID match from
  `clients/list`. Recovered Applied operations reuse the existing exactly-once settlement gates. Partial/drift stays
  locked in ManualReview. A new operation stores a credential-free pre-mutation snapshot; at least three exact pre-state
  GETs spanning ten minutes may mark it Failed/NotApplied, leave settlement untouched, clear the lock, and allow a fresh
  renewal. Rows without that snapshot can never auto-unlock this way. Recovery never replays or recomputes UpdateClient.
- XUI v3 API calls use bounded retry/backoff for transient TLS/socket/timeouts and HTTP `408/429/502/503/504/520-527`; retry settings live beside `xuiV3RequestTimeoutSeconds` in `Data/configuration.json`. Read-only and idempotent-mutation endpoints keep that retry policy; every non-idempotent create (`/clients/add`, `/clients/bulkCreate`, inbound add/import, node add, geo-source add, client-group create, API-token create, DB import, backup-to-Telegram) is sent exactly once with `NoAutomaticRetry` because a timeout can hide a committed side effect. The current transport creates/disposes one handler per attempt, sends exact HTTP/1.1 with `Connection: close`, and therefore never reuses a failed TLS connection. Private error diagnostics record request/response-version availability, reuse/handler policy, retry mode, and a sanitized bad-record-MAC classifier; a mutation failure is still never replayed.
- XUI v3 account creation treats generated email as the idempotency key. `addClient` is never replayed: if the add or the follow-up client/link read fails ambiguously, the bot re-reads the panel by email and returns the recovered panel UUID/subId when the account exists instead of creating a duplicate.
- XUI v3 failures must never expose panel URLs, root paths, endpoints, responses, tokens, or cookies in Telegram.
  `XuiV3ApiException.Message` is redacted and `XuiV3UserSafeError` owns fixed user-facing creation errors; complete
  endpoint diagnostics are restricted to the private daily error file.
- XUI v3 link changes use a durable users.db saga shared by owned and tenant bots. The first `ach/asch` click only
  creates a ten-minute confirmation; an atomic callback claim, filtered unique `PanelKey + ClientId` index, and
  renewable lease ensure only one processor can rename a physical client. Snapshot and replacement email/UUID/subId
  are persisted before the first POST and reused after callbacks, timeouts, and restarts.
- Link-change identity, attach, detach, corrective-update, and traffic mutations use `NoAutomaticRetry`. Ambiguous
  responses are resolved by read-back; timeout/TLS/520/incomplete responses are `Unknown`, never empty inbound or zero
  traffic. Recovery remains locked until verification or manual review. Direct `GetClient` data supplies stable fields,
  list data supplies inbound membership, semantic JSON/null comparisons prevent false `final-fields` failures, and
  normal success logging/site sync occurs only after final verification.
- Link-change configuration keys are optional for backward compatibility and default in `AppConfig` to confirmation
  10m, poll 30s, 12 attempts, max delay 900s, and lease 300s; explicit out-of-range values fail startup. `PanelKey`
  hashes the endpoint without storing its URL and preserves case-sensitive path components.
- Durable link-change recovery treats operation columns as the immutable target identity and strips typed/response-only
  keys from XUI extension data. This prevents stale duplicate `uuid/email/subId/client` data from causing false
  `final-fields` failures or repeated corrective mutations after the panel already committed the rename.
- The 3x-ui global clients API uses different UUID names on each side of the boundary: GET `ClientRecord` responses use
  `uuid`, while POST update bodies bind `model.Client.id`. `ApiServicev3.PrepareClientUpdatePayload` performs this
  mapping; sending `uuid` in an update silently preserves the old credential on 3x-ui 3.4.x.
- A 3x-ui email rename can recreate the old email as a distinct detached ClientRecord while inbound synchronization is
  in progress. Link-change recovery deletes that row only after the replacement is fully verified, the old row has a
  different numeric id, still owns the original UUID, and has an authoritatively empty inbound set. Deletion uses
  `keepTraffic=1` and read-back, so ambiguous responses are not replayed blindly.
- XUI v3 creation-result expiry display resolves top-level `ExpiryTime`, nested `Traffic.ExpiryTime`, and client `Extra`
  before falling back to the submitted payload. This protects normal fixed-date accounts from being displayed as
  unlimited when newer 3x-ui responses return a zero top-level expiry field.
- `ApiServicev3.UpdateClientAsync` copies outgoing payloads and normalizes any legacy `Extra.allowedIPs` string into
  the JSON string-array required by 3x-ui 3.4.x. This protects owned/tenant renewal, link-change, comment, and
  enable/disable updates without discarding other panel fields.
- Owned bot `💻 ارتباط با ادمین` reads only the active bot's `SupportAccount`; it must not leak the default brand's
  support account when the active owned bot has no configured support contact.
- Operational payment/account logs are delivered through the default owned bot to the central logger channel; include origin bot metadata in the message for non-default owned bots and tenant storefronts.
- Super-admin XUI bulk-account creation audits use the dedicated `LogTelegramHtml` event: builders emit encoded `<code>` entities, the default logging bot sends them with Telegram HTML, and no financial database backup is triggered. Ordinary `LogInformation` channel messages remain plain text.
- Tenant owner mutation buttons use target-state callbacks plus row revision and an issue timestamp. Legacy invert-state
  `TBM:TOGGLE*` callbacks are read-only; delayed or repeated buttons must never reverse a newer bot/gateway/join state.
- Telegram callbacks can be stale; acknowledge long owner operations before external calls and reject expired mutation
  callbacks without changing state. Owner-panel no-op edits should be detected before calling Telegram.
- Telegram blocked-user, deactivated-user, chat-not-found, and forbidden errors are definitive per-user delivery
  failures. `Request timed out` is a transient transport failure and must never be described as an unreachable chat.
- Before every `MultiBotHostedService` receiver generation, Telegram webhook state is probed once. An active webhook is
  deleted with `dropPendingUpdates=false` and absence is verified before `StartReceiving`; transient probe/delete
  failure starts no receiver and uses the existing serialized recovery. Runtime `webhook is active` 409 schedules one
  bot-scoped stop/preflight/restart task. A real duplicate `getUpdates` process remains critical and is never restarted
  automatically; operators must remove the duplicate deployment, old service, or screen/tmux process.
- XUI/HTTP `TaskCanceledException`, `TimeoutException`, and `HttpClient.Timeout` during update handling are treated as external operation timeouts. The active bot logs `handle_update_external_timeout` and sends a best-effort retry notice instead of turning the panel delay into a Telegram polling failure.
- `Domain/Logging/TelegramLogger.cs` truncates plain-text application logs before sending them to Telegram so large exception stacks do not trigger `message is too long` and create secondary logger noise.
- `Domain/Logging/DailyErrorFileLoggerProvider.cs` writes warning/error/critical diagnostics with full exception chains
  and active bot context to the configurable daily `Data/Logs/errors-{shamsiDate}.log` file. It masks common token,
  authorization, cookie, API-key, and secret representations and is fail-soft if disk logging is unavailable.
- In owned customer routing, XUI v3 free-trial messages must be handled before purchase text flow. The trial start clears any half-built purchase session so metered purchases cannot reach summary without `TrafficGb`.
- "🗽 Admin" is privileged high-priority owned-bot navigation for configured super-admins (`AppConfig.AdminsUserIds`):
  `HandleUpdateCoreAsync` evaluates `TryHandleSuperAdminPanelEntryAsync` (then the "📑 Menu" exit) BEFORE every stateful
  XUI/customer/renewal/colleague handler and any service-plan/panel dependency. It authorizes against the admin list
  only (never `CredUser.IsColleague` or tenant ownership), refuses outside owned bots, clears only the current
  BotId/user conversation plus the in-memory XUI purchase session, and sends the fixed `پنل مدیریت` keyboard. Panel
  labels `🤝 همکار کردن کاربر` / `👤 لغو همکاری کاربر` toggle only `CredUser.IsColleague` (shared seam
  `ApplyColleagueRoleChangeAsync`) and never mutate the configuration-controlled admin allow-list.
- Bot token duplication between owned and tenant bots must be rejected or disabled at runtime.
- `MultiBotHostedService` serializes start/stop/cleanup per `BotId`; never register a receiver outside that lifecycle
  gate or overwrite its CTS. A transient bounded `GetMe` probe starts one optimistic receiver and completes identity/
  command setup in the background with `initializing/degraded` status. Invalid and duplicate tokens remain fail-closed.
- `telegramBotStartupProbeTimeoutSeconds` controls the short Telegram startup/panel probe (default 12 seconds).
  `SetMyCommands` is background initialization and must not stop an already registered receiver.
- Super-admins can use `🤖 وضعیت ربات‌ها` to see process-local receiver health for every owned, assistant, and tenant bot. The report comes from `BotRuntimeStatusStore`; it never exposes tokens and does not call Telegram.
- Telegram polling 5xx bursts such as `502 Bad Gateway` and delivery timeouts such as `Request timed out` are transient Telegram-side noise. They are swallowed before operational Telegram logging and should not be sent repeatedly to the private logger channel.
- Telegram `429 Too Many Requests` is handled centrally through `Domain/Logging/TelegramRateLimitPolicy.cs`: the polling error handler pauses the receiver for Telegram's `RetryAfter` (+1s buffer, capped at 60s) before the next `getUpdates` (Telegram.Bot 19.x does not delay on its own and would tight-loop), the update wrapper swallows a 429 after the same backoff instead of letting it kill the receiver, and `Domain/Logging/TelegramLogSuppression.cs` suppresses any log entry whose exception is a Telegram 429 so the logger never amplifies the rate-limit storm. Receivers keep polling after the window and are never restarted, so no duplicate receiver instances can appear.
- `Domain/Logging/TelegramLogger.cs` also applies message-level channel suppression for known noncritical noise: stale Sales Assistant callbacks, unchanged Telegram edits, receipt-photo relay warnings that have a text fallback, repeated tenant forced-join probes, routine XUI v3 volume-reminder scan summaries, and Telegram polling 5xx/429/timeouts. Suppression is Telegram-provider-only, so standard/local logging retains these entries; payment/audit logs and real token/XUI/settlement failures still reach the private channel.
- Tenant forced-join activation validates the tenant bot identity, channel access, administrator-list access, and that the
  bot itself is an administrator; it never probes the tenant owner's membership. Runtime storefront access still checks
  each actual customer with `GetChatMember` and remains fail-closed. Only Telegram 400 `PARTICIPANT_ID_INVALID` receives
  one 500ms cancellation-aware retry; unrelated 400 responses and permission failures are never retried.
- All production data access uses factories with short-lived contexts (`UserDbContextFactory`,
  `CredentialsDbContextFactory`, `CredentialsStore`, `UserStateStore`, `UserWorkflowStore`, inbox/creation/settlement
  stores). No EF-tracked entity survives a Telegram/XUI/gateway/website HTTP call or `Task.Delay`; reads are
  `AsNoTracking` detached snapshots, writes reload their targets in the saving context. The only process-wide lock
  left is the daily error file logger's append lock (protects a genuinely global file resource); `MultiBotHostedService`
  serializes receiver start/stop per `BotId` through per-bot lifecycle gates. The legacy `PeriodicTaskRunner`
  (`async void` timer + static DbContext) and `ZibalHttpServer` are dead code with no source references and are not
  registered anywhere.
