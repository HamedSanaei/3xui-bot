using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class NowPaymentsRateTests
{
    [Fact]
    public void Canonical_irt_quote_converts_one_million_without_factor_of_ten_error()
    {
        var amount = NowPayments.ConvertTomanUsingCanonicalIrtPrice(1_000_000, 187_944);
        Assert.Equal(5.320734m, amount);
        Assert.NotEqual(53.20734m, amount);
    }

    [Fact]
    public async Task Changed_rls_behavior_is_not_used_by_financial_quote_path()
    {
        var handler = new NobitexFixtureHandler(v3: 187_944, apiv2Irt: null, apiIrt: null);
        var helper = new DollarPriceHelper(new HttpClient(handler));

        var quote = await helper.NobitexUSDTIRTQuote();

        Assert.Equal(187_944, quote.Price);
        Assert.Equal("IRT", quote.SourceUnit);
        Assert.Equal(0, handler.RlsRequestCount);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("dstCurrency=rls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task V3_orderbook_last_trade_price_is_canonical_irt()
    {
        var handler = new NobitexFixtureHandler(v3: 187_944, apiv2Irt: null, apiIrt: null);
        var quote = await new DollarPriceHelper(new HttpClient(handler)).NobitexUSDTIRTQuote();

        Assert.Equal(187_944, quote.Price);
        Assert.Equal(187_944, quote.RawPrice);
        Assert.Equal(187_944, quote.NormalizedPriceIrt);
        Assert.Equal("USDTIRT", quote.SourcePair);
        Assert.Equal("irt-native:none", quote.Normalization);
    }

    [Theory]
    [InlineData(199_999)]
    [InlineData(200_000)]
    [InlineData(200_001)]
    [InlineData(250_000)]
    public void Legitimate_rates_have_no_threshold_discontinuity(long priceIrt)
    {
        var amount = NowPayments.ConvertTomanUsingCanonicalIrtPrice(1_000_000, priceIrt);
        var expected = Math.Round(1_000_000m / priceIrt, 6, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, amount);
    }

    [Fact]
    public async Task Factor_of_ten_disagreement_rejects_outlier_and_uses_consensus()
    {
        var handler = new NobitexFixtureHandler(v3: 188_000, apiv2Irt: 187_900, apiIrt: 18_800);
        var quote = await new DollarPriceHelper(new HttpClient(handler)).NobitexUSDTIRTQuote();

        Assert.Equal(188_000, quote.Price);
        Assert.Equal(2, quote.ConsensusSourceCount);
        Assert.NotEqual(18_800, quote.Price);
    }

    [Fact]
    public void Legacy_fallback_is_normalized_by_explicit_unit_not_magnitude()
    {
        Assert.Equal(180_000, NowPayments.NormalizeConfiguredFallbackPriceIrt(1_800_000, "rial"));
        Assert.Equal(180_000, NowPayments.NormalizeConfiguredFallbackPriceIrt(1_800_000, null));
        Assert.Equal(1_800_000, NowPayments.NormalizeConfiguredFallbackPriceIrt(1_800_000, "irt"));
        Assert.Equal(0, NowPayments.NormalizeConfiguredFallbackPriceIrt(1_800_000, "unknown"));
    }

    [Fact]
    public async Task Explicit_rial_fallback_creates_invoice_with_canonical_irt_diagnostics()
    {
        var apiHandler = new CapturingNowPaymentsHandler();
        var nowPayments = CreateNowPayments(apiHandler, DollarPriceQuote.Empty, 1_800_000, "rial");

        var response = await nowPayments.CreateInvoiceAsync(1_000_000, "fallback-order", "test");

        Assert.Equal(1, apiHandler.RequestCount);
        Assert.Equal(180_000, response.LocalUsdtIrtPrice);
        Assert.True(response.LocalUsedFallbackPrice);
        Assert.False(response.LocalPriceIsRial);
        Assert.Equal(5.555556m, ReadPriceAmount(apiHandler.LastRequestBody!));
    }

    [Fact]
    public async Task No_valid_live_quote_and_no_valid_fallback_fails_before_http_create()
    {
        var apiHandler = new CapturingNowPaymentsHandler();
        var nowPayments = CreateNowPayments(apiHandler, DollarPriceQuote.Empty, 0, "irt");

        await Assert.ThrowsAsync<NowPaymentsRateUnavailableException>(() =>
            nowPayments.CreateInvoiceAsync(1_000_000, "fail-closed-order", "test"));

        Assert.Equal(0, apiHandler.RequestCount);
        Assert.Null(apiHandler.LastRequestBody);
    }

    [Fact]
    public async Task Serialized_invoice_request_uses_5_point_32_not_53_for_canonical_quote()
    {
        var apiHandler = new CapturingNowPaymentsHandler();
        var quote = CanonicalQuote(187_944, "fixture:v3");
        var nowPayments = CreateNowPayments(apiHandler, quote, 0, "irt");

        var response = await nowPayments.CreateInvoiceAsync(1_000_000, "canonical-order", "test");
        var priceAmount = ReadPriceAmount(apiHandler.LastRequestBody!);

        Assert.Equal(5.320734m, priceAmount);
        Assert.Equal(5.320734m, response.price_amount);
        Assert.InRange(priceAmount, 5.32m, 5.33m);
        Assert.DoesNotContain("53.", apiHandler.LastRequestBody!, StringComparison.Ordinal);
    }

    private static NowPayments CreateNowPayments(
        HttpMessageHandler apiHandler,
        DollarPriceQuote quote,
        long fallbackPrice,
        string fallbackUnit)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["nowPaymentApiKey"] = "test-api-key",
            ["nowpaymentPriceCurrency"] = "usdtbsc",
            ["nowpaymentUsdIrtFallbackPrice"] = fallbackPrice.ToString(),
            ["nowpaymentUsdIrtFallbackPriceUnit"] = fallbackUnit
        }).Build();
        var client = new HttpClient(apiHandler) { BaseAddress = new Uri("https://api.nowpayments.io/v1/") };
        return new NowPayments(configuration, client, new FixedQuoteProvider(quote));
    }

    private static DollarPriceQuote CanonicalQuote(long price, string source) => new()
    {
        Price = price,
        RawPrice = price,
        NormalizedPriceIrt = price,
        Source = source,
        SourcePair = "USDTIRT",
        SourceUnit = "IRT",
        Normalization = "irt-native:none"
    };

    private static decimal ReadPriceAmount(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("price_amount").GetDecimal();
    }

    private sealed class FixedQuoteProvider(DollarPriceQuote quote) : IDollarPriceQuoteProvider
    {
        public Task<DollarPriceQuote> NobitexUSDTIRTQuote(CancellationToken cancellationToken = default) =>
            Task.FromResult(quote);
    }

    private sealed class CapturingNowPaymentsHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var priceAmount = LastRequestBody == null ? 0m : ReadPriceAmount(LastRequestBody);
            var responseJson = JsonSerializer.Serialize(new
            {
                id = "invoice-test",
                invoice_url = "https://example.test/invoice",
                price_amount = priceAmount,
                price_currency = "usdtbsc",
                order_id = "test-order"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NobitexFixtureHandler(long? v3, long? apiv2Irt, long? apiIrt) : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = new();
        public int RlsRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            RequestUris.Add(uri);
            if (uri.Contains("dstCurrency=rls", StringComparison.OrdinalIgnoreCase))
                RlsRequestCount++;

            string body;
            if (uri.Contains("/v3/orderbook/USDTIRT", StringComparison.OrdinalIgnoreCase))
                body = v3.HasValue ? $"{{\"lastTradePrice\":\"{v3.Value}\"}}" : "{}";
            else if (uri.Contains("apiv2.nobitex.ir", StringComparison.OrdinalIgnoreCase))
                body = StatsBody(apiv2Irt);
            else
                body = StatsBody(apiIrt);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static string StatsBody(long? price) => price.HasValue
            ? $"{{\"stats\":{{\"usdt-irt\":{{\"latest\":\"{price.Value}\"}}}}}}"
            : "{\"stats\":{}}";
    }
}