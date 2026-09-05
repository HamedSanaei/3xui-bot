using Newtonsoft.Json;

/// <summary>Copies mutable configuration models before legacy per-customer code changes nested values.</summary>
public static class RuntimeSnapshot
{
    /// <summary>Creates an independent deep snapshot without retaining shared nested objects.</summary>
    /// <typeparam name="T">Serializable configuration model type.</typeparam>
    /// <param name="value">Required or nullable configuration object; may contain secrets and must never be logged.</param>
    /// <returns>An independent private copy, or default when the source is null.</returns>
    /// <remarks>Use at mutation boundaries, such as selecting an inbound or generating one customer's VMess link.
    /// The serialized intermediate exists only in memory and must never enter an inbox payload or diagnostic log.</remarks>
    /// <example><code>var panel = RuntimeSnapshot.Copy(configuredPanel); panel.Inbounds = selectedInbounds;</code></example>
    public static T Copy<T>(T value) => value == null ? default : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
}
