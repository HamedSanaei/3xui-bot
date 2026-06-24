using Adminbot.Domain;
using Newtonsoft.Json;

public static class XuiV3ClientPlanEligibility
{
    public static bool IsClientInActiveServiceInbounds(
        XuiV3Client client,
        IEnumerable<XuiV3ServiceDefinition> services)
    {
        var clientInboundIds = GetClientInboundIds(client);
        if (clientInboundIds.Count == 0)
            return false;

        var activeInboundIds = services?
            .Where(service => service?.IsEnabled == true)
            .SelectMany(service => service.InboundIds ?? new List<int>())
            .Where(id => id > 0)
            .ToHashSet() ?? new HashSet<int>();

        return activeInboundIds.Count > 0 && clientInboundIds.Any(activeInboundIds.Contains);
    }

    private static HashSet<int> GetClientInboundIds(XuiV3Client client)
    {
        var inboundIds = new HashSet<int>();

        if (client?.InboundIds != null)
        {
            foreach (var id in client.InboundIds.Where(id => id > 0))
                inboundIds.Add(id);
        }

        var metadata = TryReadMetadata(client?.Comment);
        if (metadata?.InboundIds != null)
        {
            foreach (var id in metadata.InboundIds.Where(id => id > 0))
                inboundIds.Add(id);
        }

        return inboundIds;
    }

    private static XuiV3ClientMetadata TryReadMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
        }
        catch
        {
            return null;
        }
    }
}
