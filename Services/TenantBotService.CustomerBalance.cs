using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;

/// <summary>Provides storefront-scoped, read-only owner access to recorded customer balances.</summary>
public partial class TenantBotService
{
    /// <summary>Shows a recorded storefront customer's current global wallet balance to that store's owner.</summary>
    /// <param name="client">Owned-panel Telegram client, already decorated with the selected storefront heading.</param>
    /// <param name="message">Authenticated owner's text containing a positive numeric Telegram customer id.</param>
    /// <param name="ownerId">Global Telegram owner identity resolved from the sender, never from entered text.</param>
    /// <param name="token">Cancellation for fresh authorization checks and Telegram delivery.</param>
    /// <returns>A task completing after a read-only balance response or a non-disclosing rejection.</returns>
    /// <remarks>Rechecks both selected store ownership and BotId/TelegramUserId membership. The global wallet is
    /// read only after authorization. No profile, membership, credit or debit is created. Existing owner conversation
    /// state retains OwnerStoreId; changing stores cancels this input through the existing callback routing.</remarks>
    private async Task ShowStoreCustomerBalanceAsync(ITelegramBotClient client, Message message, long ownerId,
        CancellationToken token)
    {
        var storeId = _selectedOwnerStore?.Id;
        var validId = long.TryParse(message.Text?.Trim().PersianNumbersToEnglish(), out var customerId) && customerId > 0;
        var allowed = validId && !string.IsNullOrEmpty(storeId) && await _workflow.ReadAsync(db =>
            db.BotInstances.AnyAsync(store => store.Id == storeId && store.Type == BotInstanceTypes.Tenant &&
                store.OwnerTelegramUserId == ownerId && db.BotUserStates.Any(state =>
                    state.BotId == storeId && state.TelegramUserId == customerId), token));
        var customer = allowed ? await _credentialsDbContext.GetUserStatusWithId(customerId) : null;
        await client.SendMessage(message.Chat.Id, customer == null
            ? "آیدی معتبر مشتری همین فروشگاه را ارسال کنید."
            : $"🆔 آیدی عددی: {customerId}\n💳 موجودی: {customer.AccountBalance.FormatCurrency()}",
            cancellationToken: token);
    }
}
