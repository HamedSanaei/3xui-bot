using Adminbot.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Canonical account state for website outbox deduplication; never logs request contents.</summary>
internal static class GozargahSyncSemantics
{
    /// <summary>Compares normalized outbound state and ownership without transport tracking identity.</summary>
    /// <param name="previous">Persisted preceding event with a readable request.</param>
    /// <param name="desired">Detached proposed event; no database insert is required.</param>
    /// <returns>True only when all meaningful payload and bot/owner fields agree; malformed history returns false.</returns>
    /// <remarks>Rename payloads describe their final name. Arrays retain order, JSON object keys are sorted;
    /// tracking_code alone is excluded. Delete is a tombstone and is never equivalent to an update.</remarks>
    /// <example><code>if (GozargahSyncSemantics.Equivalent(lastSucceeded, proposedUpdate)) return lastSucceeded;</code></example>
    internal static bool Equivalent(GozargahSiteSyncEvent previous, GozargahSiteSyncEvent desired)
    {
        if (previous.Operation == GozargahSiteSyncOperations.Delete && desired.Operation != previous.Operation) return false;
        if (previous.BotId != desired.BotId || previous.TenantBotId != desired.TenantBotId
            || previous.TelegramUserId != desired.TelegramUserId || previous.OwnerTelegramUserId != desired.OwnerTelegramUserId
            || previous.BuyerTelegramUserId != desired.BuyerTelegramUserId || previous.Email != desired.Email
            || previous.Uuid != desired.Uuid || previous.SubId != desired.SubId || previous.SubLink != desired.SubLink) return false;
        try { return JToken.DeepEquals(Canonical(previous.RequestJson), Canonical(desired.RequestJson)); }
        catch (JsonException) { return false; }
    }

    /// <summary>Canonicalizes an order payload without guessing missing historical fields.</summary>
    /// <param name="json">Sensitive serialized website request, required.</param>
    /// <returns>Normalized JSON token retaining every field except top-level tracking_code.</returns>
    /// <remarks>Structured comments are recursively canonicalized; arbitrary comment text is preserved exactly.</remarks>
    private static JToken Canonical(string json)
    {
        var obj = JObject.Parse(json ?? "{}");
        obj.Remove("tracking_code");
        if (!string.IsNullOrEmpty((string)obj["new_name"])) obj["name"] = obj["new_name"].DeepClone();
        obj["new_name"] = JValue.CreateNull();
        if (obj["comment"]?.Type == JTokenType.String)
        {
            try { obj["comment"] = Sort(JToken.Parse((string)obj["comment"])); }
            catch (JsonException) { }
        }
        if (obj["inbound"]?.Type == JTokenType.String)
            obj["inbound"] = string.Join(",", ((string)obj["inbound"]).Split(',')
                .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        return Sort(obj);
    }

    /// <summary>Sorts object properties recursively while preserving array order and scalar values.</summary>
    /// <param name="token">Parsed JSON subtree.</param>
    /// <returns>A deterministic detached subtree.</returns>
    private static JToken Sort(JToken token) => token switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Sort(p.Value)))),
        JArray array => new JArray(array.Select(Sort)),
        _ => token.DeepClone()
    };
}
