namespace Adminbot.Services.AppleMobileConfig;

/// <summary>Generates Apple configuration-profile bytes entirely in memory.</summary>
public interface IAppleMobileConfigGenerator
{
    /// <summary>Generates one unsigned APN configuration profile as UTF-8 plist XML.</summary>
    /// <param name="options">Strongly typed APN and protocol settings.</param>
    /// <returns>Complete <c>.mobileconfig</c> file bytes.</returns>
    byte[] GenerateApnProfile(ApnProfileOptions options);
}
