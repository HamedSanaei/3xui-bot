using System.Text;
using System.Text.Json.Serialization;
using Adminbot.Domain;
using Adminbot.Utils;
using Newtonsoft.Json;


public class User
{
    public long Id { get; set; }
    public string SelectedCountry { get; set; }
    public string SelectedPeriod { get; set; }
    public string Type { get; set; }
    /// <summary>
    /// check if is it a create or read or update config flow
    /// </summary>
    public string Flow { get; set; }
    public string LastStep { get; set; }

    public string TotoalGB { get; set; }
    public string ConfigLink { get; set; }// or message for user
    public string SubLink { get; set; }// or message for user

    public string Email { get; set; }
    public string _ConfigPrice { get; set; }
    public long ConfigPrice
    {
        get
        {
            // You can add custom logic here
            return Convert.ToInt64(_ConfigPrice);
        }
        set
        {
            // You can add custom logic here
            _ConfigPrice = value.ToString();
        }
    }
    public DateTime LastFreeAcc { get; set; } = DateTime.MinValue;
    public string PaymentMethod { get; set; } = "credit";
    public int AccountCounter { get; set; }

}
public class CookieData
{
    public Guid Id { get; set; }
    public string Url { get; set; }
    public string SessionCookie { get; set; }
    public DateTimeOffset ExpirationDate { get; set; }
}

// Replace this with your actual class structure for settings
public class Settings
{
    public int Id { get; set; }
    public List<Client> Clients { get; set; }
}

// Replace this with your actual class structure for add account request
public class AddAccountRequest
{
    public int Id { get; set; }
    public List<Client> Clients { get; set; }
}

// Replace this with your actual class structure for client
public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int AlterId { get; set; } = 0;
    public string Email { get; set; } = AccountGenerator.GenerateRandomAccountName();
    public int LimitIp { get; set; } = 0;
    public long TotalGB { get; set; }
    // [JsonProperty("expiryTime")]
    // [Newtonsoft.Json.JsonConverter(typeof(UnixTimestampConverter))]
    // public DateTime ExpiryTime { get; set; }


    [JsonProperty("expiryTime")]
    public long ExpiryTimeRaw { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime ExpiryTime
    {
        get
        {
            if (ExpiryTimeRaw < 0) return DateTime.UtcNow.AddMilliseconds(ExpiryTimeRaw * -1).ToLocalTime();

            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ExpiryTimeRaw).ToLocalTime();
        }
        set => ExpiryTimeRaw = new DateTimeOffset(value).ToUnixTimeMilliseconds();
    }

    public string TgId { get; set; } = "";
    public string SubId { get; set; } = AccountGenerator.GenerateRandomSubId();
    public bool Enable { get; set; } = true;
    // Add other properties based on your actual structure

    public static string MakeSettingString(Client client)
    {
        client.SubId = client.Email;

        StringBuilder sb = new StringBuilder("{\"clients\":[{\"id\":\"");
        sb.Append(client.Id.ToString());
        sb.Append("\",\"alterId\":");
        sb.Append(client.AlterId.ToString());
        sb.Append(",\"email\":\"");
        sb.Append(client.Email);
        sb.Append("\",\"limitIp\":0,\"totalGB\":");
        sb.Append(client.TotalGB.ToString());
        sb.Append(",\"expiryTime\":");
        sb.Append(ConvertDateTimeToTimestamp(client.ExpiryTime));
        sb.Append(" ,\"enable\":");
        sb.Append(client.Enable.ToString().ToLower());
        sb.Append(",\"tgId\":\"");
        sb.Append(client.TgId);
        sb.Append("\",\"subId\":\"");
        sb.Append(client.SubId);
        sb.Append("\"}]}");
        return sb.ToString();
    }

    public static string MakeSettingString(Client client, AccountDtoUpdate accountDto)
    {

        StringBuilder sb = new StringBuilder("{\"clients\":[{\"id\":\"");
        sb.Append(client.Id.ToString());
        sb.Append("\",\"alterId\":");
        sb.Append(client.AlterId.ToString());
        sb.Append(",\"email\":\"");
        sb.Append(client.Email);
        sb.Append("\",\"limitIp\":0,\"totalGB\":");
        sb.Append(client.TotalGB.ToString());
        if (accountDto.ConfigLink.Contains("flow=xtls-rprx-vision"))
            sb.Append(",\"flow\":\"xtls-rprx-vision\"");

        sb.Append(",\"expiryTime\":");
        sb.Append(ConvertDateTimeToTimestamp(client.ExpiryTime));
        sb.Append(" ,\"enable\":true,\"tgId\":\"");
        sb.Append(client.TgId);
        sb.Append(" \",\"subId\":\"");
        sb.Append(client.SubId);
        sb.Append("\"}]}");
        return sb.ToString();
    }


    static long ConvertDateTimeToTimestamp(DateTime dateTime)
    {

        DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan timeSpan = dateTime.ToUniversalTime() - unixEpoch;

        return (long)timeSpan.TotalMilliseconds;

        // // Assuming the input DateTime is in UTC to match the timestamp provided
        // DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // TimeSpan timeDifference = dateTime.ToUniversalTime() - unixEpoch;

        // // Return the timestamp in seconds
        // return (long)timeDifference.TotalSeconds;
    }

}


public class ClientExtend : Client
{
    public int InboundId { get; set; }
    public long Up { get; set; }
    public long Down { get; set; }

    public string TotalUsedTrafficInGB
    {
        get
        {
            long totalBytes = Up + Down;
            double totalUsed = ConvertBytesToGB(totalBytes);
            string allowed = ConvertBytesToGB(TotalGB).ToString();
            return $" used {totalUsed:F3} GB / {allowed} GB";
        }
    }
    private double ConvertBytesToGB(long bytes)
    {
        const double bytesInGB = 1024 * 1024 * 1024;
        return bytes / bytesInGB;
    }
}

public class AccountGenerator
{
    private static readonly Random random = new Random();

    public static string GenerateRandomAccountName()
    {
        const string prefix = "vniacc";
        const string allowedChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        // Generate 6 random characters
        string randomChars = new string(Enumerable.Repeat(allowedChars, 8)
            .Select(s => s[random.Next(s.Length)]).ToArray());

        // Combine the prefix and random characters
        string accountName = $"{prefix}{randomChars}";

        return accountName;
    }
    public static string GenerateRandomSubId()
    {
        const string allowedChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        // Generate 6 random characters
        string randomChars = new string(Enumerable.Repeat(allowedChars, 14)
            .Select(s => s[random.Next(s.Length)]).ToArray());

        // Combine the prefix and random characters
        string accountName = $"{randomChars}";

        return accountName;
    }
}