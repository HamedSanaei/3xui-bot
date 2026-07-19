using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

namespace Adminbot.Domain
{
    /// <summary>
    /// Writes and reads append-only wallet ledger entries stored in <c>users.db</c>.
    /// </summary>
    /// <remarks>
    /// The service does not change wallet balances in <c>credentials.db</c>. Callers must perform the
    /// balance mutation first, then record the before/after values here so every credit or debit can be audited.
    /// </remarks>
    public class WalletLedgerService
    {
        private const string GozargahSiteWalletProvider = "gozargah_site_wallet";
        /// <summary>Creates independent users.db contexts so concurrent financial writers do not share EF tracking state.</summary>
        private readonly UserDbContextFactory _userDbContextFactory;

        /// <summary>
        /// Creates a wallet ledger service backed by the runtime users database.
        /// </summary>
        /// <param name="userDbContextFactory">
        /// Per-operation EF Core context factory for <c>users.db</c>. A fresh context prevents concurrent Telegram
        /// receivers from sharing one non-thread-safe change tracker.
        /// </param>
        public WalletLedgerService(UserDbContextFactory userDbContextFactory)
        {
            _userDbContextFactory = userDbContextFactory;
        }

        /// <summary>
        /// Appends one wallet ledger row for a completed credit or debit operation.
        /// </summary>
        /// <param name="telegramUserId">Telegram user id of the wallet owner whose balance changed.</param>
        /// <param name="direction">Ledger direction, normally <see cref="WalletLedgerDirections.Credit" /> or <see cref="WalletLedgerDirections.Debit" />.</param>
        /// <param name="amountToman">Positive transaction amount in Iranian toman; zero or negative values are ignored.</param>
        /// <param name="beforeBalance">Wallet balance in toman before the caller changed the credentials balance.</param>
        /// <param name="afterBalance">Wallet balance in toman after the caller changed the credentials balance.</param>
        /// <param name="reason">Business reason key for reports, such as wallet charge, account purchase, or tenant profit.</param>
        /// <param name="provider">Optional payment provider or source key, such as hooshpay, nowpayments, admin, or tenant_card.</param>
        /// <param name="referenceType">Optional local entity type that caused the ledger row.</param>
        /// <param name="referenceId">Optional local entity id or external provider id used for audit lookup.</param>
        /// <param name="orderId">Optional order id shown in payment/order reports.</param>
        /// <param name="description">Optional human-readable description safe for admin review.</param>
        /// <param name="ownerTelegramUserId">Optional tenant owner Telegram id when the wallet entry belongs to a tenant sale.</param>
        /// <param name="counterpartyTelegramUserId">Optional Telegram id of the other party, such as a tenant customer.</param>
        /// <param name="botId">Bot id that originated the financial event; falls back to the current bot context.</param>
        /// <param name="botUsername">Bot username that originated the financial event; falls back to the current bot context.</param>
        /// <param name="botType">Bot type that originated the financial event; falls back to the current bot context.</param>
        /// <param name="idempotencyKey">
        /// Optional stable financial mutation key. When supplied, repeated calls return the existing ledger row and
        /// do not append a duplicate. The value must not contain secrets.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the users.db insert.</param>
        /// <returns>The persisted ledger entry, or null when <paramref name="amountToman" /> is not positive.</returns>
        /// <remarks>
        /// Financial settlement code should always supply <paramref name="idempotencyKey"/>. The users.db unique
        /// index is the final duplicate protection, while legacy call sites that omit the key retain append-only
        /// behavior until they are migrated. When an older row has the same provider/reference identity but no key,
        /// the service backfills the key on that row instead of appending a duplicate audit entry.
        /// </remarks>
        public async Task<WalletLedgerEntry> RecordAsync(
            long telegramUserId,
            string direction,
            long amountToman,
            long beforeBalance,
            long afterBalance,
            string reason,
            string provider = null,
            string referenceType = null,
            string referenceId = null,
            string orderId = null,
            string description = null,
            long? ownerTelegramUserId = null,
            long? counterpartyTelegramUserId = null,
            string botId = null,
            string botUsername = null,
            string botType = null,
            string idempotencyKey = null,
            CancellationToken cancellationToken = default)
        {
            if (amountToman <= 0)
                return null;

            var normalizedIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : idempotencyKey.Trim();
            if (normalizedIdempotencyKey?.Length > 240)
                throw new ArgumentException("Wallet ledger idempotency key cannot exceed 240 characters.", nameof(idempotencyKey));

            await using var context = _userDbContextFactory.CreateDbContext();
            if (normalizedIdempotencyKey != null)
            {
                var existing = await context.WalletLedgerEntries
                    .FirstOrDefaultAsync(x => x.IdempotencyKey == normalizedIdempotencyKey, cancellationToken);
                if (existing != null)
                    return existing;

                if (!string.IsNullOrWhiteSpace(referenceType) && !string.IsNullOrWhiteSpace(referenceId))
                {
                    var legacyEntry = await context.WalletLedgerEntries.FirstOrDefaultAsync(
                        x => x.TelegramUserId == telegramUserId &&
                             x.Reason == reason &&
                             x.Provider == provider &&
                             x.ReferenceType == referenceType &&
                             x.ReferenceId == referenceId,
                        cancellationToken);
                    if (legacyEntry != null)
                    {
                        // Backfill the new key on a pre-referral ledger row instead of creating a duplicate audit row.
                        legacyEntry.IdempotencyKey ??= normalizedIdempotencyKey;
                        await context.SaveChangesAsync(cancellationToken);
                        return legacyEntry;
                    }
                }
            }

            var entry = new WalletLedgerEntry
            {
                BotId = string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.CurrentBotId : botId,
                BotUsername = string.IsNullOrWhiteSpace(botUsername) ? BotContextAccessor.CurrentBotUsername : botUsername,
                BotType = string.IsNullOrWhiteSpace(botType) ? BotContextAccessor.CurrentBotType : botType,
                OwnerTelegramUserId = ownerTelegramUserId,
                TelegramUserId = telegramUserId,
                CounterpartyTelegramUserId = counterpartyTelegramUserId,
                Direction = direction,
                AmountToman = amountToman,
                BalanceBefore = beforeBalance,
                BalanceAfter = afterBalance,
                Reason = reason,
                Provider = provider,
                ReferenceType = referenceType,
                ReferenceId = referenceId,
                OrderId = orderId,
                Description = description,
                IdempotencyKey = normalizedIdempotencyKey,
                CreatedAtUtc = DateTime.UtcNow
            };

            context.WalletLedgerEntries.Add(entry);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return entry;
            }
            catch (DbUpdateException) when (normalizedIdempotencyKey != null)
            {
                // A concurrent settlement may have inserted the same unique key after our initial lookup.
                context.ChangeTracker.Clear();
                var concurrent = await context.WalletLedgerEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.IdempotencyKey == normalizedIdempotencyKey, cancellationToken);
                if (concurrent != null)
                    return concurrent;
                throw;
            }
        }

        /// <summary>
        /// Reads a newest-first page of wallet ledger rows for one Telegram user.
        /// </summary>
        /// <param name="telegramUserId">Telegram user id whose wallet history should be displayed.</param>
        /// <param name="page">Zero-based page index; negative values are treated as zero.</param>
        /// <param name="pageSize">Requested page size; clamped to a safe range of 1 through 20 rows.</param>
        /// <param name="cancellationToken">Cancellation token for the users.db query.</param>
        /// <returns>A page of bot-wallet ledger entries and the total count available for pagination.</returns>
        /// <remarks>
        /// Direct owned-bot website-wallet payments are deliberately excluded because their balance source is the
        /// Gozargah website, not the bot wallet in <c>credentials.db</c>. Tenant card-to-card base-cost rows are
        /// included even when the provider is <c>gozargah_site_wallet</c>, because they audit the colleague owner's
        /// storefront settlement and should be visible in that owner's transaction history.
        /// </remarks>
        public async Task<(List<WalletLedgerEntry> Items, int TotalCount)> GetPageAsync(
            long telegramUserId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            page = Math.Max(0, page);
            pageSize = Math.Clamp(pageSize, 1, 20);
            await using var context = _userDbContextFactory.CreateDbContext();
            var query = context.WalletLedgerEntries
                .Where(x => x.TelegramUserId == telegramUserId &&
                            (x.Provider == null ||
                             x.Provider != GozargahSiteWalletProvider ||
                             x.ReferenceType == "tenant-order" ||
                             x.ReferenceType == "tenant-renew-order"))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id);

            var total = await query.CountAsync(cancellationToken);
            var items = await query.Skip(page * pageSize).Take(pageSize).ToListAsync(cancellationToken);
            return (items, total);
        }

        /// <summary>
        /// Loads a single wallet ledger row owned by a Telegram user.
        /// </summary>
        /// <param name="telegramUserId">Telegram user id used as the ownership guard for the lookup.</param>
        /// <param name="id">Internal users.db ledger entry id selected from an inline callback.</param>
        /// <param name="cancellationToken">Cancellation token for the users.db query.</param>
        /// <returns>
        /// The matching bot-wallet ledger entry, or null when the id does not belong to the user or represents a
        /// direct website-wallet debit that should not be shown in the bot-wallet history. Tenant base-cost
        /// website-wallet rows are visible because they belong to colleague storefront settlement audit.
        /// </returns>
        public async Task<WalletLedgerEntry> GetByIdAsync(long telegramUserId, int id, CancellationToken cancellationToken = default)
        {
            await using var context = _userDbContextFactory.CreateDbContext();
            return await context.WalletLedgerEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == id &&
                         x.TelegramUserId == telegramUserId &&
                         (x.Provider == null ||
                          x.Provider != GozargahSiteWalletProvider ||
                          x.ReferenceType == "tenant-order" ||
                          x.ReferenceType == "tenant-renew-order"),
                    cancellationToken);
        }
    }
}
