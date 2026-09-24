# Tenant customer wallet

## Trust and balances

The only customer balance is `credentials.db.Users.AccountBalance`, keyed globally by Telegram user id.
Storefront approval grants a route to that balance, not ownership of it. Approved A and C can use the same
customer balance; unapproved B cannot expose or spend it. The colleague owner's wallet remains separate.

From an **owned bot**, a configured super administrator uses `/tenantwallet` or `/tenantwallet search` to list
up to 30 matching stores (brand, username, internal store id or owner id). Selecting a store shows the current
BotFather identity, owner identity, super-admin grant state, owner opt-in state and final wallet state. Grant and
revocation confirmations are bound to that owned-bot/admin conversation, a random nonce, the displayed store
revision and a five-minute expiry. Possession of a callback is not authority.

The super-admin action grants **eligibility only**. A fresh grant deliberately leaves
`TenantCustomerWalletOwnerEnabled=false`. The exact persisted storefront owner must then use the separate
`کیف پول مشتری` control in that storefront's owner panel. Attempting to enable it before a valid grant is rejected.
The owner control changes only the owner opt-in bit and can never create or modify approval metadata.

Final customer-wallet admission requires all three conditions at the same time: the storefront is enabled, the
identity-bound super-admin grant is still valid for the current BotFather id and owner id, and the current owner
has explicitly opted in. Reset, invalid-token cleanup, owner reassignment, bot identity replacement, or explicit
super-admin revocation clear the grant and owner opt-in. A routine token rotation that preserves the same numeric
BotFather identity may preserve the grant. Revocation blocks new UI, top-up, purchase and renewal admission but
never cancels committed payment evidence, paid invoice settlement, or durable debit recovery.

Only the final active state shows `💰 کیف پول` and `📒 تراکنش‌های من` to customers. History always queries the
authenticated sender's global ledger, not an id supplied by a callback. Store owners receive order amount/profit/source
only, never the customer's whole balance. Existing tenant owner suspension/debt checks still guard new interactions.

## Top-up and settlement

`WalletChargeApplicationService` shares amount policies, live gateway admission and invoice transport with the
owned flow. Tenant amounts are entered in toman; gateway selection also requires the store's switch. Supported
central providers: HooshPay, Tetraminator, UniquePay, AtlasPay and NOWPayments. Personal storefront cards are
never top-up sources. Platform gateway credentials remain central.

The bot/user confirmation state is consumed before provider creation. A `wallet_charge` payment row, including
tenant BotId, username, customer/chat and immutable `WalletOriginBotType`, commits **before** external I/O.
Creation is not automatically retried on an ambiguous response. AtlasPay retains its existing explicit creation
attempt state; other providers retain their existing pending payment/order lookup identity for inquiry or review.
No “paid” state is inferred from an invoice link, Telegram callback or unsigned return URL.

Customer status callbacks, including NOWPayments partial-payment confirmation, check saved bot/customer/purpose
before entering existing provider inquiry. Controllers continue routing by `PaymentPurpose`: wallet_charge goes
to wallet settlement; tenant_order goes to direct-order fulfillment. Existing official/provisional policies remain
unchanged. Partial NOWPayments credit uses the existing verified amount policy. Tenant-origin payments are
excluded from owned referrals both during settlement and startup replay.

Each provider's existing credit key remains `payment:{provider}:{localPaymentId}:credit`. The committed receipt
and balance are atomic in credentials.db; payment marker, ledger and notification are separate users.db commits
repaired after failure. Owned notification keys remain `owned-wallet:{provider}:{id}`; tenant keys are
`tenant-wallet:{provider}:{id}`. Delivery uses the saved tenant BotId and original payment chat, with amount and
receipt balance. `PaymentSettlementNotificationWorker` remains delivery-only; uncertain Telegram delivery is
reviewed, not blindly resent. A disabled/unavailable runtime can delay delivery without reversing the credit.

## Wallet purchase and renewal

New financial work requires fresh approval, authenticated customer, current plan and authoritative tenant pricing.
Purchase confirmation uses `TN:PAYWALLET:Pay:...`; renewal uses `TN:RNWALLET:{localOrderId}`. Existing renewal
selection paths converge on `BuildTenantRenewPaymentProviderKeyboard`; wallet funding enters the existing tenant
renewal saga, never the owned wallet-renewal engine. An unpaid wallet retry revalidates live renewal identity and
price. A paid retry uses immutable receipt proof instead of repricing the completed debit.

1. Admit a normal `TenantBotOrder` in a short users.db transaction. A unique customer/store/confirmation key
   reuses the order on duplicate delivery. Purchase uses the confirmation message's bot/customer/chat/message
   identity; renewal uses its existing local order id. Telegram update ids are not financial keys.
2. `TryDebitWalletIfSufficientAsync` reads fresh credentials.db balance inside its writer transaction, then commits
   balance and receipt together. Amount must be positive. Insufficient balance creates neither mutation nor receipt;
   the UI shows balance/required amount and offers top-up only while approval remains current. Conflicting key
   parameters fail rather than silently reusing another transaction. Legacy overdraft-capable `Pay` is unchanged.
3. Debit key: `tenant-customer-wallet:{order.Id}:debit`. The exact negative sale amount, customer, store and key
   must match. The `wallet` provider label and order PaidAt alone are never proof.
4. Existing `FULFILLPAIDTENANTORDERASYNC` holds the order gate and invokes the existing creation coordinator or
   `tenant-renew-{order.OrderId}` renewal operation. No alternate addClient/UpdateClient engine exists.
5. Successful customer delta is `-SalePriceToman`; owner delta is `+ProfitToman` via existing `tenant:{id}:profit`.
   Owner base cost is **not** debited. Existing tenant ledger/order-notification intents remain authoritative and
   identify the source as `کیف پول مشتری`.

Funding markers are `admitted → paid`, or `paid → refund_pending → refunded`; null belongs to non-wallet history.
Markers describe progress, not authority to create another balance mutation. The recovery worker scans pages of
50 orders older than a minute every 30 seconds, advances past unresolved entries, opens independent scopes and
never initiates a new debit. Receipt reconciliation repairs missing ledger/paid metadata after either database commit.

## Compensation and uncertainty

Definitive non-application permits the exact customer refund using `tenant-customer-wallet:{id}:refund`.
Authorization commits as `refund_pending` before the credentials credit. Refund is terminal for provisioning.
Any reserved, started, ambiguous, applied or manual-review XUI operation prevents automatic compensation.
Creation requires the existing coordinator's definitive rejection; renewal requires its existing failed/non-applied
classification. Generic timeouts and unknown panel errors are not proof of rejection.

Receipt reconciliation writes explicit `account_refund` ledger credit and atomically finalizes the marker with
`tenant-wallet-refund:{id}` notification intent. Replays cannot credit twice. A crash after the refund credit repairs
metadata/delivery only. A crash after XUI application resumes existing read-back and owner settlement, without
another customer debit or provisioning request. Original debit and refund ledger entries both remain auditable.

Manual review remains necessary for ambiguous XUI outcomes, missing/conflicting receipt targets, uncertain invoice
creation without a provider identity, and uncertain Telegram delivery. Resolve through existing provider/XUI recovery
procedures; do not delete markers, force a second create or refund based on timeout alone.

## Schema and implementation map

Migration `20260923083845_TenantCustomerWallet` changes **users.db only**:

- BotInstances: super-admin grant flag (false default), UTC approval time, administrator id, approved bot id and owner id.
- TenantBotOrders: nullable admission key with a unique index; nullable funding state.
- Five central payment tables: immutable WalletOriginBotType with historical `owned` default.
- No historical credits, balances, ids or receipt keys change. No credentials snapshot/schema change.
- Down refuses to discard tenant-origin payment history or any admitted wallet order. Use a compatible forward fix.
- Migration `20260924023225_TenantCustomerWalletOwnerActivation` adds
  `BotInstances.TenantCustomerWalletOwnerEnabled` with a constant false default. It changes no customer balance,
  receipt, order, provider payment or approval identity. Existing approved storefronts therefore remain fail-closed
  until their exact owner explicitly opts in after deployment.

New application files: `TenantCustomerWalletPolicy`, `TenantCustomerWalletFunding`, `TenantCustomerWalletRecoveryWorker`,
`WalletChargeApplicationService`, `TenantBotService.CustomerWallet` and `TelegramBotService.TenantWallet` in Services.
Integration changes: TenantBotService, TelegramBotService, CredentialsStore, WalletOperationReconciliationService,
ReferralService, BotRuntimeServices, Program, BotRuntime/payment models, notification factory/worker documentation,
UserDbContext, migration/designer/snapshot. Controllers and XUI mutation engines retain their existing routing.

Tests reside in the six `TenantCustomerWallet*Tests.cs` files; AtlasPay migration expectations also include the new
additive migration. Real temporary SQLite and fake HTTP/Telegram verify permissions/UI, five-provider creation,
duplicate settlement and referral exclusion, original-bot delivery, cross-store races, debit/refund commit gaps,
purchase/renewal economics and actual XUI saga recovery after success, rejection and ambiguity. No live provider
or production database is used. Verification results for the delivered revision are recorded in the final report.

## Manual deployment

Build/test and run both model-drift checks before publishing. Use the published non-serving `--migration-check`
against fresh files and **backup copies** of the intended production databases. Stop polling, webhook and background
writers for the final coordinated backup of both users.db and credentials.db; preserve their WAL state through
SQLite backup rather than copying active main files alone. Follow `backup-recovery.md` for the log outbox too.

Keep one polling process. Apply migrations before receivers/workers start (normal startup already orders this).
All existing stores remain unapproved. Verify a trusted pilot store through `/tenantwallet`, exercise a small top-up,
purchase and renewal, and monitor unreconciled receipts, customer-wallet recovery, XUI review, notification backlog
and SQLite contention. This implementation does not deploy or enable any production store.

Do not roll back binaries or restore one database independently after new wallet orders/tenant charges exist.
Retain financial evidence and use a compatible recovery release, especially while any outcome remains unresolved.

## Delivery verification

- Full Release suite: **840 passed, 0 failed** (3m54s), before four final additional regression cases.
- Requested category filter union (TenantCustomerWallet, MultiStore, Reliability, Concurrency, Tenant, Payment,
  Wallet, ManualReview, Admin): **629 passed, 0 failed** (4m01s). These overlap the full suite; do not add the counts.
- Dedicated wallet suite initially **32/32 passed**; four later cases passed separately: migration downgrade guard,
  concurrent real PAYWALLET callbacks, and applied-but-unacknowledged XUI creation/renewal recovery. The wallet
  suite now contains **36 cases**. The final owner-payment-source notification assertion passed with the callback case.
- Final `dotnet build Adminbot.csproj -c Release --no-restore`: **0 warnings, 0 errors**.
- Both `dotnet ef migrations has-pending-model-changes --context ... --configuration Release --no-build` checks: clean.
- `dotnet publish -c Release -f net10.0 -r linux-x64 --self-contained false`: successful. Published output contains
  no test assembly, xUnit, testhost or TestPlatform files.
- Non-serving preflight using the local Release assembly: both contexts **OK** on fresh temporary databases.
  Upgrade/default preservation is covered by the real SQLite migration tests. Production backup-copy preflight and
  execution on the Linux server remain deployment actions; they were not performed here.
- Test-project compilation reports existing nullable/analyzer warnings in unrelated fixtures; production build is clean.
- Diff/word-diff and strict UTF-8 checks cover all changed files. No new mojibake markers. Two pre-existing literal
  question-mark messages in TenantBotService remain unchanged and need a separate source-backed text restoration.
- Work remains uncommitted on `main`; no push, live payment, live XUI operation or deployment occurred.

## Changed files

- `Adminbot.Tests/AtlasPayTests.cs`
- `Adminbot.Tests/TenantCustomerWalletAccessTests.cs`
- `Adminbot.Tests/TenantCustomerWalletFulfillmentTests.cs`
- `Adminbot.Tests/TenantCustomerWalletMigrationTests.cs`
- `Adminbot.Tests/TenantCustomerWalletSettlementTests.cs`
- `Adminbot.Tests/TenantCustomerWalletTests.cs`
- `Adminbot.Tests/TenantCustomerWalletTopUpTests.cs`
- `CODE_MAP.md`
- `Data/UserDbContext.cs`
- `Domain/AtlasPay.cs`
- `Domain/BotRuntime.cs`
- `Domain/HooshPay.cs`
- `Domain/NowPayments.cs`
- `Domain/PaymentSettlementNotification.cs`
- `Domain/Tetraminator.cs`
- `Domain/UniquePay.cs`
- `Migrations/20260923083845_TenantCustomerWallet.Designer.cs`
- `Migrations/20260923083845_TenantCustomerWallet.cs`
- `Migrations/UserDbContextModelSnapshot.cs`
- `Program.cs`
- `Services/BotRuntimeServices.cs`
- `Services/CredentialsStore.cs`
- `Services/PaymentSettlementNotificationWorker.cs`
- `Services/ReferralService.cs`
- `Services/TelegramBotService.TenantWallet.cs`
- `Services/TelegramBotService.cs`
- `Services/TenantBotService.CustomerWallet.cs`
- `Services/TenantBotService.cs`
- `Services/TenantCustomerWalletFunding.cs`
- `Services/TenantCustomerWalletPolicy.cs`
- `Services/TenantCustomerWalletRecoveryWorker.cs`
- `Services/TenantOrderNotificationWorker.cs`
- `Services/WalletChargeApplicationService.cs`
- `Services/WalletOperationReconciliationService.cs`
- `docs/tenant-customer-wallet.md`
