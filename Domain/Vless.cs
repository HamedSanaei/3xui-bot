using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class Vless
{
    public Guid Id { get; set; }
    public string Domain { get; set; }

    public static Vless DecodeVlessLink(string vlessLink)
    {
        const string prefix = "vless://";
        const char separator1 = '@';
        const char separator2 = ':';

        int idStartIndex = vlessLink.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int idEndIndex = vlessLink.IndexOf(separator1, idStartIndex);

        int domainStartIndex = idEndIndex + 1;
        int domainEndIndex = vlessLink.IndexOf(separator2, domainStartIndex);

        if (idStartIndex < 0 || idEndIndex < 0 || domainStartIndex < 0 || domainEndIndex < 0)
        {
            throw new ArgumentException("Invalid Vless link format");
        }

        string idString = vlessLink.Substring(idStartIndex, idEndIndex - idStartIndex);
        string domain = vlessLink.Substring(domainStartIndex, domainEndIndex - domainStartIndex);

        if (!Guid.TryParse(idString, out Guid id))
        {
            throw new ArgumentException("Invalid GUID format");
        }

        return new Vless
        {
            Id = id,
            Domain = domain
        };
    }
}