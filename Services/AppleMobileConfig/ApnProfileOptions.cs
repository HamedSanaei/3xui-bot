namespace Adminbot.Services.AppleMobileConfig;

/// <summary>Immutable input contract for one Apple Cellular APN profile.</summary>
public sealed record ApnProfileOptions
{
    /// <summary>Carrier access point name. Required.</summary>
    public required string Apn { get; init; }

    /// <summary>Optional APN username; omitted from the plist when null or empty.</summary>
    public string Username { get; init; }

    /// <summary>Optional APN password; never logged by the generator or Telegram flow.</summary>
    public string Password { get; init; }

    /// <summary>APN authentication scheme. Defaults to PAP.</summary>
    public ApnAuthenticationType AuthenticationType { get; init; } = ApnAuthenticationType.Pap;

    /// <summary>Allowed IP protocol family. Defaults to Apple's dual-stack mask.</summary>
    public IpProtocolMode Protocol { get; init; } = IpProtocolMode.IPv4AndIPv6;

    /// <summary>Human-readable profile and payload name shown by iOS.</summary>
    public string DisplayName { get; init; } = "APN Configuration";

    /// <summary>Optional profile description.</summary>
    public string Description { get; init; }

    /// <summary>
    /// Emits Apple's documented domestic-roaming and roaming masks on the APNs item.
    /// </summary>
    public bool ConfigureRoamingProtocol { get; init; } = true;

    /// <summary>
    /// Enables Apple's APNs-item XLAT464 switch when explicitly requested.
    /// </summary>
    /// <remarks>Defaults off; normal IPv4+IPv6 generation never depends on XLAT464.</remarks>
    public bool EnableXlat464 { get; init; }
}
