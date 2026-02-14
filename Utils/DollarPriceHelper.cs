using System.Text.Json;

namespace Adminbot.Utils
{
    public class DollarPriceHelper
    {
        private static readonly HttpClient client = new HttpClient();
        public async Task<long> NobitexUSDTIRTPrice()
        {
            try
            {
                string url = "https://api.nobitex.ir/v3/orderbook/USDTIRT";

                // ارسال درخواست GET به API
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();

                // استخراج مقدار lastUpdate از JSON
                using (JsonDocument document = JsonDocument.Parse(responseBody))
                {
                    if (document.RootElement.TryGetProperty("lastTradePrice", out JsonElement lastTradePriceElement))
                    {
                        long lastUpdate = 0;

                        // Check if the JSON element is a Number (e.g. 12345)
                        if (lastTradePriceElement.ValueKind == JsonValueKind.Number)
                        {
                            lastUpdate = lastTradePriceElement.GetInt64();
                        }
                        // Or if the JSON element is a String (e.g. "12345")
                        else if (lastTradePriceElement.ValueKind == JsonValueKind.String)
                        {
                            string valueString = lastTradePriceElement.GetString();
                            if (!long.TryParse(valueString, out lastUpdate))
                            {
                                // Parsing failed; fallback to 0 (or handle as needed)
                                lastUpdate = 0;
                            }
                        }
                        else
                        {
                            // It's not a string or a number, so just return 0 or handle otherwise
                            lastUpdate = 0;
                        }

                        return lastUpdate;
                    }
                    else
                    {
                        // Console.WriteLine("فیلد lastUpdate یافت نشد.");
                        return 0;
                    }
                }
            }
            catch (HttpRequestException e)
            {
                // Console.WriteLine($"خطا در درخواست HTTP: {e.Message}");
                return 0;
            }
            catch (Exception e)
            {
                return 0;
                // Console.WriteLine($"خطای غیرمنتظره: {e.Message}");

            }
        }
    }
}