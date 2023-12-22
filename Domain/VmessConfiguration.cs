using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class VMessConfiguration
{
    public string V { get; set; }
    public string Ps { get; set; }
    public string Add { get; set; }
    public string Port { get; set; }
    public Guid Id { get; set; }
    public string Aid { get; set; }
    public string Scy { get; set; }
    public string Net { get; set; }
    public string Type { get; set; }
    public string Host { get; set; }
    public string Path { get; set; }
    public string Tls { get; set; }
    public string Sni { get; set; }
    public string Alpn { get; set; }
    public string Fp { get; set; }


    public static VMessConfiguration DecodeVMessLink(string base64String)
    {
        // Step 1: Remove "vmess://" prefix
        if (base64String.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            base64String = base64String.Substring("vmess://".Length);
        }

        // Step 2: Convert base64 to a regular string
        byte[] data = Convert.FromBase64String(base64String);
        string jsonString = Encoding.UTF8.GetString(data);

        // Step 3: Deserialize JSON to VMessConfiguration
        VMessConfiguration vmessConfiguration = JsonConvert.DeserializeObject<VMessConfiguration>(jsonString);

        return vmessConfiguration;
    }

    public static bool ArePropertiesNotNullOrEmpty(object obj, params string[] propertyNames)
    {
        //string[] propertiesToCheck = { "V", "Ps", "Add", "Port", "Id", "Aid", "Scy", "Net", "Type", "Host", "Path", "Tls", "Sni", "Alpn", "Fp" };
        string[] propertiesToCheck = { "V", "Ps", "Add", "Port", "Id", "Net", "Path", "Tls", "Sni", "Alpn", "Fp" };
        propertyNames = propertiesToCheck;

        foreach (string propertyName in propertyNames)
        {
            PropertyInfo property = obj.GetType().GetProperty(propertyName);
            object value = property?.GetValue(obj);

            if (value == null || (value is string stringValue && string.IsNullOrEmpty(stringValue)))
            {
                return false;
            }
        }

        return true;
    }
    public string ToVMessLink()
    {
        try
        {
            // Convert VMessConfiguration to JSON string
            string json = JsonConvert.SerializeObject(this);

            // Encode JSON to Base64 (URL-safe)
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .Replace('+', '-')
                .Replace('/', '_');

            // Construct VMess link
            return $"vmess://{base64}";
        }
        catch (Exception ex)
        {
            // Handle any serialization errors
            Console.WriteLine($"Error converting VMessConfiguration to VMess link: {ex.Message}");
            return string.Empty;
        }
    }
}