using System.Globalization;
using System.Text.Json;

namespace Adminbot.Utils
{
    /// <summary>Provides canonical USDT quotes expressed only as IRT/Toman per one USDT.</summary>
    public interface IDollarPriceQuoteProvider
    {
        Task<DollarPriceQuote> NobitexUSDTIRTQuote(CancellationToken cancellationToken = default);
    }

    public class DollarPriceHelper : IDollarPriceQuoteProvider
    {
        private const decimal MaximumConsensusRatio = 2m;
        private static readonly HttpClient SharedClient = CreateHttpClient();
        private readonly HttpClient _client;

        private static readonly QuoteEndpoint[] IrtNativeEndpoints =
        {
            new("https://api.nobitex.ir/v3/orderbook/USDTIRT", "nobitex:v3-orderbook-USDTIRT", "USDTIRT", QuoteShape.OrderBook),
            new("https://apiv2.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=irt", "nobitex:apiv2-market-stats-usdt-irt", "usdt-irt", QuoteShape.Stats),
            new("https://api.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=irt", "nobitex:market-stats-usdt-irt", "usdt-irt", QuoteShape.Stats)
        };

        public DollarPriceHelper() : this(SharedClient) { }

        /// <summary>HTTP seam for deterministic tests; production uses the shared configured client.</summary>
        public DollarPriceHelper(HttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<long> NobitexUSDTIRTPrice(CancellationToken cancellationToken = default)
        {
            return (await NobitexUSDTIRTQuote(cancellationToken)).Price;
        }

        /// <summary>
        /// Reads only IRT-native Nobitex markets. DollarPriceQuote.Price always means Toman/IRT per one USDT.
        /// Ambiguous RLS-labelled endpoints are intentionally excluded from this financial path.
        /// </summary>
        public async Task<DollarPriceQuote> NobitexUSDTIRTQuote(CancellationToken cancellationToken = default)
        {
            var tasks = IrtNativeEndpoints.Select(endpoint => TryReadIrtQuoteAsync(endpoint, cancellationToken)).ToArray();
            var quotes = (await Task.WhenAll(tasks)).Where(quote => quote.Price > 0).ToList();
            return SelectConsensus(quotes);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Adminbot/1.1.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        private async Task<DollarPriceQuote> TryReadIrtQuoteAsync(QuoteEndpoint endpoint, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _client.GetAsync(endpoint.Url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(responseBody);
                var rawPrice = endpoint.Shape == QuoteShape.OrderBook
                    ? ExtractOrderBookTopPrice(document.RootElement)
                    : ExtractStatsIrtPrice(document.RootElement);

                if (rawPrice <= 0)
                    return DollarPriceQuote.Empty;

                return new DollarPriceQuote
                {
                    Price = rawPrice,
                    RawPrice = rawPrice,
                    NormalizedPriceIrt = rawPrice,
                    Source = endpoint.Source,
                    SourcePair = endpoint.Pair,
                    SourceUnit = "IRT",
                    Normalization = "irt-native:none"
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return DollarPriceQuote.Empty;
            }
        }

        internal static DollarPriceQuote SelectConsensus(IReadOnlyList<DollarPriceQuote> input)
        {
            var quotes = input?.Where(quote => quote != null && quote.Price > 0 &&
                string.Equals(quote.SourceUnit, "IRT", StringComparison.OrdinalIgnoreCase)).ToList()
                ?? new List<DollarPriceQuote>();

            if (quotes.Count == 0)
                return DollarPriceQuote.Empty;
            if (quotes.Count == 1)
            {
                quotes[0].ConsensusSourceCount = 1;
                return quotes[0];
            }

            List<DollarPriceQuote> bestCluster = null;
            var bestIndex = int.MaxValue;
            for (var index = 0; index < quotes.Count; index++)
            {
                var candidate = quotes[index];
                var cluster = quotes.Where(other => IsSameScale(candidate.Price, other.Price)).ToList();
                if (bestCluster == null || cluster.Count > bestCluster.Count ||
                    cluster.Count == bestCluster.Count && index < bestIndex)
                {
                    bestCluster = cluster;
                    bestIndex = index;
                }
            }

            if (bestCluster == null || bestCluster.Count < 2)
            {
                foreach (var quote in quotes)
                    LogRejectedQuote(quote, "no_cross_source_consensus");
                return DollarPriceQuote.Empty;
            }

            foreach (var rejected in quotes.Except(bestCluster))
                LogRejectedQuote(rejected, "factor_of_ten_or_scale_disagreement");

            var sorted = bestCluster.Select(quote => quote.Price).OrderBy(price => price).ToArray();
            var midpoint = sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2m;
            var chosen = bestCluster
                .Select((quote, index) => new { Quote = quote, Index = index, Distance = Math.Abs(quote.Price - midpoint) })
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Index)
                .First().Quote;
            chosen.ConsensusSourceCount = bestCluster.Count;
            return chosen;
        }

        private static bool IsSameScale(long left, long right)
        {
            if (left <= 0 || right <= 0) return false;
            var high = Math.Max(left, right);
            var low = Math.Min(left, right);
            return (decimal)high / low <= MaximumConsensusRatio;
        }

        private static void LogRejectedQuote(DollarPriceQuote quote, string reason)
        {
            Console.WriteLine(
                $"[NobitexRate] rejected source={quote.Source ?? "unknown"}, raw={quote.RawPrice}, normalizedIrt={quote.Price}, reason={reason}");
        }

        private static long ExtractStatsIrtPrice(JsonElement root)
        {
            if (!root.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
                return 0;

            var pair = stats.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals("usdt-irt", StringComparison.OrdinalIgnoreCase));
            if (pair.Value.ValueKind != JsonValueKind.Object)
                return 0;

            foreach (var field in new[] { "latest", "dayClose", "bestSell", "bestBuy", "lastTradePrice" })
                if (pair.Value.TryGetProperty(field, out var token) && TryReadLong(token, out var value))
                    return value;
            return 0;
        }

        private static long ExtractOrderBookTopPrice(JsonElement root)
        {
            foreach (var field in new[] { "lastTradePrice", "last_trade_price", "latest", "lastPrice", "close" })
                if (root.TryGetProperty(field, out var token) && TryReadLong(token, out var value))
                    return value;

            foreach (var bookField in new[] { "asks", "bids" })
            {
                if (!root.TryGetProperty(bookField, out var book) || book.ValueKind != JsonValueKind.Array)
                    continue;
                var firstRow = book.EnumerateArray().FirstOrDefault();
                if (firstRow.ValueKind == JsonValueKind.Array)
                {
                    var firstValue = firstRow.EnumerateArray().FirstOrDefault();
                    if (TryReadLong(firstValue, out var value)) return value;
                }
                else if (firstRow.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in new[] { "price", "rate" })
                        if (firstRow.TryGetProperty(field, out var token) && TryReadLong(token, out var value))
                            return value;
                }
            }
            return 0;
        }

        private static bool TryReadLong(JsonElement token, out long value)
        {
            value = 0;
            if (token.ValueKind == JsonValueKind.Number)
            {
                if (token.TryGetInt64(out value)) return value > 0;
                if (token.TryGetDecimal(out var decimalValue))
                {
                    value = decimal.ToInt64(decimal.Round(decimalValue, 0, MidpointRounding.AwayFromZero));
                    return value > 0;
                }
            }
            if (token.ValueKind != JsonValueKind.String) return false;
            var text = token.GetString()?.Trim().Replace(",", string.Empty);
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value > 0;
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return false;
            value = decimal.ToInt64(decimal.Round(parsed, 0, MidpointRounding.AwayFromZero));
            return value > 0;
        }

        private sealed record QuoteEndpoint(string Url, string Source, string Pair, QuoteShape Shape);
        private enum QuoteShape { OrderBook, Stats }
    }

    public class DollarPriceQuote
    {
        public static DollarPriceQuote Empty => new();
        /// <summary>Canonical price: IRT/Toman per one USDT. Never raw IRR/RLS.</summary>
        public long Price { get; set; }
        public long RawPrice { get; set; }
        public long NormalizedPriceIrt { get; set; }
        public string Source { get; set; }
        public string SourcePair { get; set; }
        public string SourceUnit { get; set; }
        public string Normalization { get; set; }
        public int ConsensusSourceCount { get; set; }
    }
}