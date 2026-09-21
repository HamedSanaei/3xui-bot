using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;

public partial class TelegramBotService
{
    /// <summary>Reuses existing central provider inquiry/settlement for a tenant-origin wallet charge.</summary>
    /// <param name="callback">Telegram customer callback; its sender must own the persisted payment in this bot.</param>
    /// <param name="token">Cancellation of provider inquiry, database commits and Telegram delivery.</param>
    /// <returns>True when a supported payment callback was consumed, including unauthorized references.</returns>
    /// <remarks>This path deliberately does not require current approval: an already-created paid invoice remains settleable.
    /// Routing requires wallet_charge and immutable tenant origin, never merely a tenant BotId.</remarks>
    public async Task<bool> TryHandleTenantWalletPaymentCheckAsync(CallbackQuery callback, CancellationToken token)
    {
        var data = callback.Data ?? "";
        var botId = BotContextAccessor.CurrentBotId;
        var actor = callback.From.Id;
        var split = data.IndexOf('_');
        var id = split >= 0 && int.TryParse(data[(split + 1)..], out var parsed) ? parsed : 0;
        bool authorized;
        if (data.StartsWith("hpchk_", StringComparison.Ordinal))
        {
            authorized = await _workflow.ReadAsync(db => db.HooshPayPaymentInfos.AnyAsync(x => x.Id == id && x.BotId == botId
                && x.TelegramUserId == actor && x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge && x.WalletOriginBotType == BotInstanceTypes.Tenant, token));
            if (authorized) await ProcessHooshPayPaymentCallback(callback, token);
        }
        else if (data.StartsWith("tmchk_", StringComparison.Ordinal))
        {
            authorized = await _workflow.ReadAsync(db => db.TetraminatorPaymentInfos.AnyAsync(x => x.Id == id && x.BotId == botId
                && x.TelegramUserId == actor && x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge && x.WalletOriginBotType == BotInstanceTypes.Tenant, token));
            if (authorized) await ProcessTetraminatorPaymentCallbackAsync(callback, token);
        }
        else if (data.StartsWith("upchk_", StringComparison.Ordinal))
        {
            authorized = await _workflow.ReadAsync(db => db.UniquePayPaymentInfos.AnyAsync(x => x.Id == id && x.BotId == botId
                && x.TelegramUserId == actor && x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge && x.WalletOriginBotType == BotInstanceTypes.Tenant, token));
            if (authorized) await ProcessUniquePayPaymentCallbackAsync(callback, token);
        }
        else if (data.StartsWith("apchk_", StringComparison.Ordinal))
        {
            authorized = await _workflow.ReadAsync(db => db.AtlasPayPaymentInfos.AnyAsync(x => x.Id == id && x.BotId == botId
                && x.TelegramUserId == actor && x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge && x.WalletOriginBotType == BotInstanceTypes.Tenant, token));
            if (authorized) await ProcessAtlasPayPaymentCallbackAsync(callback, token);
        }
        else if (data.StartsWith("settle_crypto_partial_", StringComparison.Ordinal))
        {
            var paymentId = int.TryParse(data["settle_crypto_partial_".Length..], out var value) ? value : 0;
            authorized = await _workflow.ReadAsync(db => db.SwapinoPaymentInfos.AnyAsync(x => x.Id == paymentId && x.BotId == botId
                && x.TelegramUserId == actor && x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge && x.WalletOriginBotType == BotInstanceTypes.Tenant, token));
            if (authorized) await ProcessCryptoPartialSettlementCallback(callback, token);
        }
        else if (data.StartsWith("check_crypto_payment_", StringComparison.Ordinal))
        {
            var orderId = data["check_crypto_payment_".Length..];
            authorized = await _workflow.ReadAsync(db => db.SwapinoPaymentInfos.AnyAsync(x => x.OrderId == orderId && x.BotId == botId
                && x.TelegramUserId == actor && x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge && x.WalletOriginBotType == BotInstanceTypes.Tenant, token));
            if (authorized) await ProcessCryptoPaymentCallback(callback, token);
        }
        else return false;
        if (!authorized) await SafeAnswerCallbackQueryAsync(ActiveBotClient, callback.Id, "پرداخت متعلق به این گفتگو نیست.", cancellationToken: token);
        return true;
    }
}
