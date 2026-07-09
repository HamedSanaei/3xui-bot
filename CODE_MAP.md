# CODE_MAP.md

## Purpose

Adminbot is a multi-brand Telegram sales bot for XUI/3x-ui VPN accounts. It supports owned bots, colleague-owned tenant storefront bots, wallet payments, payment gateways, card-to-card tenant receipts, broadcast jobs, XUI v3 account management, and optional sync with `gozargah.network`.

## Entry Points

- `Program.cs`: ASP.NET host, DI registration, EF migration startup, controller mapping, bot runtime registration, hosted services.
- `Services/BotRuntimeServices.cs`: bot registry, bot context accessor, bot client provider, and multi-bot receiver startup.
- `Services/TelegramBotService.cs`: main dispatcher for owned bots and legacy/admin/customer flows.
- `Services/TenantBotService.cs`: tenant owner panel and tenant customer storefront flows.
- `Controllers/PaymentController.cs`: payment IPN endpoints and gateway callbacks.

## Data Stores

- `Data/UserDbContext.cs`: bot state, tenant bot settings, payment records, broadcast jobs, wallet ledger, tenant orders, Gozargah sync outbox.
- `Data/CredentialsDbContex.cs`: shared user wallet/profile data. Do not change this schema unless explicitly requested.
- `Data/configuration.json`: app-level settings and owned bot configs. Secrets live here locally and must not be copied into docs.
- `Data/xui-v3-service-plans.json`: XUI v3 service catalog, prices, inbounds, duration options, unlimited fair-usage plans, and metered `minimumTrafficGb`.

## Core Services

- `Services/XuiV3PurchaseService.cs`: resolves service selections, validates plan rules, builds XUI v3 account metadata, and creates accounts.
- `Services/XuiV3BotFlowService.cs`: shared customer account flows for owned and tenant bots: purchase, renewal, search, account list, link change, comment change, delete, and state callbacks.
- `Services/XuiV3RenewalPolicy.cs`: central renewal payload calculation for metered, national, and unlimited accounts.
- `Services/XuiV3ClientPlanEligibility.cs`: checks whether an XUI client belongs to active service inbounds.
- `Services/XuiV3AdminFlowService.cs`: super-admin XUI v3 management flows.
- `Services/BroadcastManager.cs`: queued broadcast engine with progress/status tracking and retry behavior.
- `Services/SalesAssistantService.cs`: central assistant bot for tenant sale notifications and manual receipt approval.
- `Services/WalletLedgerService.cs`: append-only wallet ledger for credits/debits.
- `Domain/GozargahSite.cs`: Gozargah site API client, sync event models, mapping, and retry helpers.

## Tenant Bot Rules

- Each tenant bot is a `BotInstance` with `Type = tenant` and id `tenant-{ownerTelegramUserId}`.
- Tenant runtime state is scoped by `BotId + TelegramUserId` in `BotUserStates`; never key tenant customer flow only by Telegram user id.
- Tenant customers reuse shared XUI account flows where possible, but tenant payments and fulfillment go through `TenantBotOrder`.
- Tenant account metadata stored in XUI comments must preserve `CreatedByBotId`, `TenantBotId`, buyer Telegram id, service key, service kind, inbounds, and last action.
- Normal and unlimited services may share the same public inbounds. Renewal/search must trust metadata first; if metadata is missing, negative expiry means unlimited, otherwise shared public inbounds should resolve to normal metered service.
- Tenant support contacts should be stored as canonical `@username`; `t.me` links are normalized before display so customers never see `@https://...`.
- Tenant operational logs, lifecycle notifications, and payment audit logs are delivered through the default owned bot to the central logger channel. Tenant storefront bots are not expected to be members of the private logger channel.
- Tenant owner-panel reset clears only owner-configured storefront settings and disables the tenant bot; it must preserve orders, receipts, payments, ledger entries, and customer state. Invalid-token cleanup on panel refresh clears only `Token`, `Username`, and `Enabled`, leaving card/support/tutorial settings intact.
- Tenant owner toggle uses bounded runtime startup retries for transient Telegram/network timeouts. If the receiver still cannot start, the tenant row is rolled back to `Enabled=false` and no central tenant failure notification is sent for the transient timeout.

## Payment and Ledger Rules

- NOWPayments and HooshPay payment records live in `users.db` and can be linked to tenant orders.
- Super-admin manual NOWPayments checks are provider re-checks only: local code must not set `finished` or credit balances unless NOWPayments returns a paid status (`finished`, `confirmed`, or `sending`).
- Tenant platform-gateway sales credit owner profit; tenant card-to-card fulfillment debits owner base cost and can allow negative owner balances if configured by business rules.
- Tenant card-to-card base cost settlement tries the owner's bot wallet first, then the owner's Gozargah website wallet when connected and sufficient, then allows the bot wallet to go negative with an owner warning. This does not auto-disable the customer account in the current phase.
- Tenant platform-gateway sales credit profit to the owner's bot wallet and include a live Gozargah website wallet snapshot in the private sale log; the site wallet is not mutated for gateway profit.
- Every wallet movement should have a matching `WalletLedgerEntry`.
- Admin manual wallet credits/debits and colleague role promotions/demotions must be mirrored to the private logger channel with clickable actor and target identities.
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

## Current Gotchas

- Persian/RTL Telegram text and emoji are production UI; edit surgically and verify diffs for mojibake.
- `credentials.db` is shared wallet/profile state and is intentionally kept stable.
- Financial `LogPayment` backup sends both `credentials.db` and `users.db` to the configured backup channel; backup failures must stay fail-soft and must not break settlement.
- XUI v3 panel responses may omit `Traffic`; helpers must use null-safe access and fallback to top-level fields or `Extra`.
- XUI v3 request timeout is controlled by `xuiV3RequestTimeoutSeconds` in `Data/configuration.json`; slow panels can otherwise time out during `/panel/api/clients/add`.
- XUI v3 API calls use bounded retry/backoff for transient TLS/socket/timeouts and HTTP `429/502/503/504`; retry settings live beside `xuiV3RequestTimeoutSeconds` in `Data/configuration.json`.
- XUI v3 account creation treats generated email as the idempotency key. If `addClient` or the follow-up client/link read fails ambiguously, the bot re-reads the panel by email and returns the recovered panel UUID/subId when the account exists instead of creating a duplicate.
- Operational payment/account logs are delivered through the default owned bot to the central logger channel; include origin bot metadata in the message for non-default owned bots and tenant storefronts.
- Telegram callbacks can be stale; callback answers must be best-effort and must not crash receivers.
- Telegram blocked-user, deactivated-user, chat-not-found, and Telegram send-timeout errors are per-user/transient delivery failures; update and polling error handlers must log and swallow them without stack traces so one customer or one slow Telegram reply cannot stop or pollute logs for owned, tenant, or assistant bot receivers.
- Telegram `409 getUpdates` conflict means another process/receiver is polling the same token. `MultiBotHostedService` stops only the affected receiver and logs a critical message; operators still need to remove the duplicate deployment, old service, screen/tmux process, or webhook/polling conflict that owns the token.
- XUI/HTTP `TaskCanceledException`, `TimeoutException`, and `HttpClient.Timeout` during update handling are treated as external operation timeouts. The active bot logs `handle_update_external_timeout` and sends a best-effort retry notice instead of turning the panel delay into a Telegram polling failure.
- `Domain/Logging/TelegramLogger.cs` truncates plain-text application logs before sending them to Telegram so large exception stacks do not trigger `message is too long` and create secondary logger noise.
- In owned customer routing, XUI v3 free-trial messages must be handled before purchase text flow. The trial start clears any half-built purchase session so metered purchases cannot reach summary without `TrafficGb`.
- Bot token duplication between owned and tenant bots must be rejected or disabled at runtime.
- `MultiBotHostedService` does a bounded background recovery pass after startup for enabled bots that missed their first Telegram receiver start because of transient `GetMe`/command setup failures. Disabled, duplicate-token, and invalid-token bots are non-retryable; do not replace this with an all-or-nothing startup.
- Super-admins can use `🤖 وضعیت ربات‌ها` to see process-local receiver health for every owned, assistant, and tenant bot. The report comes from `BotRuntimeStatusStore`; it never exposes tokens and does not call Telegram.
- Telegram polling 5xx bursts such as `502 Bad Gateway` and delivery timeouts such as `Request timed out` are transient Telegram-side noise. They are swallowed before operational Telegram logging and should not be sent repeatedly to the private logger channel.
- `Domain/Logging/TelegramLogger.cs` also applies message-level channel suppression for known noncritical noise: stale Sales Assistant callbacks, unchanged Telegram edits, receipt-photo relay warnings that have a text fallback, repeated tenant forced-join probes, and Telegram polling 5xx/429/timeouts. Payment/audit logs and real token/XUI/settlement failures should still reach the private channel.
- `CredentialsDbContext` and legacy `UserDbContext` state helper methods are still singleton-backed in DI and currently use a `SemaphoreSlim` gate as a temporary concurrency guard. The long-term fix is a separate refactor to per-operation DbContext/factory usage.
