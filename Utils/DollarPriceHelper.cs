using System.Globalization;
using System.Text.Json;

namespace Adminbot.Utils
{
    public class DollarPriceHelper
    {
        private static readonly HttpClient Client = CreateHttpClient();

        public async Task<long> NobitexUSDTIRTPrice()
        {
            return (await NobitexUSDTIRTQuote()).Price;
        }

        public async Task<DollarPriceQuote> NobitexUSDTIRTQuote()
        {
            var endpoints = new[]
            {
                ("https://apiv2.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=rls", "nobitex:apiv2-market-stats-usdt-rls"),
                ("https://apiv2.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=irt", "nobitex:apiv2-market-stats-usdt-irt"),
                ("https://api.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=rls", "nobitex:market-stats-usdt-rls"),
                ("https://api.nobitex.ir/market/stats?srcCurrency=usdt&dstCurrency=irt", "nobitex:market-stats-usdt-irt"),
                ("https://api.nobitex.ir/v3/orderbook/USDTIRT", "nobitex:v3-orderbook-USDTIRT")
            };

            foreach (var (url, source) in endpoints)
            {
                var quote = await TryReadQuoteAsync(url, source);
                if (quote.Price > 0)
                    return quote;
            }

            return DollarPriceQuote.Empty;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Adminbot/1.1.0");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        private static async Task<DollarPriceQuote> TryReadQuoteAsync(string url, string source)
        {
            try
            {
                using var response = await Client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var responseBody = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(responseBody);
                var price = ExtractPrice(document.RootElement);

                if (price <= 0)
                    return DollarPriceQuote.Empty;

                return new DollarPriceQuote
                {
                    Price = price,
                    Source = source
                };
            }
            catch
            {
                return DollarPriceQuote.Empty;
            }
        }

        private static long ExtractPrice(JsonElement root)
        {
            var price = ExtractStatsPrice(root);
            if (price > 0)
                return price;

            price = ExtractOrderBookTopPrice(root);
            if (price > 0)
                return price;

            return FindFirstPriceByName(root);
        }

        private static long ExtractStatsPrice(JsonElement root)
        {
            if (!root.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
                return 0;

            foreach (var pairKey in new[] { "usdt-rls", "usdt-irt", "USDT-RLS", "USDT-IRT" })
            {
                if (!stats.TryGetProperty(pairKey, out var pair) || pair.ValueKind != JsonValueKind.Object)
                    continue;

                var isRialPair = pairKey.Contains("rls", StringComparison.OrdinalIgnoreCase);
                foreach (var field in new[] { "latest", "dayClose", "bestSell", "bestBuy", "lastTradePrice" })
                {
                    if (pair.TryGetProperty(field, out var token) && TryReadLong(token, out var value))
                        return isRialPair
                            ? Math.Max(1, value / 10)
                            : value;
                }
            }

            return 0;
        }

        private static long ExtractOrderBookTopPrice(JsonElement root)
        {
            foreach (var field in new[] { "lastTradePrice", "last_trade_price", "latest", "lastPrice", "close" })
            {
                if (root.TryGetProperty(field, out var token) && TryReadLong(token, out var value))
                    return value;
            }

            foreach (var bookField in new[] { "asks", "bids" })
            {
                if (!root.TryGetProperty(bookField, out var book) || book.ValueKind != JsonValueKind.Array)
                    continue;

                var firstRow = book.EnumerateArray().FirstOrDefault();
                if (firstRow.ValueKind == JsonValueKind.Array)
                {
                    var firstValue = firstRow.EnumerateArray().FirstOrDefault();
                    if (TryReadLong(firstValue, out var value))
                        return value;
                }
                else if (firstRow.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in new[] { "price", "rate" })
                    {
                        if (firstRow.TryGetProperty(field, out var token) && TryReadLong(token, out var value))
                            return value;
                    }
                }
            }

            return 0;
        }

        private static long FindFirstPriceByName(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (IsPriceField(property.Name) && TryReadLong(property.Value, out var directValue))
                        return directValue;

                    var nestedValue = FindFirstPriceByName(property.Value);
                    if (nestedValue > 0)
                        return nestedValue;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nestedValue = FindFirstPriceByName(item);
                    if (nestedValue > 0)
                        return nestedValue;
                }
            }

            return 0;
        }

        private static bool IsPriceField(string fieldName)
        {
            return fieldName.Equals("lastTradePrice", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("last_trade_price", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("dayClose", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("bestSell", StringComparison.OrdinalIgnoreCase) ||
                   fieldName.Equals("bestBuy", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadLong(JsonElement token, out long value)
        {
            value = 0;

            if (token.ValueKind == JsonValueKind.Number)
            {
                if (token.TryGetInt64(out value))
                    return value > 0;

                if (token.TryGetDecimal(out var decimalValue))
                {
                    value = decimal.ToInt64(decimal.Round(decimalValue, 0, MidpointRounding.AwayFromZero));
                    return value > 0;
                }
            }

            if (token.ValueKind != JsonValueKind.String)
                return false;

            var text = token.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim().Replace(",", string.Empty);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value > 0;

            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDecimal))
            {
                value = decimal.ToInt64(decimal.Round(parsedDecimal, 0, MidpointRounding.AwayFromZero));
                return value > 0;
            }

            return false;
        }
    }

    public class DollarPriceQuote
    {
        public static DollarPriceQuote Empty { get; } = new DollarPriceQuote();

        public long Price { get; set; }
        public string Source { get; set; }
    }
}
