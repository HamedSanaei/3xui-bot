using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

public partial class TenantBotService
{
    /// <summary>Pending customer text step for a tenant renewal code; no payment or capacity is claimed at this step.</summary>
    private const string TenantRenewDiscountEntryStep = "renew-discount-entry";

    /// <summary>Serializable, tenant- and selection-bound snapshot of a displayed renewal discount.</summary>
    /// <remarks>The state row is scoped by bot and Telegram sender; this snapshot is additionally checked against the current storefront, sender, selection and gross/base amounts before use. It is not proof of capacity or authority to charge.</remarks>
    private sealed class TenantRenewDiscountPreview
    {
        /// <summary>Internal storefront bot id, never the Telegram chat id.</summary>
        public string TenantBotId { get; set; }
        /// <summary>Telegram sender who entered this code in the storefront.</summary>
        public long CustomerTelegramUserId { get; set; }
        /// <summary>Internal users.db code id selected for this renewal.</summary>
        public int CodeId { get; set; }
        /// <summary>Exact code configuration revision seen during the preview.</summary>
        public DateTime CodeUpdatedAtUtc { get; set; }
        /// <summary>The selected plan encoded by the existing tenant checkout selection encoder.</summary>
        public string SelectionKey { get; set; }
        /// <summary>Undiscounted tenant sale amount in whole toman.</summary>
        public long DisplayedGrossToman { get; set; }
        /// <summary>Colleague cost in whole toman; persisted for validation but never displayed.</summary>
        public long DisplayedBaseCostToman { get; set; }
        /// <summary>Actual capped reduction in whole toman.</summary>
        public long DisplayedDiscountToman { get; set; }
        /// <summary>Final displayed payable amount in whole toman.</summary>
        public long DisplayedNetToman { get; set; }
    }

    /// <summary>Checks a saved renewal preview against this sender, storefront and current selection without using it as admission authority.</summary>
    /// <param name="user">Detached bot-scoped customer state read for the current Telegram sender.</param>
    /// <param name="tenant">Current tenant storefront row; its internal id must match the state snapshot.</param>
    /// <param name="selection">Current renewal plan rebuilt from the customer's saved state.</param>
    /// <param name="gross">Fresh undiscounted sale amount in whole toman.</param>
    /// <param name="baseCost">Fresh colleague cost in whole toman; not customer-visible.</param>
    /// <returns>A successful null when no code was selected; the exact displayed selection when all fields agree; otherwise an explicit ChangedQuote failure, never a full-price fallback.</returns>
    /// <remarks>The caller must recheck the live code revision, scope, capacity and price when admitting an order. A malformed or stale nonempty state is always a failure, not a missing selection.</remarks>
    /// <example><code>var saved = ReadTenantRenewDiscountSelection(user, tenant, selection, price.SalePriceToman, price.BaseCostToman);
    /// if (!saved.Success) { /* Ask the customer to re-enter the code. */ }</code></example>
    private static TenantDiscountResult<TenantDiscountSelection> ReadTenantRenewDiscountSelection(
        User user, BotInstance tenant, XuiV3PurchaseSelection selection, long gross, long baseCost)
    {
        if (user == null || tenant == null || selection == null || user.Id <= 0)
            return TenantDiscountResult<TenantDiscountSelection>.Fail(TenantDiscountFailure.ChangedQuote);
        if (string.IsNullOrEmpty(user.RenewalDiscountSelectionJson))
            return TenantDiscountResult<TenantDiscountSelection>.Ok(null);

        TenantRenewDiscountPreview preview;
        try
        {
            preview = JsonConvert.DeserializeObject<TenantRenewDiscountPreview>(user.RenewalDiscountSelectionJson);
        }
        catch (JsonException)
        {
            return TenantDiscountResult<TenantDiscountSelection>.Fail(TenantDiscountFailure.ChangedQuote);
        }

        if (preview == null ||
            !string.Equals(preview.TenantBotId, tenant.Id, StringComparison.Ordinal) ||
            preview.CustomerTelegramUserId <= 0 || preview.CustomerTelegramUserId != user.Id ||
            preview.CodeId <= 0 || preview.CodeUpdatedAtUtc == default ||
            !string.Equals(preview.SelectionKey, BUILDPAYACTION(selection), StringComparison.Ordinal) ||
            gross <= 0 || baseCost < 0 || gross <= baseCost ||
            preview.DisplayedGrossToman != gross || preview.DisplayedBaseCostToman != baseCost ||
            preview.DisplayedDiscountToman <= 0 || preview.DisplayedDiscountToman > gross - baseCost ||
            preview.DisplayedNetToman != gross - preview.DisplayedDiscountToman)
            return TenantDiscountResult<TenantDiscountSelection>.Fail(TenantDiscountFailure.ChangedQuote);

        return TenantDiscountResult<TenantDiscountSelection>.Ok(new TenantDiscountSelection(
            preview.CodeId, preview.CodeUpdatedAtUtc,
            new TenantDiscountPrice(gross, baseCost, preview.DisplayedDiscountToman, preview.DisplayedNetToman)));
    }

    /// <summary>Builds HTML-safe price lines for a discounted renewal summary without exposing colleague cost.</summary>
    /// <param name="selection">Validated displayed selection returned by <see cref="ReadTenantRenewDiscountSelection"/>.</param>
    /// <returns>Escaped HTML showing gross, actual reduction and final payable, or an empty string if there is no discount.</returns>
    /// <remarks>Use only after checking the read result for success; the price is a preview, not an invoice or capacity reservation.</remarks>
    /// <example><code>var lines = BuildTenantRenewDiscountPriceText(saved.Value);</code></example>
    private static string BuildTenantRenewDiscountPriceText(TenantDiscountSelection selection)
    {
        if (selection is not { } displayed) return string.Empty;
        return $"تعرفه قبل از تخفیف: <b>{Html(displayed.Displayed.GrossToman.FormatCurrency())}</b>\n" +
               $"تخفیف واقعی: <b>{Html(displayed.Displayed.DiscountAmountToman.FormatCurrency())}</b>\n" +
               $"مبلغ قابل پرداخت: <b>{Html(displayed.Displayed.NetToman.FormatCurrency())}</b>";
    }

    /// <summary>Turns a named discount validation failure into actionable customer-facing Persian text.</summary>
    /// <param name="failure">Business failure from the storefront-scoped discount service or snapshot read.</param>
    /// <returns>A concrete, HTML-safe reason suitable for an ordinary Telegram text message.</returns>
    /// <remarks>Never interpret a discount error as permission to create an undiscounted order.</remarks>
    /// <example><code>var text = TenantRenewDiscountFailureText(result.Failure);</code></example>
    private static string TenantRenewDiscountFailureText(TenantDiscountFailure failure) => failure switch
    {
        TenantDiscountFailure.InvalidCode or TenantDiscountFailure.NotFound => "کد تخفیف نامعتبر است یا در این فروشگاه وجود ندارد.",
        TenantDiscountFailure.Disabled => "این کد تخفیف غیرفعال یا حذف شده است.",
        TenantDiscountFailure.Scope => "این کد برای تمدید اکانت قابل استفاده نیست.",
        TenantDiscountFailure.MinimumOrder => "تعرفه تمدید قبل از تخفیف به حداقل مبلغ این کد نمی‌رسد.",
        TenantDiscountFailure.Exhausted => "ظرفیت استفاده از این کد تخفیف تکمیل شده است.",
        TenantDiscountFailure.NoAvailableMargin => "برای این تعرفه مبلغی که بتوان از آن تخفیف داد باقی نمانده است.",
        TenantDiscountFailure.ChangedQuote or TenantDiscountFailure.Conflict => "تعرفه یا اطلاعات کد تخفیف تغییر کرده است؛ کد را دوباره وارد کنید.",
        TenantDiscountFailure.InvalidDefinition => "تنظیمات این کد تخفیف معتبر نیست؛ کد دیگری وارد کنید.",
        TenantDiscountFailure.Unauthorized => "این کد برای فروشگاه فعلی قابل استفاده نیست.",
        _ => "امکان بررسی این کد تخفیف وجود ندارد؛ دوباره تلاش کنید."
    };

    /// <summary>Applies a typed code to the current tenant renewal preview without creating an order or reserving capacity.</summary>
    /// <param name="botClient">Telegram transport of the same tenant bot that owns this conversation.</param>
    /// <param name="message">Customer text sent in the code-entry step; the sender must match the state owner.</param>
    /// <param name="tenant">Current tenant storefront with the authoritative customer price.</param>
    /// <param name="customer">Authenticated credentials profile of the Telegram sender.</param>
    /// <param name="user">Detached state of this sender in this bot, including renewal account and plan selection.</param>
    /// <param name="token">Cancellation of users.db reads/writes and Telegram delivery.</param>
    /// <returns>A task completing after either a precise retry prompt or a newly sent discounted renewal summary.</returns>
    /// <remarks>Call for the renewal code-entry step after the existing cancellation check. Live target/service authorization runs again when the summary is sent; admission must repeat those checks. Invalid input clears any previous selected code, retains the entry step and account/plan/UUID state, and never silently confirms a gross-price order. Renewal cancellation clears this bot-scoped state.</remarks>
    private async Task HandleTenantRenewDiscountTextAsync(
        ITelegramBotClient botClient, Message message, BotInstance tenant, CredUser customer, User user,
        CancellationToken token)
    {
        if (message.From == null || tenant == null || customer == null || user == null ||
            message.From.Id != customer.TelegramUserId || user.Id != customer.TelegramUserId ||
            !string.Equals(BotContextAccessor.CurrentBotId, tenant.Id, StringComparison.Ordinal) ||
            user.Flow != TENANTRENEWFLOW || user.LastStep != TenantRenewDiscountEntryStep)
            return;

        var selection = BuildTenantRenewSelectionFromState(user);
        TenantPriceResult price;
        try
        {
            price = CalculateTenantPrice(tenant, selection);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            user.RenewalDiscountSelectionJson = string.Empty;
            await _state.SaveUserStatus(user);
            await botClient.SendMessage(message.Chat.Id,
                "تعرفه انتخاب‌شده دیگر معتبر نیست. از منوی تمدید، پلن را دوباره انتخاب کنید؛ فاکتوری ساخته نشد.",
                replyMarkup: BuildTenantRenewDiscountRetryKeyboard(), cancellationToken: token);
            return;
        }

        var result = await _serviceProvider.GetRequiredService<TenantDiscountService>().QuoteAsync(
            tenant.Id, message.Text ?? string.Empty, price.SalePriceToman, price.BaseCostToman,
            TenantDiscountScopes.Renew, token);
        if (!result.Success)
        {
            user.RenewalDiscountSelectionJson = string.Empty;
            await _state.SaveUserStatus(user);
            await botClient.SendMessage(message.Chat.Id,
                TenantRenewDiscountFailureText(result.Failure) + "\nکد دیگری وارد کنید یا برای خروج «❌ انصراف» را بزنید.",
                replyMarkup: BuildTenantRenewDiscountRetryKeyboard(), cancellationToken: token);
            return;
        }

        var (code, discountPrice) = result.Value;
        user.RenewalDiscountSelectionJson = JsonConvert.SerializeObject(new TenantRenewDiscountPreview
        {
            TenantBotId = tenant.Id,
            CustomerTelegramUserId = customer.TelegramUserId,
            CodeId = code.Id,
            CodeUpdatedAtUtc = code.UpdatedAtUtc,
            SelectionKey = BUILDPAYACTION(selection),
            DisplayedGrossToman = discountPrice.GrossToman,
            DisplayedBaseCostToman = discountPrice.BaseCostToman,
            DisplayedDiscountToman = discountPrice.DiscountAmountToman,
            DisplayedNetToman = discountPrice.NetToman
        });
        user.LastStep = TENANTRENEWSTEPCONFIRM;
        await _state.SaveUserStatus(user);
        await SendTenantRenewSummaryAsync(botClient, message.Chat.Id, tenant, customer, user, token);
    }

    /// <summary>Offers a retry or cancellation while keeping a failed code entry separate from confirmation.</summary>
    /// <returns>A reply keyboard containing only the existing renewal cancellation action.</returns>
    /// <remarks>The customer may type another code directly; cancellation goes through the renewal state machine and clears the saved selection.</remarks>
    /// <example><code>var keyboard = BuildTenantRenewDiscountRetryKeyboard();</code></example>
    private static ReplyKeyboardMarkup BuildTenantRenewDiscountRetryKeyboard() =>
        new(new[] { new[] { new KeyboardButton("❌ انصراف") } }) { ResizeKeyboard = true };
}
