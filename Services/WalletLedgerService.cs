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
        private readonly UserDbContext _userDbContext;

        /// <summary>
        /// Creates a wallet ledger service backed by the runtime users database.
        /// </summary>
        /// <param name="userDbContext">EF Core context for <c>users.db</c>, where ledger rows are persisted.</param>
        public WalletLedgerService(UserDbContext userDbContext)
        {
            _userDbContext = userDbContext;
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
        /// <param name="cancellationToken">Cancellation token for the users.db insert.</param>
        /// <returns>The persisted ledger entry, or null when <paramref name="amountToman" /> is not positive.</returns>
        /// <remarks>
        /// Idempotency is enforced by the caller, not this service. Payment settlement code must check its
        /// payment/order flags before calling this method so duplicate IPNs do not create duplicate ledger rows.
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
            CancellationToken cancellationToken = default)
        {
            if (amountToman <= 0)
                return null;

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
                CreatedAtUtc = DateTime.UtcNow
            };

            _userDbContext.WalletLedgerEntries.Add(entry);
            await _userDbContext.SaveChangesAsync(cancellationToken);
            return entry;
        }

        /// <summary>
        /// Reads a newest-first page of wallet ledger rows for one Telegram user.
        /// </summary>
        /// <param name="telegramUserId">Telegram user id whose wallet history should be displayed.</param>
        /// <param name="page">Zero-based page index; negative values are treated as zero.</param>
        /// <param name="pageSize">Requested page size; clamped to a safe range of 1 through 20 rows.</param>
        /// <param name="cancellationToken">Cancellation token for the users.db query.</param>
        /// <returns>A page of ledger entries and the total count available for pagination.</returns>
        public async Task<(List<WalletLedgerEntry> Items, int TotalCount)> GetPageAsync(
            long telegramUserId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            page = Math.Max(0, page);
            pageSize = Math.Clamp(pageSize, 1, 20);
            var query = _userDbContext.WalletLedgerEntries
                .Where(x => x.TelegramUserId == telegramUserId)
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
        /// <returns>The matching ledger entry, or null when the id does not belong to the user.</returns>
        public Task<WalletLedgerEntry> GetByIdAsync(long telegramUserId, int id, CancellationToken cancellationToken = default)
        {
            return _userDbContext.WalletLedgerEntries
                .FirstOrDefaultAsync(x => x.Id == id && x.TelegramUserId == telegramUserId, cancellationToken);
        }
    }
}
