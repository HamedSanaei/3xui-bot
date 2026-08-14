# CODE_MAP.md

## Purpose

Adminbot is a multi-brand Telegram sales bot for XUI/3x-ui VPN accounts. It supports owned bots, colleague-owned tenant storefront bots, wallet payments, payment gateways, card-to-card tenant receipts, broadcast jobs, XUI v3 account management, and optional sync with `gozargah.network`.

## Entry Points

- `Program.cs`: ASP.NET host, DI registration, EF migration startup, controller mapping, bot runtime registration, hosted services.
- `Services/BotRuntimeServices.cs`: bot registry, bot context accessor, bot client provider, and multi-bot receiver startup.
- `Services/TelegramBotService.cs`: main dispatcher for owned bots and legacy/admin/customer flows.
- `Services/TenantBotService.cs`: tenant owner panel and tenant customer storefront flows.
- `Controllers/PaymentController.cs`: payment IPN endpoints and gateway callbacks.

## Build and Publish

- Restore/build: `dotnet restore`, then `dotnet build Adminbot.sln --configuration Release --no-restore`.
- Server publish: `dotnet publish -c Release -f net10.0 -r linux-x64 --self-contained false` from the repository root.
- The solution intentionally contains only the production `Adminbot` project. Do not add a separate test project or test-framework dependency unless the user explicitly requests that repository structure in the current task.
- `Adminbot.csproj` excludes `Adminbot.Tests/**` from default SDK items so stale untracked `bin/obj` files in an existing server checkout cannot be compiled after the removed test project is deployed.
- `Adminbot.csproj` explicitly pins `SQLitePCLRaw.bundle_e_sqlite3` to patched 2.1.12 so EF Core's native
  SQLite library, provider, and core packages resolve as one compatible family instead of vulnerable 2.1.11.

## Data Stores

- `Data/UserDbContext.cs`: bot state, tenant bot settings, payment records, broadcast jobs, wallet ledger, global referral relationships/events/rewards, tenant orders, Gozargah sync outbox, weekly usage-report dispatch leases, and durable XUI volume-reminder cycles/claims.
- `Data/CredentialsDbContex.cs`: unchanged shared user wallet/profile data; referral must not add tables, columns, or models to this database.
- `Data/configuration.json`: app-level settings and owned bot configs. Secrets live here locally and must not be copied into docs.
- `Data/configuration.example.json`: sanitized configuration example including referral and four-gateway enable/readiness settings; all gateway switches and secret placeholders default to off/empty.
- `Data/xui-v3-service-plans.json`: XUI v3 service catalog, inbounds, metered per-GB/per-day/lifetime pricing, duration availability, unlimited fair-usage plans, and `minimumTrafficGb`.
- Historical migration filenames and `[Migration("timestamp_name")]` ids are immutable database history. Their CLR
  types use PascalCase to keep current compilers warning-free; never rename the files or attribute ids as cleanup.

## Core Services

- `Services/XuiV3PurchaseService.cs`: resolves service selections, validates plan rules, builds XUI v3 account metadata, and creates accounts.
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
- `Services/XuiV3RenewalTargetParser.cs` + `XuiV3RenewalTargetResolver.cs`: shared side-effect-free exact renewal lookup
  for email, raw SubId/full subscription link, UUID, and VLESS/VMess/Trojan/Shadowsocks/Hysteria configurations. One
  fresh `clients/list` snapshot must produce exactly one client; configuration matching trusts only embedded UUID or
  protocol password, never host/fragment/display label. Password matching is ordinal and duplicate matches fail closed.
- `Services/XuiV3ClientPlanEligibility.cs`: checks whether an XUI client belongs to active service inbounds.
- `Services/XuiV3ClientUsageResolver.cs`: shared null-safe XUI list-response interpretation for consumption, quota,
  expiry, `createdAt`, `updatedAt`, origin bot, and renewal metadata. A missing nested `traffic` object falls back to
  top-level/extension fields; the volume worker isolates recoverably malformed rows so one client cannot abort a full
  panel scan.
- `Services/XuiV3VolumeExpirationReminderService.cs` + `XuiV3VolumeReminderStateStore.cs`: one-list-request
  30-minute 80/90/99 traffic reminder worker and users.db cycle/claim idempotency. Notifications are bot-scoped,
  separate per account, rate-limit-aware, and require a matching `BotUserState`; migration
  `20260809000000_AddXuiV3VolumeReminderStates` needs no backfill.
- `Services/XuiV3AdminFlowService.cs`: super-admin XUI v3 management flows.
- `Services/XuiV3LinkChangeOperationStore.cs`: per-operation users.db contexts, atomic confirmation, active-client uniqueness, leases, and bounded recovery state for link changes.
- `Services/XuiV3LinkChangeRecoveryService.cs`: hosted worker that resumes the exact persisted email/UUID/subId after ambiguous XUI responses or process restarts.
- `Services/BroadcastManager.cs`: queued broadcast engine with progress/status tracking and retry behavior.
- `Services/SalesAssistantService.cs`: central assistant bot for tenant sale notifications and manual receipt approval.
- `Services/WalletLedgerService.cs`: append-only wallet ledger for credits/debits.
- `Services/ReferralService.cs`: global owned-bot relationship registration, reward calculation, users.db state/ledger idempotency, user stats, notifications, and startup reconciliation.
- `Domain/PaymentGatewayAvailability.cs`: process-wide live snapshot for HooshPay, Tetraminator, UniquePay, and NOWPayments. Super-admin target-state callbacks use this service; only root `enabled` booleans are persisted through the byte-preserving atomic JSON editor. API credentials remain restart-loaded and are never displayed or logged.
- `Domain/UniquePay.cs`: UniquePay Bearer/form-urlencoded bot-gateway client, owned-wallet settlement, fail-closed authoritative verification for both official toman fee-payer contracts, durable settlement claims, restricted provisional OWNED credits, callback coordination, and bounded recovery polling. New invoice creation is single-attempt; inquiry is read-only.
- `Services/UsageAnalyticsService.cs`: completed Tehran-day aggregation of JSONL messages/callbacks, successful owned sales, and fulfilled tenant sales; excludes global super-admin ids and supports tenant bot filtering.
- `Services/UsageReportChartRenderer.cs`: cross-platform SkiaSharp high-resolution line-chart PNG renderer with
  explicit Y scales, every weekly/monthly date, adaptive value labels, point markers, and current-versus-previous weekly comparison. It uses
  the embedded OFL-licensed `Assets/Fonts/NotoSans-Regular.ttf`; never fall back to `SKTypeface.Default`, because
  minimal Linux hosts can silently render every chart label blank.
- `Services/WeeklyUsageReportHostedService.cs`: Saturday 00:01 Tehran report scheduler, catch-up behavior, users.db claim/lease idempotency, and direct central logger delivery through the default owned bot.
- `Domain/GozargahSite.cs`: Gozargah site API client, sync event models, mapping, and retry helpers.

## Tenant Bot Rules

- Each tenant bot is a `BotInstance` with `Type = tenant` and id `tenant-{ownerTelegramUserId}`.
- Tenant runtime state is scoped by `BotId + TelegramUserId` in `BotUserStates`; never key tenant customer flow only by Telegram user id.
- Tenant customers reuse shared XUI account flows where possible, but tenant payments and fulfillment go through `TenantBotOrder`.
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
- UniquePay payment records live in `users.db` (`UniquePayPaymentInfos`) and link owned wallet charges or tenant orders through `HashId`, optional provider `RefId`, and `TenantBotOrder.UniquePayPaymentInfoId`. Migration `20260731000000_AddUniquePayPayments` also adds `BotInstance.TenantUniquePayEnabled` (default `true`) and reconciliation indexes; `credentials.db` is unchanged.
- UniquePay amounts are Iranian toman and the authoritative API may label the currency `IRT` or `toman`. Settlement remains fail-closed: `check-invoice` must return `status=true`, `code=200`, `isPaid=true`, the saved provider reference must match `invoice.id`, and any returned root hash must match the saved merchant hash. Production inquiry responses can omit that root hash echo. The live buyer alias is `feePayer=user`: `invoice.amount` must equal the stored base and `payableAmount` must equal `base + fee + uniqueAmount`; owner-paid responses use `base + uniqueAmount`. Legacy `feePayer=buyer` responses without payable fields retain the documented `invoice.amount - invoice.fee = base` rule. The fee must match the snapshotted percentage within one toman. `isVerified` is informational and not a settlement requirement.
- UniquePay invoices use `/api/ddbot/create-invoice` so each OWNED/TENANT invoice carries separate configured return and callback URLs. The unsigned `POST /uniquepay-callback`, browser return, and customer check are lookup triggers only; every financial change still requires authoritative `/api/check-invoice`. Recovery polling uses `NextInquiryAtUtc`, exponential backoff, a configurable hard attempt cap (default 12), and independent factory-created EF contexts; reaching the cap stops automatic queries but never blocks callback/return/customer/admin inquiry or settlement. Disabling the global switch blocks new invoices only. Explicit provider lifecycle hints map to `expired`, `cancelled`, or `failed`; absent/unknown hints remain `pending`, and network errors remain retryable within the recovery cap. Creation failures log safe bot/tenant/order/payment identifiers and provider status codes only.
- UniquePay settlement uses an atomic users.db claim (`pending -> processing -> settled`) before wallet/XUI side effects. Because wallet/tenant fulfillment crosses users.db, credentials.db, and XUI, a process crash after the claim is ambiguous; claims stale for 30 minutes move to `manual_review` and are never automatically replayed, preventing duplicate wallet credit, owner profit, or account delivery.
- Migration `20260801000000_AddUniquePayProvisionalApproval` adds provisional audit fields and safely requeues only unsettled rows failed by the former currency/fee-payer validator. Data-only migration `20260801220000_RequeueUniquePayOptionalHashFailures` requeues uncredited rows rejected by the former mandatory hash-echo rule; all are freshly verified before settlement. Super-admin `Verify payment` accepts `UP:<internal-id>`, Hash ID, or Ref ID; it always performs an official inquiry first. A still-unpaid, valid OWNED wallet invoice can be credited provisionally through two confirmation stages using only its stored base amount. TENANT invoices, terminal/mismatched responses, and provider/network failures cannot be provisionally approved. No referral is awarded; later official confirmation records audit only, while later terminal failure is logged for human review without automatic clawback.
- Data-only migration `20260802000000_RequeueUniquePayUserFeePayerFailures` requeues uncredited rows rejected before the live `feePayer=user` buyer alias/payable contract was supported; the worker still requires a fresh fully matching paid response before settlement.
- Data-only migration `20260803180000_RequeueUniquePayVerifiedUnsettled` schedules provider-paid but uncredited rows for a fresh authenticated inquiry after the stale-context settlement fix. `UniquePaySettlementService` owns a factory-created users.db context per attempt so reconciliation's newly persisted `paid` state cannot be hidden by the legacy singleton change tracker; no migration directly credits a wallet.
- UniquePay `feePayer` is controlled by the business-level `gatewayFee`/`feePayer` settings in the provider panel; the documented create-invoice form has no fee-payer field. Keep verification support for `user`/`buyer` and `owner` so existing invoices remain settleable; the provider currently reports `user` when the customer bears the configured 12% fee.
- Tenant UniquePay availability is `global UniquePay enabled && TenantUniquePayEnabled`; the tenant owner panel shows `سراسری خاموش` when the global switch is off and refuses local enabling until global configuration is ready.
- Super-admin `⚙️ مدیریت درگاه‌ها` displays all four live gateway states, root key names, and configuration readiness without exposing secrets. Enabling a gateway with missing token/URL is rejected. Target-state callbacks carry a revision and short expiry, and are restricted to configured super-admin ids.
- New HooshPay invoices require the live global `hooshPayEnabled` switch and, for tenant storefronts, the
  per-tenant `TenantHooshPayEnabled` preference. Disabling either switch hides and blocks only new invoices, including
  stale Telegram callbacks; existing rows remain eligible for status checks, IPN processing, and settlement. A missing
  global key is disabled, while the tracked operational configuration explicitly keeps the gateway enabled.
- NOWPayments creation uses the same live global snapshot and, for tenant storefronts, `TenantNowPaymentsEnabled`;
  IPN validation and settlement of existing crypto invoices continue when new creation is disabled.
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
- Tenant fulfillment must reload the order and treat an existing `TenantBotLedgerEntry` for the same `TenantBotOrderId` as already fulfilled; this protects against stale singleton EF tracking and duplicate "check status" clicks.
- If XUI account creation times out after a tenant card-to-card receipt is approved, keep the order unfulfilled but retryable and leave Sales Assistant approval controls available. Do not mark timeout as a definitive failed payment.
- If Sales Assistant cannot relay a tenant card-to-card receipt photo, it must send a text-only fallback with the same approve/reject/detail callbacks so the owner can still confirm the receipt.
- When a tenant order later fulfills successfully after an earlier timeout/failure, clear stale `TenantBotOrder.ErrorMessage` and linked receipt errors before saving so successful order details and audit logs do not keep showing old timeout text.
- Super-admin `Verify payment` accepts tenant storefront `OrderId` values. It retries the same tenant fulfillment path and resends stored account details for fulfilled orders instead of creating another account.

## Gozargah Site Sync

- Site sync is optional and controlled by `GozargahSite*` config flags.
- Successful create/update/delete/link-change operations enqueue or send sync events through the outbox in `users.db`.
- Website records for tenant purchases belong to the tenant owner while preserving buyer Telegram id for audit.
- Pending sync events may need to re-read fresh XUI panel data before a super-admin retry.
- `get_user` HTTP 404 from the Gozargah website means the Telegram user has no website account; wallet-button checks treat it as expected and must not spam the Telegram logger channel.
- Owned-bot profile/status messages should display Gozargah `get_user` 404/not-found as `متصل نشده`, not as the raw HTTP/API error.
- A successful non-banned `get_user` lookup means the owned-bot buyer should be promoted to `CredUser.IsColleague=true` before tariffs, purchases, or renewals are priced.
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
  30 minutes. The full clients list already supplies `updatedAt`, so never add per-client polling.
- `XuiV3VolumeReminderStates` is unique by credential-free `PanelKey + ClientId`. A cycle resets for counter drop,
  quota increase, client recreation, newer bot renewal metadata, or successful owned/admin/tenant renewal hook;
  `updatedAt` alone never resets it. Only the highest crossed threshold is sent, stale ambiguous claims are suppressed
  to prevent duplicates, and a successful renewal must never be rolled back when reminder-state persistence fails.
- Metered XUI pricing is centralized in `XuiV3PurchaseService.ResolvePurchase`: finite durations charge
  `trafficGb * rolePricePerGb + days * rolePricePerDay`, while zero-day lifetime durations charge
  `trafficGb * rolePricePerGb * lifetimePriceMultiplier` and round upward to whole toman. Missing daily prices,
  lifetime multipliers, and duration `isEnabled` values default to zero, one, and true for catalog compatibility.
  Disabled durations must be omitted and rejected across owned purchase/renewal, super-admin creation, and tenant
  purchase/renewal; the separate fixed-price `kind=unlimited` plans do not use these metered fields. The resolver also
  returns the authoritative metered component breakdown, and owned-bot final purchase/renewal previews must format
  that stored breakdown rather than recalculate rates or subtotals in the Telegram presentation layer.
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
- XUI v3 API calls use bounded retry/backoff for transient TLS/socket/timeouts and HTTP `408/429/502/503/504/520-527`; retry settings live beside `xuiV3RequestTimeoutSeconds` in `Data/configuration.json`.
- XUI v3 account creation treats generated email as the idempotency key. If `addClient` or the follow-up client/link read fails ambiguously, the bot re-reads the panel by email and returns the recovered panel UUID/subId when the account exists instead of creating a duplicate.
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
- Telegram `409 getUpdates` conflict means another process/receiver is polling the same token. `MultiBotHostedService` stops only the affected receiver and logs a critical message; operators still need to remove the duplicate deployment, old service, screen/tmux process, or webhook/polling conflict that owns the token.
- XUI/HTTP `TaskCanceledException`, `TimeoutException`, and `HttpClient.Timeout` during update handling are treated as external operation timeouts. The active bot logs `handle_update_external_timeout` and sends a best-effort retry notice instead of turning the panel delay into a Telegram polling failure.
- `Domain/Logging/TelegramLogger.cs` truncates plain-text application logs before sending them to Telegram so large exception stacks do not trigger `message is too long` and create secondary logger noise.
- `Domain/Logging/DailyErrorFileLoggerProvider.cs` writes warning/error/critical diagnostics with full exception chains
  and active bot context to the configurable daily `Data/Logs/errors-{shamsiDate}.log` file. It masks common token,
  authorization, cookie, API-key, and secret representations and is fail-soft if disk logging is unavailable.
- In owned customer routing, XUI v3 free-trial messages must be handled before purchase text flow. The trial start clears any half-built purchase session so metered purchases cannot reach summary without `TrafficGb`.
- Bot token duplication between owned and tenant bots must be rejected or disabled at runtime.
- `MultiBotHostedService` serializes start/stop/cleanup per `BotId`; never register a receiver outside that lifecycle
  gate or overwrite its CTS. A transient bounded `GetMe` probe starts one optimistic receiver and completes identity/
  command setup in the background with `initializing/degraded` status. Invalid and duplicate tokens remain fail-closed.
- `telegramBotStartupProbeTimeoutSeconds` controls the short Telegram startup/panel probe (default 12 seconds).
  `SetMyCommands` is background initialization and must not stop an already registered receiver.
- Super-admins can use `🤖 وضعیت ربات‌ها` to see process-local receiver health for every owned, assistant, and tenant bot. The report comes from `BotRuntimeStatusStore`; it never exposes tokens and does not call Telegram.
- Telegram polling 5xx bursts such as `502 Bad Gateway` and delivery timeouts such as `Request timed out` are transient Telegram-side noise. They are swallowed before operational Telegram logging and should not be sent repeatedly to the private logger channel.
- `Domain/Logging/TelegramLogger.cs` also applies message-level channel suppression for known noncritical noise: stale Sales Assistant callbacks, unchanged Telegram edits, receipt-photo relay warnings that have a text fallback, repeated tenant forced-join probes, and Telegram polling 5xx/429/timeouts. Payment/audit logs and real token/XUI/settlement failures should still reach the private channel.
- `CredentialsDbContext` and legacy `UserDbContext` state helper methods are still singleton-backed in DI and currently use a `SemaphoreSlim` gate as a temporary concurrency guard. The long-term fix is a separate refactor to per-operation DbContext/factory usage.
- New wallet-ledger, referral, and scheduled usage-report operations use `UserDbContextFactory` per operation; legacy
  conversation/payment code still uses the singleton contexts and their compatibility gates.
