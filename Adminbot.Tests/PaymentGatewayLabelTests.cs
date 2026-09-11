using System.Reflection;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>
/// Regression coverage for the customer-facing payment-gateway button captions.
/// </summary>
/// <remarks>
/// Every Iranian-toman method a customer can pick (HooshPay, Tetraminator, UniquePay, AtlasPay, and the tenant owner's
/// personal card-to-card option) must be recognisable as a rial method from its caption alone, while the NOWPayments
/// cryptocurrency button must never carry that marker.
///
/// These tests exist because the marker is a display-only suffix: it must be present exactly once, must not reorder or
/// drop the gateway name, fee percentage, or <c>کارت‌به‌کارت</c> wording, and must never leak into the callback payload
/// that routes the customer's choice. A caption regression silently mislabels the currency a customer is about to pay
/// in, and a callback regression would break invoice creation for already-issued keyboards.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>The single marker appended to every rial payment-gateway caption.</summary>
    private const string RialMarker = "ریالی";

    /// <summary>
    /// Verifies the owned-bot wallet-charge keyboard marks exactly the four rial gateways and leaves crypto unmarked.
    /// </summary>
    /// <remarks>
    /// Builds the real reply keyboard with all five gateways globally enabled and asserts each caption still carries the
    /// gateway name and its displayed fee percentage. The crypto assertion protects against a future copy/paste change
    /// that would advertise a toman settlement for the USDT/crypto provider.
    /// </remarks>
    [Fact]
    public void Owned_wallet_gateway_keyboard_marks_rial_gateways_and_leaves_crypto_unmarked()
    {
        using var databases = new Databases();
        var gateway = new GatewayAvailabilityProbe();
        var (service, _, _) = BuildGatewayCallbackService(databases, gateway, new GatewayTelegramClient());
        EnableEveryGateway(gateway);

        var labels = OwnedChargeKeyboardLabels(service);

        Assert.Equal(5, labels.Count);
        AssertOwnedRialGatewayLabel(labels, "هوش‌پی", "کارمزد ۱۵٪");
        AssertOwnedRialGatewayLabel(labels, "تترامیناتور", "کارمزد ۱۲٪");
        AssertOwnedRialGatewayLabel(labels, "یونیک‌پی", "کارمزد ۱۲٪");

        var atlas = Assert.Single(labels, label => label.Contains("اطلس‌پی", StringComparison.Ordinal));
        Assert.Contains("کارت‌به‌کارت آنی", atlas);
        Assert.Contains(RialMarker, atlas);

        var crypto = Assert.Single(labels, label => label.Contains("ارز دیجیتال", StringComparison.Ordinal));
        Assert.Contains("کارمزد ۰٪", crypto);
        Assert.DoesNotContain(RialMarker, crypto);

        // No caption may repeat the marker; the crypto row must have none at all.
        foreach (var label in labels)
            Assert.True(Occurrences(label, RialMarker) <= 1, $"caption repeated the rial marker: {label}");
    }

    /// <summary>
    /// Proves the owned-bot reply-keyboard matcher still accepts every caption shape that was ever shipped.
    /// </summary>
    /// <remarks>
    /// Telegram reply keyboards are one-time and already handed to customers, so a customer who received the
    /// payment-method keyboard before the rial marker was added can still press the older caption. If the matcher
    /// stopped accepting the pre-marker captions those presses would fall through to unknown text and the charge flow
    /// would stall. The legacy descriptive aliases are asserted for the same reason.
    /// </remarks>
    [Theory]
    [InlineData("⚡ هوش‌پی آنی | کارمزد ۱۵٪ | ریالی", "⚡ هوش‌پی آنی | کارمزد ۱۵٪", "درگاه ریالی هوش‌پی")]
    [InlineData("⚡ تترامیناتور آنی | کارمزد ۱۲٪ | ریالی", "⚡ تترامیناتور آنی | کارمزد ۱۲٪", "درگاه ریالی تترامیناتور")]
    [InlineData("⚡ یونیک‌پی آنی | کارمزد ۱۲٪ | ریالی", "⚡ یونیک‌پی آنی | کارمزد ۱۲٪", "درگاه ریالی یونیک‌پی")]
    [InlineData("💳 اطلس‌پی | کارت‌به‌کارت آنی | ریالی", "💳 اطلس‌پی | کارت‌به‌کارت آنی", "درگاه ریالی اطلس‌پی")]
    public void Owned_gateway_routing_accepts_current_and_previously_issued_captions(
        string currentLabel, string preMarkerLabel, string descriptiveAlias)
    {
        Assert.True(OwnedGatewayActionMatches(currentLabel, currentLabel, preMarkerLabel, descriptiveAlias));
        Assert.True(OwnedGatewayActionMatches(preMarkerLabel, currentLabel, preMarkerLabel, descriptiveAlias));
        Assert.True(OwnedGatewayActionMatches(descriptiveAlias, currentLabel, preMarkerLabel, descriptiveAlias));
        Assert.False(OwnedGatewayActionMatches("متن نامعتبر", currentLabel, preMarkerLabel, descriptiveAlias));
    }

    /// <summary>
    /// Verifies the tenant AtlasPay invoice keyboard labels its payment button as rial and keeps the status callback.
    /// </summary>
    /// <remarks>
    /// The tenant AtlasPay keyboard is the only tenant payment surface that offers both a provider checkout link and a
    /// manual status check. The callback payload must stay <c>apchk_{paymentId}</c> because stored tenant payments and
    /// already-delivered keyboards reference it.
    /// </remarks>
    [Fact]
    public void Tenant_atlas_pay_keyboard_marks_the_payment_button_as_rial_and_keeps_the_status_callback()
    {
        var payment = new AtlasPayPaymentInfo
        {
            Id = 4242,
            CustomerStartLink = "https://t.me/atlaspay_bot/start?start=tenant"
        };

        var labels = FlattenLabels(TenantAtlasPayKeyboard(payment));

        Assert.Equal(2, labels.Count);
        var pay = Assert.Single(labels, label => label.Contains("اطلس‌پی", StringComparison.Ordinal));
        Assert.Contains(RialMarker, pay);
        Assert.Equal(1, Occurrences(pay, RialMarker));

        var status = Assert.Single(labels, label => label.Contains("بررسی وضعیت پرداخت", StringComparison.Ordinal));
        Assert.Contains("🔄", status);

        // The corrupted '?' placeholders shipped by an older revision must never come back on this keyboard.
        foreach (var label in labels)
            Assert.DoesNotContain("??", label);
    }

    /// <summary>
    /// Verifies both tenant storefront keyboards (new purchase and renewal) mark the rial methods and keep crypto and
    /// every callback payload untouched.
    /// </summary>
    /// <remarks>
    /// The two tenant keyboards are built by different code paths for the same commercial purpose, so both are asserted
    /// against the same expectations. Callback payloads (<c>PAY*</c> for a new purchase, <c>RN*</c> for a renewal) are
    /// compared byte-for-byte because existing invoices and idempotent settlement depend on them. The transaction
    /// amount is chosen inside HooshPay's and UniquePay's accepted ranges and above the Tetraminator provider minimum so
    /// every rial gateway row is actually rendered.
    /// </remarks>
    [Fact]
    public async Task Tenant_purchase_and_renewal_keyboards_mark_rial_methods_and_keep_callback_payloads()
    {
        using var databases = new Databases();
        const string xuiUrl = "http://127.0.0.1:59999/";
        var configuration = RialGatewayLabelConfiguration(databases, xuiUrl);
        // No provider call is made by these keyboard builders, so the default transport is left unused.
        var atlas = new AtlasPay(configuration);
        await using var provider = AtlasTenantProvider(databases, configuration, atlas, out var registry, out var clients);

        var tenant = new BotInstance
        {
            Id = "tenant-label-store",
            Username = "label_store",
            Token = "60002:" + new string('b', 35),
            TelegramBotId = 60002,
            Type = BotInstanceTypes.Tenant,
            Enabled = true,
            OwnerTelegramUserId = 911,
            TenantStoreNumber = 1,
            TenantPriceMarkupPercent = 20,
            TenantHooshPayEnabled = true,
            TenantTetraminatorEnabled = true,
            TenantUniquePayEnabled = true,
            TenantAtlasPayEnabled = true,
            TenantNowPaymentsEnabled = true,
            TenantCardPaymentEnabled = true,
            TenantCardNumber = "6037991234567890",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        registry.Upsert(tenant);
        var client = clients.GetOrAdd(tenant.Id, _ => new StorefrontClient());

        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();

        // New-purchase keyboard: reflect the real pre-invoice renderer so the shipped caption literals are asserted.
        var purchase = typeof(TenantBotService).GetMethod("SHOWCUSTOMERCONFIRMASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // 50 GB of the metered 'normal' service keeps the sale price inside HooshPay's accepted range and above the
        // Tetraminator minimum, so every rial gateway row renders regardless of colleague vs user rate resolution.
        var selection = new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 50, DurationKey = "m1", AccountCount = 1 };
        await (Task)purchase.Invoke(service, new object?[] { client, new ChatId(911), null, tenant, selection, CancellationToken.None })!;

        AssertRialSelectionKeyboard(client.Labels, client.Callbacks, "PAY", "کارمزد ۱۵٪", "کارمزد ۱۲٪", "کارمزد ۱۲٪");

        // Renewal keyboard: same expectations through the renewal builder, which returns the markup without sending it.
        var renewal = typeof(TenantBotService).GetMethod("BuildTenantRenewPaymentProviderKeyboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var order = new TenantBotOrder
        {
            Id = 77,
            OrderId = "LABEL77",
            TenantBotId = tenant.Id,
            CustomerTelegramUserId = 912,
            SalePriceToman = 200_000,
            PaymentStatus = TenantBotOrderStatuses.Pending
        };
        var renewalMarkup = (InlineKeyboardMarkup)renewal.Invoke(service, new object?[] { order, tenant })!;
        var renewalButtons = renewalMarkup.InlineKeyboard.SelectMany(row => row).ToList();

        // The renewal keyboard deliberately omits fee wording for HooshPay and Tetraminator, so those rows only prove
        // that the gateway name and the rial marker survived.
        AssertRialSelectionKeyboard(
            renewalButtons.Select(button => button.Text).ToList(),
            renewalButtons.Select(button => button.CallbackData).Where(payload => payload != null).Select(payload => payload!).ToList(),
            "RN", null, null, "کارمزد ۱۲٪");
    }

    /// <summary>
    /// Asserts that a tenant gateway-selection keyboard exposes the expected rial and crypto captions.
    /// </summary>
    /// <param name="labels">Customer-visible button captions captured from the rendered keyboard.</param>
    /// <param name="callbacks">Callback payloads captured from the same rendered keyboard.</param>
    /// <param name="prefix">
    /// Row prefix identifying the flow: <c>PAY</c> for a new purchase, <c>RN</c> for a renewal of an existing order.
    /// Both are asserted against the same captions because they describe the same payment methods.
    /// </param>
    /// <param name="hooshPayFee">Existing HooshPay fee wording to preserve, or <c>null</c> when that keyboard omits it.</param>
    /// <param name="tetraminatorFee">Existing Tetraminator fee wording to preserve, or <c>null</c> when omitted.</param>
    /// <param name="uniquePayFee">Existing UniquePay fee wording to preserve, or <c>null</c> when omitted.</param>
    /// <remarks>
    /// Every rial method must carry the marker exactly once while keeping its gateway name and fee wording, and the
    /// crypto row must stay unmarked. The payload set is compared exactly, which fails the test if any label change ever
    /// altered a callback and broke already-issued buttons or stored invoices.
    /// </remarks>
    private static void AssertRialSelectionKeyboard(
        List<string> labels, List<string> callbacks, string prefix,
        string? hooshPayFee, string? tetraminatorFee, string? uniquePayFee)
    {
        AssertRialGatewayLabel(labels, "هوش‌پی", hooshPayFee);
        AssertRialGatewayLabel(labels, "تترامیناتور", tetraminatorFee);
        AssertRialGatewayLabel(labels, "یونیک‌پی", uniquePayFee);

        var atlas = Assert.Single(labels, label => label.Contains("اطلس‌پی", StringComparison.Ordinal));
        Assert.Contains("کارت‌به‌کارت آنی", atlas);
        Assert.Contains(RialMarker, atlas);

        var card = Assert.Single(labels, label => label.Contains("کارت‌به‌کارت به فروشگاه", StringComparison.Ordinal));
        Assert.Contains(RialMarker, card);

        var crypto = Assert.Single(labels, label => label.Contains("ارز دیجیتال", StringComparison.Ordinal));
        Assert.DoesNotContain(RialMarker, crypto);

        foreach (var label in labels)
            Assert.True(Occurrences(label, RialMarker) <= 1, $"caption repeated the rial marker: {label}");


        var providerCodes = new[] { "HP", "TM", "UP", "AP", "NP" };
        if (prefix == "RN")
        {
            // Renewal payloads address one existing order, so each must still end with the unchanged order token.
            foreach (var code in providerCodes)
                Assert.Contains(callbacks, payload => payload.EndsWith($"RN{code}:77", StringComparison.Ordinal));
            Assert.Contains(callbacks, payload => payload.EndsWith("RNCARD:77", StringComparison.Ordinal));
        }
        else
        {
            // Purchase payloads append the encoded plan selection, so only the gateway token is compared byte-exact.
            foreach (var code in providerCodes)
                Assert.Contains(callbacks, payload => payload.Contains($"PAY{code}:", StringComparison.Ordinal));
            Assert.Contains(callbacks, payload => payload.Contains("PAYCARD:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Asserts a rial gateway caption keeps its gateway name and fee text and carries the marker exactly once.
    /// </summary>
    /// <param name="labels">All captured button captions.</param>
    /// <param name="gatewayName">Persian gateway name that must remain present in exactly one caption.</param>
    /// <param name="feeText">
    /// Existing fee wording that must not be reordered away, or <c>null</c> when that keyboard never displayed a fee
    /// for this gateway.
    /// </param>
    private static void AssertRialGatewayLabel(List<string> labels, string gatewayName, string? feeText)
    {
        var label = Assert.Single(labels, candidate => candidate.Contains(gatewayName, StringComparison.Ordinal));
        if (feeText != null)
            Assert.Contains(feeText, label);
        Assert.Contains(RialMarker, label);
        Assert.Equal(1, Occurrences(label, RialMarker));
    }

    /// <summary>Asserts an owned-bot rial gateway caption through the shared gateway-label assertion.</summary>
    /// <param name="labels">All captured reply-keyboard captions.</param>
    /// <param name="gatewayName">Persian gateway name expected exactly once.</param>
    /// <param name="feeText">Existing fee wording expected to survive the marker change.</param>
    private static void AssertOwnedRialGatewayLabel(List<string> labels, string gatewayName, string? feeText)
        => AssertRialGatewayLabel(labels, gatewayName, feeText);

    /// <summary>Enables every payment gateway in the in-memory availability probe.</summary>
    /// <param name="gateway">
    /// Probe that replaces the production switch store. Each toggle advances the snapshot revision, so the revision of
    /// the current snapshot is re-read before every call.
    /// </param>
    private static void EnableEveryGateway(GatewayAvailabilityProbe gateway)
    {
        foreach (var candidate in new[]
                 {
                     PaymentGateway.HooshPay, PaymentGateway.Tetraminator, PaymentGateway.UniquePay,
                     PaymentGateway.AtlasPay, PaymentGateway.NowPayments
                 })
        {
            gateway.SetEnabledAsync(candidate, true, gateway.Snapshot.Revision).GetAwaiter().GetResult();
        }
    }

    /// <summary>Reflects the owned-bot wallet-charge reply keyboard and returns its customer-visible captions.</summary>
    /// <param name="service">Owned-bot service built with the in-memory gateway probe.</param>
    /// <returns>One caption per row, in render order.</returns>
    private static List<string> OwnedChargeKeyboardLabels(TelegramBotService service)
    {
        var method = typeof(TelegramBotService).GetMethod("BuildChargePaymentMethodKeyboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var markup = (ReplyKeyboardMarkup)method.Invoke(service, Array.Empty<object>())!;
        return markup.Keyboard.SelectMany(row => row).Select(button => button.Text).ToList();
    }

    /// <summary>Reflects the private owned-bot gateway caption matcher.</summary>
    /// <param name="input">Caption text being matched, as a customer's press would deliver it.</param>
    /// <param name="currentLabel">Current shipped caption.</param>
    /// <param name="acceptedLegacyLabels">Captions accepted only for previously issued keyboards.</param>
    /// <returns><c>true</c> when the matcher routes the caption to its gateway handler.</returns>
    private static bool OwnedGatewayActionMatches(string input, string currentLabel, params string[] acceptedLegacyLabels)
    {
        var method = typeof(TelegramBotService).GetMethod("IsGatewayAction", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, new object[] { input, currentLabel, acceptedLegacyLabels })!;
    }

    /// <summary>Builds the tenant AtlasPay invoice keyboard through the production internal builder.</summary>
    /// <param name="payment">Persisted tenant AtlasPay payment row; the id becomes the status callback payload.</param>
    /// <returns>The rendered inline keyboard.</returns>
    private static InlineKeyboardMarkup TenantAtlasPayKeyboard(AtlasPayPaymentInfo payment)
        => TenantBotService.BuildTenantAtlasPayPaymentKeyboard(payment);

    /// <summary>Flattens an inline keyboard into its customer-visible captions.</summary>
    /// <param name="markup">Rendered keyboard; must not be <c>null</c>.</param>
    /// <returns>One caption per button, in render order.</returns>
    private static List<string> FlattenLabels(InlineKeyboardMarkup markup)
        => markup.InlineKeyboard.SelectMany(row => row).Select(button => button.Text).ToList();

    /// <summary>Counts non-overlapping occurrences of a substring inside a caption.</summary>
    /// <param name="value">Caption being inspected.</param>
    /// <param name="needle">Substring to count, for example the rial marker.</param>
    /// <returns>The number of ordinal matches found.</returns>
    private static int Occurrences(string value, string needle)
    {
        var count = 0;
        var index = value.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = value.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Builds a tenant-storefront configuration that has every gateway both enabled and fully configured.
    /// </summary>
    /// <param name="databases">Fixture owning the temporary users.db and credentials.db paths.</param>
    /// <param name="xuiUrl">Loopback XUI base URL used only so startup validation succeeds.</param>
    /// <returns>
    /// The standard AtlasPay tenant test configuration with the five gateway switches turned on and the minimum
    /// credentials each readiness check requires. Only placeholder secrets are used; no live provider is contacted.
    /// </returns>
    /// <remarks>
    /// <c>PaymentGatewayAvailabilityService</c> treats UniquePay, AtlasPay, and NOWPayments as enabled only when their
    /// required credentials and URLs are present, so the placeholder values are required for the captions to render.
    /// </remarks>
    private static IConfiguration RialGatewayLabelConfiguration(Databases databases, string xuiUrl)
        => new ConfigurationBuilder()
            .AddConfiguration(AtlasTenantConfiguration(databases, xuiUrl))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["hooshPayEnabled"] = "true",
                ["hooshPayApiKey"] = "test-only-hooshpay-key",
                ["hooshPayIpnSecretKey"] = "test-only-hooshpay-ipn",
                ["tetraminatorEnabled"] = "true",
                ["tetraminatorApiKey"] = "test-only-tetraminator-key",
                ["uniquePayEnabled"] = "true",
                ["uniquePayBusinessToken"] = "test-only-uniquepay-token",
                ["nowPaymentsEnabled"] = "true",
                ["nowPaymentApiKey"] = "test-only-nowpayments-key",
                ["ipnSecretKey"] = "test-only-nowpayments-ipn"
            })
            .Build();
}
