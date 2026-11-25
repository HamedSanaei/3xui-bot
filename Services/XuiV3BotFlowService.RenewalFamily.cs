using Adminbot.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>Resolves, presents and revalidates the service family of an exact owned-bot renewal target.</summary>
public partial class XuiV3BotFlowService
{
    /// <summary>Bot-scoped step waiting for continuation in a verified service family.</summary>
    private const string RenewStepSameService = "renew-same-service";

    /// <summary>Bot-scoped step waiting for an explicit compatible choice for ambiguous legacy evidence.</summary>
    private const string RenewStepServiceCategory = "renew-service-category";

    /// <summary>Reads identity-checked service evidence for an already authorized owned-bot target.</summary>
    /// <param name="client">Detached list or lookup client that the caller has authorized for this renewal.</param>
    /// <param name="token">Cancellation for the foreground panel read.</param>
    /// <returns>Resolved service, explicitly compatible legacy choices, or a failure without any renewal authorization.</returns>
    /// <remarks>Uses the shared storefront resolver. Detail failure may use structured list metadata but never infers a category from traffic or term.</remarks>
    /// <exception cref="OperationCanceledException">The caller stops the update.</exception>
    private async Task<XuiV3TenantRenewalServiceResolution> ReadOwnedRenewalFamilyAsync(XuiV3Client client, CancellationToken token)
    {
        var services = _purchaseService.GetEnabledServices();
        if (client == null || string.IsNullOrWhiteSpace(client.Email))
            return XuiV3TenantRenewalServiceResolver.Resolve(client, null, XuiV3TenantClientDetailAvailability.NotFound, services);

        XuiV3Client detail = null;
        var availability = XuiV3TenantClientDetailAvailability.Unavailable;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(ApiServicev3.ResolveForegroundReadOverallBudget(_appConfig));
        try
        {
            var response = await ApiServicev3.GetClientAsync(BuildConfiguredPanelServerInfo(), _configuration,
                client.Email, budget.Token, suppressIdentifierBearingRetryLogs: true);
            if (response.Success && response.Obj != null)
            {
                detail = response.Obj;
                availability = XuiV3TenantClientDetailAvailability.Available;
            }
        }
        catch (XuiV3ApiException exception) when (exception.StatusCode == StatusCodes.Status404NotFound)
        {
            availability = XuiV3TenantClientDetailAvailability.NotFound;
        }
        catch (Exception exception) when (!token.IsCancellationRequested &&
            exception is XuiV3ApiException or HttpRequestException or OperationCanceledException)
        {
            // Retain the distinction between missing metadata and an unavailable panel detail endpoint.
        }

        var resolution = XuiV3TenantRenewalServiceResolver.Resolve(client, detail, availability, services);
        if (resolution.Success && resolution.AuthoritativeComment != null)
            client.Comment = resolution.AuthoritativeComment;
        return resolution;
    }

    /// <summary>Starts service selection after exact target lookup and any required third-party warning.</summary>
    /// <param name="botClient">Active owned Telegram bot transport.</param>
    /// <param name="chatId">Chat receiving renewal navigation and the reply keyboard.</param>
    /// <param name="payer">Authenticated customer whose role governs plan visibility.</param>
    /// <param name="client">Unique panel client already authorized for renewal.</param>
    /// <param name="canonicalUuid">Exact panel UUID, or empty for a determinable legacy owned account.</param>
    /// <param name="messageId">Existing text message to edit, or zero to send a new message.</param>
    /// <param name="token">Cancellation of panel, bot-scoped state and Telegram delivery.</param>
    /// <param name="navigation">Optional origin-specific navigation for account-list/search entry.</param>
    /// <remarks>Only bot/user state is written. No order, wallet, panel mutation or credentials delivery occurs.</remarks>
    private async Task StartOwnedRenewFamilyAsync(ITelegramBotClient botClient, ChatId chatId, CredUser payer,
        XuiV3Client client, string canonicalUuid, int messageId, CancellationToken token, InlineKeyboardMarkup navigation = null)
    {
        var resolution = await ReadOwnedRenewalFamilyAsync(client, token);
        await ShowOwnedRenewFamilyAsync(botClient, chatId, payer, client, canonicalUuid, resolution, messageId, token, navigation);
    }

    /// <summary>Replaces this owned bot's unpaid renewal selection with the current service evidence.</summary>
    /// <param name="botClient">Active owned Telegram transport.</param>
    /// <param name="chatId">Chat receiving the target and available choices.</param>
    /// <param name="payer">Authenticated payer whose independent bot-scoped state is replaced.</param>
    /// <param name="client">Renewal-authorized panel client for this evidence.</param>
    /// <param name="canonicalUuid">Exact UUID required for ambiguous selection.</param>
    /// <param name="resolution">Current result of identity-checked metadata and compatible-service resolution.</param>
    /// <param name="messageId">Origin message to edit, or zero to send fresh guidance.</param>
    /// <param name="token">Cancellation of state writes and Telegram delivery.</param>
    /// <param name="navigation">Optional return to the account or search list.</param>
    /// <remarks>The button never contains the target identity. Resets any obsolete price/plan preview when evidence changes.</remarks>
    private async Task ShowOwnedRenewFamilyAsync(ITelegramBotClient botClient, ChatId chatId, CredUser payer,
        XuiV3Client client, string canonicalUuid, XuiV3TenantRenewalServiceResolution resolution,
        int messageId, CancellationToken token, InlineKeyboardMarkup navigation = null)
    {
        if (resolution.Success && await RejectClosedServiceOperationAsync(botClient, chatId, resolution.Service,
                ServiceSalesOperation.Renewal, token))
        {
            await _state.ClearUserStatus(new User { Id = payer.TelegramUserId });
            return;
        }
        var candidates = AvailableOwnedRenewFamilies(resolution, payer.IsColleague);
        if (candidates.Length == 0 || (!resolution.Success && !TryNormalizeClientUuid(canonicalUuid, out _)))
        {
            var reason = resolution.Status == XuiV3TenantRenewalServiceResolutionStatus.DetailUnavailable
                ? "بررسی نوع سرویس موقتاً ممکن نیست. لطفاً کمی بعد دوباره تلاش کنید."
                : "نوع قابل تمدید این اکانت به‌طور مطمئن مشخص نشد. لطفاً با پشتیبانی تماس بگیرید.";
            await SendOwnedRenewTerminalAsync(botClient, chatId, new User { Id = payer.TelegramUserId }, reason, token);
            return;
        }

        await _state.ClearUserStatus(new User { Id = payer.TelegramUserId });
        await _state.SaveUserStatus(new User
        {
            Id = payer.TelegramUserId,
            Flow = RenewFlowName,
            LastStep = resolution.Success ? RenewStepSameService : RenewStepServiceCategory,
            ConfigLink = client.Email,
            SelectedCountry = resolution.Service?.Key ?? string.Empty,
            RenewTargetUuid = canonicalUuid,
            RenewalServiceResolutionMode = resolution.Success ? OwnedRenewalEvidenceMode(resolution) : string.Empty,
            PaymentMethod = "credit"
        });

        var current = resolution.Success ? XuiCustomerServiceText.Label(resolution.Service) : "نامشخص";
        await SendOrEditTextAsync(botClient, chatId, messageId,
            $"اکانت برای تمدید انتخاب شد: <code>{Html(client.Email)}</code>\nنوع فعلی سرویس: <b>{Html(current)}</b>",
            ParseMode.Html, navigation ?? BuildRenewHomeKeyboard(), token);
        await botClient.SendMessage(chatId, resolution.Success
                ? "برای ادامه، تمدید همین نوع سرویس را انتخاب کنید."
                : "نوع فعلی این اکانت قدیمی قطعی نیست. یکی از گزینه‌های سازگار را برای تمدید انتخاب کنید.",
            replyMarkup: resolution.Success ? XuiCustomerServiceText.SameServiceKeyboard() : BuildOwnedRenewFamilyChoices(candidates),
            cancellationToken: token);
    }

    /// <summary>Accepts the same-family action or an explicit legacy choice after reloading the exact scoped target.</summary>
    /// <param name="botClient">Active owned Telegram client.</param>
    /// <param name="message">Customer message with the reply keyboard action.</param>
    /// <param name="payer">Authenticated Telegram sender and buyer role.</param>
    /// <param name="user">Bot/user renewal state at the known or ambiguous family step.</param>
    /// <param name="token">Cancellation of panel, state and Telegram operations.</param>
    /// <remarks>Revalidates compatibility before storing the service key. A stale reply cannot authorize a different family or target.</remarks>
    private async Task HandleOwnedRenewFamilyStepAsync(ITelegramBotClient botClient, Message message, CredUser payer, User user, CancellationToken token)
    {
        var client = await GetAuthorizedRenewClientAsync(BuildConfiguredPanelServerInfo(), user, payer.TelegramUserId, token);
        var resolution = await ReadOwnedRenewalFamilyAsync(client, token);
        var candidates = AvailableOwnedRenewFamilies(resolution, payer.IsColleague);
        var same = user.LastStep == RenewStepSameService && resolution.Success && candidates.Length == 1 &&
            string.Equals(user.SelectedCountry, resolution.Service.Key, StringComparison.OrdinalIgnoreCase);
        var legacy = user.LastStep == RenewStepServiceCategory && resolution.RequiresCustomerSelection &&
            TryNormalizeClientUuid(user.RenewTargetUuid, out _);
        var selected = same && message.Text?.Trim() == XuiCustomerServiceText.RenewSame
            ? resolution.Service
            : legacy ? XuiCustomerServiceText.FindCategory(message.Text, candidates) : null;
        if (selected == null)
        {
            await ShowOwnedRenewFamilyAsync(botClient, message.Chat.Id, payer, client, user.RenewTargetUuid, resolution, 0, token);
            return;
        }

        user.SelectedCountry = selected.Key;
        user.RenewalServiceResolutionMode = resolution.Success ? OwnedRenewalEvidenceMode(resolution)
            : TenantRenewalServiceResolutionModes.CustomerSelectedLegacy;
        user.LastStep = selected.IsUnlimited ? RenewStepUnlimitedPlan : RenewStepTraffic;
        await _state.SaveUserStatus(user);
        await botClient.SendMessage(message.Chat.Id, $"{XuiCustomerServiceText.Label(selected)}\n" +
                (selected.IsUnlimited ? "بستهٔ تمدید را انتخاب کنید:" : "یکی از حجم‌های زیر را انتخاب کنید یا حجم دلخواه را به گیگابایت بفرستید:"),
            replyMarkup: selected.IsUnlimited ? BuildUnlimitedPlanReplyKeyboard(selected, payer.IsColleague) : BuildTrafficReplyKeyboard(selected),
            cancellationToken: token);
    }

    /// <summary>Checks whether the saved unpaid renewal choice is still authorized before preview or first mutation.</summary>
    /// <param name="botClient">Owned Telegram transport used to recover invalidated selections.</param>
    /// <param name="chatId">Customer chat receiving updated choices when needed.</param>
    /// <param name="payer">Authenticated buyer and current role.</param>
    /// <param name="user">Bot-scoped state containing the exact account and previously confirmed service.</param>
    /// <param name="client">Authorized detached panel target to be used for the preview or mutation.</param>
    /// <param name="token">Cancellation of panel and recovery work.</param>
    /// <returns>The same live catalog service on success, or null after displaying an updated selector or safe failure.</returns>
    /// <remarks>Call only for unpaid work. An admitted operation continues using its immutable recovery snapshot.</remarks>
    private async Task<XuiV3ServiceDefinition> RevalidateOwnedRenewFamilyAsync(ITelegramBotClient botClient, ChatId chatId,
        CredUser payer, User user, XuiV3Client client, CancellationToken token)
    {
        var resolution = await ReadOwnedRenewalFamilyAsync(client, token);
        var candidates = AvailableOwnedRenewFamilies(resolution, payer.IsColleague);
        var stillAuthorized = resolution.Success || (resolution.RequiresCustomerSelection &&
            user.RenewalServiceResolutionMode == TenantRenewalServiceResolutionModes.CustomerSelectedLegacy &&
            TryNormalizeClientUuid(user.RenewTargetUuid, out _));
        var service = stillAuthorized ? candidates.FirstOrDefault(x =>
            string.Equals(x.Key, user.SelectedCountry, StringComparison.OrdinalIgnoreCase)) : null;
        if (service == null)
            await ShowOwnedRenewFamilyAsync(botClient, chatId, payer, client, user.RenewTargetUuid, resolution, 0, token);
        return service;
    }

    /// <summary>Filters compatible services by the live owned renewal permissions and the buyer's package visibility.</summary>
    /// <param name="resolution">Current identity-checked catalog resolution.</param>
    /// <param name="isColleague">Current owned-buyer role used to filter ready packages.</param>
    /// <returns>Available known service or enabled compatible family candidates.</returns>
    private XuiV3ServiceDefinition[] AvailableOwnedRenewFamilies(XuiV3TenantRenewalServiceResolution resolution, bool isColleague) =>
        (resolution.Success ? new[] { resolution.Service } : resolution.CandidateServices)
            .Where(x => _purchaseService.SalesAvailability.IsEnabled(x, ServiceSalesOperation.Renewal) &&
                (!x.IsUnlimited || XuiV3PurchaseService.GetUnlimitedPlansForOwnedBot(x, isColleague).Any()))
            .ToArray();

    /// <summary>Builds the text-only candidate keyboard for an ambiguous owned renewal.</summary>
    /// <param name="services">Enabled choices compatible with the exact customer target.</param>
    /// <returns>One row per service plus a cancel row.</returns>
    private static ReplyKeyboardMarkup BuildOwnedRenewFamilyChoices(IEnumerable<XuiV3ServiceDefinition> services) =>
        new(services.Select(x => new[] { new KeyboardButton(XuiCustomerServiceText.CategoryOption(x)) })
            .Append(new[] { new KeyboardButton("انصراف") })) { ResizeKeyboard = true };

    /// <summary>Stores how a uniquely determined renewal family was proven.</summary>
    /// <param name="resolution">Successful service resolution for the current account.</param>
    /// <returns>The existing shared mode for structured metadata or deterministic legacy evidence.</returns>
    private static string OwnedRenewalEvidenceMode(XuiV3TenantRenewalServiceResolution resolution) =>
        resolution.Source is XuiV3TenantRenewalServiceResolutionSource.DetailMetadata or XuiV3TenantRenewalServiceResolutionSource.ListMetadata
            ? TenantRenewalServiceResolutionModes.Metadata : TenantRenewalServiceResolutionModes.LegacyDeterministic;
}
