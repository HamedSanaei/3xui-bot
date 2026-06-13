using Adminbot.Domain;
using Adminbot.Utils;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using Telegram.Bot.Types.ReplyMarkups;

public class XuiV3PurchaseService
{
    public const int MaxBulkAccountCount = 10;

    private readonly IConfiguration _configuration;
    private readonly AppConfig _appConfig;

    public XuiV3PurchaseService(IConfiguration configuration)
    {
        _configuration = configuration;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
    }

    public XuiV3ServicePlanCatalog LoadCatalog()
    {
        return XuiV3ServicePlanCatalog.Load(_appConfig.XuiV3ServicePlansPath);
    }

    public IReadOnlyList<XuiV3ServiceDefinition> GetEnabledServices()
    {
        return LoadCatalog().Services
            .Where(s => s.IsEnabled)
            .ToList();
    }

    public XuiV3ResolvedPurchase ResolvePurchase(XuiV3PurchaseSelection selection, bool isColleague)
    {
        if (selection == null)
            throw new ArgumentNullException(nameof(selection));

        var catalog = LoadCatalog();
        var service = catalog.Services.FirstOrDefault(s =>
            string.Equals(s.Key, selection.ServiceKey, StringComparison.OrdinalIgnoreCase) && s.IsEnabled);

        if (service == null)
            throw new InvalidOperationException($"Service plan '{selection.ServiceKey}' was not found or is disabled.");

        if (service.IsUnlimited)
        {
            var unlimitedPlan = service.UnlimitedPlans.FirstOrDefault(p =>
                p.IsEnabled &&
                string.Equals(p.Key, selection.UnlimitedPlanKey, StringComparison.OrdinalIgnoreCase));

            if (unlimitedPlan == null)
                throw new InvalidOperationException($"Unlimited plan '{selection.UnlimitedPlanKey}' was not found or is disabled.");

            return new XuiV3ResolvedPurchase
            {
                Service = service,
                UnlimitedPlan = unlimitedPlan,
                TrafficGb = unlimitedPlan.FairUsageGb,
                TrafficBytes = ApiService.ConvertGBToBytes(unlimitedPlan.FairUsageGb),
                DurationDays = unlimitedPlan.Days,
                LimitIp = unlimitedPlan.MaxUsers,
                PriceToman = unlimitedPlan.Price.GetForRole(isColleague),
                IsUnlimited = true
            };
        }

        if (selection.TrafficGb == null || selection.TrafficGb <= 0)
            throw new InvalidOperationException("TrafficGb is required for metered plans.");

        var duration = service.DurationOptions.FirstOrDefault(d =>
            string.Equals(d.Key, selection.DurationKey, StringComparison.OrdinalIgnoreCase));

        if (duration == null)
            throw new InvalidOperationException($"Duration '{selection.DurationKey}' is not configured for service '{service.Key}'.");

        return new XuiV3ResolvedPurchase
        {
            Service = service,
            Duration = duration,
            TrafficGb = selection.TrafficGb.Value,
            TrafficBytes = ApiService.ConvertGBToBytes(selection.TrafficGb.Value),
            DurationDays = duration.Days,
            LimitIp = 0,
            PriceToman = selection.TrafficGb.Value * service.GetPricePerGb(isColleague),
            IsUnlimited = false
        };
    }

    public InlineKeyboardMarkup BuildServiceKeyboard()
    {
        var rows = GetEnabledServices()
            .Select(service => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    service.DisplayName,
                    XuiV3PurchaseCallbacks.Service(service.Key))
            })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildTrafficKeyboard(string serviceKey)
    {
        var service = FindService(serviceKey);
        var rows = service.TrafficOptionsGb
            .Chunk(2)
            .Select(chunk => chunk
                .Select(gb => InlineKeyboardButton.WithCallbackData(
                    $"{gb} GB",
                    XuiV3PurchaseCallbacks.Traffic(service.Key, gb)))
                .ToArray())
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices()) })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildDurationKeyboard(string serviceKey, int trafficGb)
    {
        var service = FindService(serviceKey);
        var rows = service.DurationOptions
            .Select(duration => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    duration.DisplayName,
                    XuiV3PurchaseCallbacks.Duration(service.Key, trafficGb, duration.Key))
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.Service(service.Key)) })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildUnlimitedPlanKeyboard(string serviceKey, bool isColleague)
    {
        var service = FindService(serviceKey);
        var rows = service.UnlimitedPlans
            .Where(p => p.IsEnabled && p.Price.GetForRole(isColleague) > 0)
            .Select(plan => new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{plan.DisplayName} - {plan.Price.GetForRole(isColleague).FormatCurrency()}",
                    XuiV3PurchaseCallbacks.UnlimitedPlan(service.Key, plan.Key))
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices()) })
            .ToArray();

        return new InlineKeyboardMarkup(rows);
    }

    public InlineKeyboardMarkup BuildConfirmKeyboard(XuiV3PurchaseSelection selection)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("تایید نهایی", XuiV3PurchaseCallbacks.Confirm(selection)),
                InlineKeyboardButton.WithCallbackData("انصراف", XuiV3PurchaseCallbacks.Cancel())
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("بازگشت", XuiV3PurchaseCallbacks.BackToServices())
            }
        });
    }

    public string BuildSummaryText(XuiV3PurchaseSelection selection, bool isColleague)
    {
        var resolved = ResolvePurchase(selection, isColleague);
        var accountCount = NormalizeAccountCount(selection.AccountCount);
        var totalPrice = resolved.PriceToman * accountCount;
        var text = "سفارش جدید\n";
        text += $"نوع سرویس: {resolved.Service.DisplayName}\n";
        text += $"تعداد اکانت: {accountCount}\n";
        text += $"حجم هر اکانت: {FormatTrafficSize(resolved.TrafficBytes, resolved.TrafficGb)}\n";
        text += resolved.DurationDays <= 0
            ? "مدت: نامحدود\n"
            : $"مدت: {resolved.DurationDays} روز\n";

        if (resolved.IsUnlimited)
            text += $"تعداد کاربر مجاز: {resolved.LimitIp}\n";

        text += $"قیمت هر اکانت: {resolved.PriceToman.FormatCurrency()}\n";
        text += $"قیمت کل: {totalPrice.FormatCurrency()}\n";
        if (!string.IsNullOrWhiteSpace(selection.UserComment))
            text += $"کامنت: {selection.UserComment}\n";

        text += "\nبرای ساخت اکانت، تایید نهایی را بزنید.";
        return text;
    }

    public bool CanAfford(CredUser user, XuiV3PurchaseSelection selection)
    {
        var resolved = ResolvePurchase(selection, user.IsColleague);
        return user.AccountBalance >= resolved.PriceToman * NormalizeAccountCount(selection.AccountCount);
    }

    public async Task<XuiV3AccountCreationResult> CreateAccountAsync(
        CredUser user,
        ServerInfo serverInfo,
        XuiV3PurchaseSelection selection,
        string selectedCountry,
        CancellationToken cancellationToken = default,
        XuiV3AccountMetadataOptions metadataOptions = null)
    {
        metadataOptions ??= new XuiV3AccountMetadataOptions();
        metadataOptions.AccountCounter = await ResolveAccountCounterAsync(user, metadataOptions);
        var resolved = ResolvePurchase(selection, user.IsColleague);
        var inboundIds = ResolveInboundIds(resolved.Service);
        var trafficBytes = metadataOptions.TrafficBytes > 0 ? metadataOptions.TrafficBytes : resolved.TrafficBytes;
        var priceToman = metadataOptions.PriceTomanOverride ?? resolved.PriceToman;
        Console.WriteLine(
            $"[XUIv3] create account target panel url={serverInfo?.Url}, rootPath={serverInfo?.RootPath}, panelTag={selectedCountry}, service={resolved.Service.Key}, inboundIds=[{string.Join(",", inboundIds)}]");

        var accountDto = new AccountDto
        {
            TelegramUserId = user.TelegramUserId,
            SelectedCountry = selectedCountry,
            SelectedPeriod = resolved.DurationDays <= 0 ? "Unlimited" : $"{resolved.DurationDays} Days",
            TotoalGB = resolved.TrafficGb.ToString(),
            ServerInfo = serverInfo,
            AccType = resolved.Service.Key,
            IsColleague = user.IsColleague,
            AccountCounter = metadataOptions.AccountCounter
        };

        return await ApiServicev3.CreateUserAccountAsync(
            accountDto,
            _configuration,
            new XuiV3CreateAccountOptions
            {
                InboundIds = inboundIds,
                TrafficGb = resolved.TrafficGb,
                TrafficBytes = trafficBytes,
                DurationDays = resolved.DurationDays,
                LimitIp = resolved.LimitIp,
                Comment = BuildClientComment(user, resolved, inboundIds, serverInfo, metadataOptions, trafficBytes, priceToman),
                SaveUserStatus = metadataOptions.SaveUserStatus
            },
            cancellationToken);
    }

    public async Task<XuiV3BulkCreationResult> CreateBulkAccountsAsync(
        CredUser user,
        ServerInfo serverInfo,
        XuiV3PurchaseSelection selection,
        string selectedCountry,
        XuiV3BulkCreateOptions options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new XuiV3BulkCreateOptions();
        var resolved = ResolvePurchase(selection, user.IsColleague);
        var accountCount = NormalizeAccountCount(options.AccountCount > 0 ? options.AccountCount : selection.AccountCount);
        var bulkOrderId = string.IsNullOrWhiteSpace(options.BulkOrderId)
            ? $"x3-{user.TelegramUserId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : options.BulkOrderId;

        var result = new XuiV3BulkCreationResult
        {
            BulkOrderId = bulkOrderId,
            RequestedCount = accountCount,
            UnitPriceToman = options.PriceTomanOverride ?? resolved.PriceToman,
            TotalRequestedPriceToman = (options.PriceTomanOverride ?? resolved.PriceToman) * accountCount,
            ServiceKey = resolved.Service.Key,
            ServiceName = resolved.Service.DisplayName,
            TrafficGb = resolved.TrafficGb,
            TrafficBytes = options.TrafficBytes > 0 ? options.TrafficBytes : resolved.TrafficBytes,
            DurationDays = resolved.DurationDays
        };

        for (var i = 1; i <= accountCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var createOptions = new XuiV3AccountMetadataOptions
            {
                UserComment = options.UserComment,
                BulkOrderId = bulkOrderId,
                BulkIndex = i,
                BulkTotal = accountCount,
                IsTrial = options.IsTrial,
                TrialKey = options.TrialKey,
                TrafficBytes = options.TrafficBytes,
                PriceTomanOverride = options.PriceTomanOverride,
                CreatedByTelegramUserId = options.CreatedByTelegramUserId,
                LastUpdatedByTelegramUserId = options.LastUpdatedByTelegramUserId,
                LastAction = options.LastAction,
                AccountCounter = options.NextAccountCounter > 0 ? options.NextAccountCounter + i - 1 : 0,
                SaveUserStatus = options.SaveUserStatus
            };

            var created = await CreateAccountAsync(
                user,
                serverInfo,
                selection,
                selectedCountry,
                cancellationToken,
                createOptions);

            if (!created.Success)
            {
                result.Failures.Add(new XuiV3BulkCreationFailure
                {
                    Index = i,
                    Email = created.Email,
                    Message = created.Message
                });
                break;
            }

            result.CreatedAccounts.Add(created);

            if (i < accountCount && options.DelayBetweenCreatesMs > 0)
                await Task.Delay(options.DelayBetweenCreatesMs, cancellationToken);
        }

        result.SuccessfulCount = result.CreatedAccounts.Count;
        result.TotalSuccessfulPriceToman = result.UnitPriceToman * result.SuccessfulCount;
        return result;
    }

    public async Task<XuiV3AccountCreationResult> CreateTrialAccountAsync(
        CredUser user,
        ServerInfo serverInfo,
        string serviceKey,
        int displayTrafficGb,
        long trafficBytes,
        int durationDays,
        string trialKey,
        CancellationToken cancellationToken = default)
    {
        var service = FindService(serviceKey);
        var inboundIds = ResolveInboundIds(service);
        var resolved = new XuiV3ResolvedPurchase
        {
            Service = service,
            Duration = new XuiV3DurationOption
            {
                Key = $"trial-{durationDays}d",
                DisplayName = $"تست {durationDays} روزه",
                Days = durationDays
            },
            TrafficGb = displayTrafficGb,
            TrafficBytes = trafficBytes,
            DurationDays = durationDays,
            LimitIp = 0,
            PriceToman = 0,
            IsUnlimited = false
        };

        Console.WriteLine(
            $"[XUIv3] create trial account target panel url={serverInfo?.Url}, rootPath={serverInfo?.RootPath}, service={service.Key}, trialKey={trialKey}, trafficBytes={trafficBytes}, inboundIds=[{string.Join(",", inboundIds)}]");

        var accountDto = new AccountDto
        {
            TelegramUserId = user.TelegramUserId,
            SelectedCountry = serverInfo?.Url,
            SelectedPeriod = $"{durationDays} Days",
            TotoalGB = displayTrafficGb.ToString(),
            ServerInfo = serverInfo,
            AccType = service.Key,
            IsColleague = user.IsColleague
        };

        return await ApiServicev3.CreateUserAccountAsync(
            accountDto,
            _configuration,
            new XuiV3CreateAccountOptions
            {
                InboundIds = inboundIds,
                TrafficGb = displayTrafficGb,
                TrafficBytes = trafficBytes,
                DurationDays = durationDays,
                LimitIp = 0,
                Comment = BuildClientComment(
                    user,
                    resolved,
                    inboundIds,
                    serverInfo,
                    new XuiV3AccountMetadataOptions
                    {
                        IsTrial = true,
                        TrialKey = trialKey,
                        TrafficBytes = trafficBytes,
                        PriceTomanOverride = 0,
                        CreatedByTelegramUserId = user.TelegramUserId,
                        LastUpdatedByTelegramUserId = user.TelegramUserId,
                        LastAction = "trial-create",
                        SaveUserStatus = true
                    },
                    trafficBytes,
                    0),
                SaveUserStatus = true
            },
            cancellationToken);
    }

    private static async Task<int> ResolveAccountCounterAsync(CredUser user, XuiV3AccountMetadataOptions metadataOptions)
    {
        if (metadataOptions?.AccountCounter > 0)
            return metadataOptions.AccountCounter;

        if (metadataOptions == null ||
            metadataOptions.IsTrial ||
            !metadataOptions.SaveUserStatus ||
            user == null ||
            user.TelegramUserId <= 0)
        {
            return 0;
        }

        var userDbContext = new UserDbContext();
        var flowUser = await userDbContext.GetUserStatus(user.TelegramUserId);
        return flowUser.AccountCounter + 1;
    }

    public string BuildCreatedAccountText(XuiV3AccountCreationResult result)
    {
        if (result == null || !result.Success)
            return $"ساخت اکانت ناموفق بود.\n{result?.Message}";

        var text = "✅ اکانت شما با موفقیت ساخته شد.\n\n";
        text += $"👤 نام اکانت: <code>{System.Net.WebUtility.HtmlEncode(result.Email)}</code>\n";
        text += $"📦 حجم: <b>{System.Net.WebUtility.HtmlEncode(FormatTrafficSize(result.TrafficBytes, result.TrafficGb))}</b>\n";
        text += $"📅 تاریخ انقضا: <b>{System.Net.WebUtility.HtmlEncode(FormatExpiry(result.ExpiryTime))}</b>\n\n";

        if (!string.IsNullOrWhiteSpace(result.SubLink))
        {
            text += "🔗 سابلینک:\n";
            text += $"<code>{System.Net.WebUtility.HtmlEncode(result.SubLink)}</code>\n\n";
            text += "📌 برای اتصال سریع، QR Code همین پیام را اسکن کنید.";
        }
        else
        {
            text += "⚠️ سابلینک ساخته نشد. مقدار xuiV3SubLinkBaseUrl یا مسیر subscription پنل را بررسی کنید.";
        }

        return text;
    }

    private static string FormatExpiry(long expiryTime)
    {
        if (expiryTime <= 0)
            return "نامحدود";

        return DateTimeOffset
            .FromUnixTimeMilliseconds(expiryTime)
            .UtcDateTime
            .AddMinutes(210)
            .ConvertToHijriShamsi();
    }

    public static int NormalizeAccountCount(int accountCount)
    {
        if (accountCount <= 0)
            return 1;

        return Math.Min(accountCount, MaxBulkAccountCount);
    }

    public static string FormatTrafficSize(long trafficBytes, int fallbackTrafficGb = 0)
    {
        if (trafficBytes <= 0 && fallbackTrafficGb > 0)
            trafficBytes = ApiService.ConvertGBToBytes(fallbackTrafficGb);

        if (trafficBytes <= 0)
            return "نامشخص";

        const decimal gb = 1024m * 1024m * 1024m;
        const decimal mb = 1024m * 1024m;
        if (trafficBytes >= (long)gb)
            return $"{trafficBytes / gb:0.##} GB";

        return $"{trafficBytes / mb:0.##} MB";
    }

    private XuiV3ServiceDefinition FindService(string serviceKey)
    {
        var service = GetEnabledServices().FirstOrDefault(s =>
            string.Equals(s.Key, serviceKey, StringComparison.OrdinalIgnoreCase));

        if (service == null)
            throw new InvalidOperationException($"Service '{serviceKey}' was not found or is disabled.");

        return service;
    }

    private static List<int> ResolveInboundIds(XuiV3ServiceDefinition service)
    {
        return service?.InboundIds?.Distinct().ToList() ?? new List<int>();
    }

    private static string BuildClientComment(
        CredUser user,
        XuiV3ResolvedPurchase resolved,
        List<int> inboundIds,
        ServerInfo serverInfo,
        XuiV3AccountMetadataOptions metadataOptions,
        long trafficBytes,
        long priceToman)
    {
        var metadata = new XuiV3ClientMetadata
        {
            TelegramUserId = user.TelegramUserId,
            UserRole = user.IsColleague ? "colleague" : "customer",
            ServiceKey = resolved.Service.Key,
            ServiceName = resolved.Service.DisplayName,
            ServiceKind = resolved.Service.Kind,
            PlanKey = resolved.IsUnlimited ? resolved.UnlimitedPlan?.Key : resolved.Duration?.Key,
            PlanName = resolved.IsUnlimited ? resolved.UnlimitedPlan?.DisplayName : resolved.Duration?.DisplayName,
            TrafficGb = resolved.TrafficGb,
            TrafficBytes = trafficBytes,
            DurationDays = resolved.DurationDays,
            LimitIp = resolved.LimitIp,
            PriceToman = priceToman,
            UserComment = metadataOptions.UserComment,
            BulkOrderId = metadataOptions.BulkOrderId,
            BulkIndex = metadataOptions.BulkIndex,
            BulkTotal = metadataOptions.BulkTotal,
            IsTrial = metadataOptions.IsTrial,
            TrialKey = metadataOptions.TrialKey,
            AccountCounter = metadataOptions.AccountCounter,
            InboundIds = inboundIds ?? new List<int>(),
            MultiInbound = resolved.Service.MultiInbound,
            PanelUrl = serverInfo?.Url,
            CreatedByTelegramUserId = metadataOptions.CreatedByTelegramUserId ?? user.TelegramUserId,
            LastUpdatedByTelegramUserId = metadataOptions.LastUpdatedByTelegramUserId ?? user.TelegramUserId,
            LastAction = string.IsNullOrWhiteSpace(metadataOptions.LastAction) ? "customer-create" : metadataOptions.LastAction
        };

        return JsonConvert.SerializeObject(metadata, Formatting.None);
    }

    private static string FormatInboundIds(IEnumerable<int> inboundIds)
    {
        return inboundIds == null ? "[]" : $"[{string.Join(",", inboundIds)}]";
    }
}

public class XuiV3AccountMetadataOptions
{
    public string UserComment { get; set; }
    public string BulkOrderId { get; set; }
    public int? BulkIndex { get; set; }
    public int? BulkTotal { get; set; }
    public bool IsTrial { get; set; }
    public string TrialKey { get; set; }
    public long TrafficBytes { get; set; }
    public long? PriceTomanOverride { get; set; }
    public long? CreatedByTelegramUserId { get; set; }
    public long? LastUpdatedByTelegramUserId { get; set; }
    public string LastAction { get; set; }
    public int AccountCounter { get; set; }
    public bool SaveUserStatus { get; set; } = true;
}

public class XuiV3BulkCreateOptions
{
    public int AccountCount { get; set; } = 1;
    public string UserComment { get; set; }
    public string BulkOrderId { get; set; }
    public bool IsTrial { get; set; }
    public string TrialKey { get; set; }
    public long TrafficBytes { get; set; }
    public long? PriceTomanOverride { get; set; }
    public long? CreatedByTelegramUserId { get; set; }
    public long? LastUpdatedByTelegramUserId { get; set; }
    public string LastAction { get; set; }
    public int NextAccountCounter { get; set; }
    public bool SaveUserStatus { get; set; } = true;
    public int DelayBetweenCreatesMs { get; set; } = 350;
}

public class XuiV3BulkCreationResult
{
    public string BulkOrderId { get; set; }
    public int RequestedCount { get; set; }
    public int SuccessfulCount { get; set; }
    public long UnitPriceToman { get; set; }
    public long TotalRequestedPriceToman { get; set; }
    public long TotalSuccessfulPriceToman { get; set; }
    public string ServiceKey { get; set; }
    public string ServiceName { get; set; }
    public int TrafficGb { get; set; }
    public long TrafficBytes { get; set; }
    public int DurationDays { get; set; }
    public List<XuiV3AccountCreationResult> CreatedAccounts { get; set; } = new List<XuiV3AccountCreationResult>();
    public List<XuiV3BulkCreationFailure> Failures { get; set; } = new List<XuiV3BulkCreationFailure>();
    public bool Success => SuccessfulCount == RequestedCount && Failures.Count == 0;
}

public class XuiV3BulkCreationFailure
{
    public int Index { get; set; }
    public string Email { get; set; }
    public string Message { get; set; }
}

public static class XuiV3PurchaseCallbacks
{
    private const string Prefix = "x3";

    public static string BackToServices()
    {
        return $"{Prefix}:back";
    }

    public static string Cancel()
    {
        return $"{Prefix}:cancel";
    }

    public static string Home()
    {
        return $"{Prefix}:home";
    }

    public static string Service(string serviceKey)
    {
        return $"{Prefix}:svc:{serviceKey}";
    }

    public static string Traffic(string serviceKey, int trafficGb)
    {
        return $"{Prefix}:gb:{serviceKey}:{trafficGb}";
    }

    public static string Duration(string serviceKey, int trafficGb, string durationKey)
    {
        return $"{Prefix}:dur:{serviceKey}:{trafficGb}:{durationKey}";
    }

    public static string UnlimitedPlan(string serviceKey, string planKey)
    {
        return $"{Prefix}:upl:{serviceKey}:{planKey}";
    }

    public static string Confirm(XuiV3PurchaseSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.UnlimitedPlanKey))
            return $"{Prefix}:ok:{selection.ServiceKey}:u:{selection.UnlimitedPlanKey}";

        return $"{Prefix}:ok:{selection.ServiceKey}:{selection.TrafficGb}:{selection.DurationKey}";
    }

    public static string AccountCount(int count)
    {
        return $"{Prefix}:cnt:{Math.Max(1, Math.Min(count, XuiV3PurchaseService.MaxBulkAccountCount))}";
    }

    public static string AccountState(int clientId, bool enable)
    {
        return $"{Prefix}:acct:{(enable ? "en" : "dis")}:{clientId}";
    }

    public static string AccountList(int page)
    {
        return $"{Prefix}:alist:{Math.Max(0, page)}";
    }

    public static string AccountSearchStart()
    {
        return $"{Prefix}:asrch";
    }

    public static string AccountSearchList(int page)
    {
        return $"{Prefix}:asl:{Math.Max(0, page)}";
    }

    public static string AccountView(int clientId, int page)
    {
        return $"{Prefix}:aview:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchView(int clientId, int page)
    {
        return $"{Prefix}:asv:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountRenew(int clientId, int page)
    {
        return $"{Prefix}:aren:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchRenew(int clientId, int page)
    {
        return $"{Prefix}:asren:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountUuidRenew(int clientId)
    {
        return $"{Prefix}:auren:{clientId}";
    }

    public static string AccountDeleteAsk(int clientId, int page)
    {
        return $"{Prefix}:adelask:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchDeleteAsk(int clientId, int page)
    {
        return $"{Prefix}:asdelask:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountDeleteConfirm(int clientId, int page)
    {
        return $"{Prefix}:adel:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchDeleteConfirm(int clientId, int page)
    {
        return $"{Prefix}:asdel:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchState(int clientId, bool enable, int page)
    {
        return $"{Prefix}:asacct:{(enable ? "en" : "dis")}:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountChangeLink(int clientId, int page)
    {
        return $"{Prefix}:ach:{clientId}:{Math.Max(0, page)}";
    }

    public static string AccountSearchChangeLink(int clientId, int page)
    {
        return $"{Prefix}:asch:{clientId}:{Math.Max(0, page)}";
    }

    public static bool TryParse(string callbackData, out XuiV3PurchaseCallback callback)
    {
        callback = null;

        if (string.IsNullOrWhiteSpace(callbackData))
            return false;

        var parts = callbackData.Split(':');
        if (parts.Length < 2 || parts[0] != Prefix)
            return false;

        callback = new XuiV3PurchaseCallback
        {
            Action = parts[1],
            ServiceKey = parts.Length > 2 ? parts[2] : null
        };

        if (callback.Action == "acct" && parts.Length >= 4)
        {
            callback.AccountOperation = parts[2];
            if (int.TryParse(parts[3], out var clientId))
                callback.ClientId = clientId;
        }

        if (callback.Action == "alist" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var page))
                callback.Page = page;
        }

        if (callback.Action == "asl" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var page))
                callback.Page = page;
        }

        if ((callback.Action == "aview" ||
             callback.Action == "aren" ||
             callback.Action == "adelask" ||
             callback.Action == "adel" ||
             callback.Action == "ach") &&
            parts.Length >= 4)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
            if (int.TryParse(parts[3], out var page))
                callback.Page = page;
        }

        if ((callback.Action == "asv" ||
             callback.Action == "asren" ||
             callback.Action == "asdelask" ||
             callback.Action == "asdel" ||
             callback.Action == "asch") &&
            parts.Length >= 4)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
            if (int.TryParse(parts[3], out var page))
                callback.Page = page;
        }

        if (callback.Action == "auren" && parts.Length >= 3)
        {
            if (int.TryParse(parts[2], out var clientId))
                callback.ClientId = clientId;
        }

        if (callback.Action == "asacct" && parts.Length >= 5)
        {
            callback.AccountOperation = parts[2];
            if (int.TryParse(parts[3], out var clientId))
                callback.ClientId = clientId;
            if (int.TryParse(parts[4], out var page))
                callback.Page = page;
        }

        if (callback.Action == "gb" && parts.Length >= 4 && int.TryParse(parts[3], out var trafficGb))
            callback.TrafficGb = trafficGb;

        if (callback.Action == "cnt" && parts.Length >= 3 && int.TryParse(parts[2], out var accountCount))
            callback.AccountCount = accountCount;

        if (callback.Action == "dur" && parts.Length >= 5)
        {
            if (int.TryParse(parts[3], out var durationTrafficGb))
                callback.TrafficGb = durationTrafficGb;
            callback.DurationKey = parts[4];
        }

        if (callback.Action == "upl" && parts.Length >= 4)
            callback.UnlimitedPlanKey = parts[3];

        if (callback.Action == "ok" && parts.Length >= 5)
        {
            if (parts[3] == "u")
            {
                callback.UnlimitedPlanKey = parts[4];
            }
            else
            {
                if (int.TryParse(parts[3], out var confirmTrafficGb))
                    callback.TrafficGb = confirmTrafficGb;
                callback.DurationKey = parts[4];
            }
        }

        return true;
    }
}

public class XuiV3PurchaseCallback
{
    public string Action { get; set; }
    public string ServiceKey { get; set; }
    public string AccountOperation { get; set; }
    public int? ClientId { get; set; }
    public int? Page { get; set; }
    public int? TrafficGb { get; set; }
    public int? AccountCount { get; set; }
    public string DurationKey { get; set; }
    public string UnlimitedPlanKey { get; set; }

    public XuiV3PurchaseSelection ToSelection()
    {
        return new XuiV3PurchaseSelection
        {
            ServiceKey = ServiceKey,
            TrafficGb = TrafficGb,
            DurationKey = DurationKey,
            UnlimitedPlanKey = UnlimitedPlanKey,
            AccountCount = 1
        };
    }
}
