# Tenant wallet reporting

Central gateway top-ups retain their existing customer and owner credit keys and financial behavior. The shared
mirror service creates one receipt-backed report for HooshPay, Tetraminator, UniquePay, AtlasPay and NOWPayments.
Both parties' before/after balances come from committed credentials.db receipts, never a fresh balance read.
When the identities match, one credit receipt supplies both sections. Personal card top-ups are excluded.

The existing users.db notification table stores the owner report with a unique provider/payment/owner key prefixed
`tenant-owner-topup-report:`. This prefix is an explicit notification-kind discriminator; previous keys remain customer
notices. No schema migration is needed. The owner chat is captured at enqueue and cannot follow a later store transfer.
Delivery uses only the configured receipt-confirmation Sales Assistant, with HTML parsing. Customer notices still
require the original tenant bot identity. Missing assistant configuration retries within the existing budget, then
requires review; Telegram failures cannot re-enter financial settlement. Existing uncertain-delivery review applies.

Duplicate callbacks and receipt reconciliation recover missing reports without repeating credits or creating another
owner notification. The central logger is invoked only on the first report insertion. There remains a narrow crash
window between insertion and central logger enqueue: the owner notification survives, but the channel copy may be
missing. There is no automatic historical backfill or claim of cross-database atomicity.

The selected-store owner panel includes «💰 موجودی مشتری». Numeric input is stored in the existing bot/user owner
flow with OwnerStoreId, rechecking ownership and recorded BotUserStates membership before a read-only global wallet
lookup. Switching stores invalidates the previous input. The customer wallet screen retains charge/history/back
buttons and shows only its title, numeric user id and balance.

Verification for this change is code/diff and UTF-8 review plus one production Release build. Tests are neither added
nor executed. Production project exclusions keep existing test sources and dependencies out of publishing; no publish
or deployment is performed. Use a coordinated backup of credentials.db and users.db before the normal release procedure;
apply any previously pending migrations before receivers start. This change itself adds no migration.

Release verification: `dotnet build Adminbot.csproj -c Release --no-restore` succeeded with zero warnings and zero
errors. UTF-8 decoding and changed-line marker review found no newly introduced corruption. Two pre-existing
question-mark-only Persian fallback messages in TenantBotService.cs remain unchanged and require separate source
recovery by the maintainer. No tests, publish, commit, push or deployment were performed.

Changed files: the five gateway implementations in Domain (AtlasPay, HooshPay, NowPayments, Tetraminator, UniquePay),
Domain/PaymentSettlementNotification.cs, Services/PaymentSettlementNotificationWorker.cs,
Services/TenantWalletOwnerTopUpMirrorService.cs, Services/TenantBotService.cs,
Services/TenantBotService.CustomerWallet.cs, new Services/TenantBotService.CustomerBalance.cs, CODE_MAP.md and this guide.
