using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Adminbot.Services.AppleMobileConfig;

/// <summary>Stateless, in-memory generator for Apple's modern <c>com.apple.cellular</c> payload.</summary>
/// <remarks>
/// The schema keys used here are limited to Apple's current DeviceManagement Cellular documentation.
/// The deprecated <c>com.apple.apn.managed</c> payload is never emitted.
/// </remarks>
public sealed class AppleMobileConfigGenerator : IAppleMobileConfigGenerator
{
    private const string PlistPublicId = "-//Apple//DTD PLIST 1.0//EN";
    private const string PlistSystemId = "http://www.apple.com/DTDs/PropertyList-1.0.dtd";
    private const string RootPayloadType = "Configuration";
    private const string CellularPayloadType = "com.apple.cellular";
    private const string IdentifierPrefix = "com.adminbot.mobileconfig";

    /// <inheritdoc />
    public byte[] GenerateApnProfile(ApnProfileOptions options)
    {
        var normalized = MobileConfigValidation.ValidateAndNormalize(options);
        var protocolMask = (int)normalized.Protocol;
        var authentication = normalized.AuthenticationType == ApnAuthenticationType.Chap ? "CHAP" : "PAP";
        var rootUuid = Guid.NewGuid();
        var cellularUuid = Guid.NewGuid();
        while (cellularUuid == rootUuid)
            cellularUuid = Guid.NewGuid();

        var rootIdentifier = $"{IdentifierPrefix}.{rootUuid:N}";
        var cellularIdentifier = $"{rootIdentifier}.cellular";

        var apn = BuildApnDictionary(normalized, authentication, protocolMask, includeRoaming: true);
        var attachApn = BuildApnDictionary(normalized, authentication, protocolMask, includeRoaming: false);

        var cellularPayload = new XElement("dict");
        Add(cellularPayload, "APNs", new XElement("array", apn));
        Add(cellularPayload, "AttachAPN", attachApn);
        Add(cellularPayload, "PayloadDisplayName", normalized.DisplayName);
        Add(cellularPayload, "PayloadIdentifier", cellularIdentifier);
        Add(cellularPayload, "PayloadType", CellularPayloadType);
        Add(cellularPayload, "PayloadUUID", cellularUuid.ToString("D"));
        Add(cellularPayload, "PayloadVersion", 1);
        if (normalized.Description != null)
            Add(cellularPayload, "PayloadDescription", normalized.Description);

        var rootPayload = new XElement("dict");
        Add(rootPayload, "PayloadContent", new XElement("array", cellularPayload));
        Add(rootPayload, "PayloadDisplayName", normalized.DisplayName);
        Add(rootPayload, "PayloadIdentifier", rootIdentifier);
        Add(rootPayload, "PayloadRemovalDisallowed", false);
        Add(rootPayload, "PayloadType", RootPayloadType);
        Add(rootPayload, "PayloadUUID", rootUuid.ToString("D"));
        Add(rootPayload, "PayloadVersion", 1);
        if (normalized.Description != null)
            Add(rootPayload, "PayloadDescription", normalized.Description);

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", PlistPublicId, PlistSystemId, null),
            new XElement("plist", new XAttribute("version", "1.0"), rootPayload));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false
        }))
        {
            document.Save(writer);
        }

        return stream.ToArray();
    }

    private static XElement BuildApnDictionary(
        ApnProfileOptions options,
        string authentication,
        int protocolMask,
        bool includeRoaming)
    {
        var dictionary = new XElement("dict");
        Add(dictionary, "Name", options.Apn);
        Add(dictionary, "AuthenticationType", authentication);
        Add(dictionary, "AllowedProtocolMask", protocolMask);

        if (options.Username != null)
            Add(dictionary, "Username", options.Username);
        if (options.Password != null)
            Add(dictionary, "Password", options.Password);

        // Apple documents these roaming keys on Cellular.APNsItem, not on Cellular.AttachAPN.
        if (includeRoaming && options.ConfigureRoamingProtocol)
        {
            Add(dictionary, "AllowedProtocolMaskInDomesticRoaming", protocolMask);
            Add(dictionary, "AllowedProtocolMaskInRoaming", protocolMask);
        }

        // Apple documents EnableXLAT464 on Cellular.APNsItem for iOS/iPadOS 16+.
        if (includeRoaming && options.EnableXlat464)
            Add(dictionary, "EnableXLAT464", true);

        return dictionary;
    }

    private static void Add(XElement dictionary, string key, string value)
    {
        dictionary.Add(new XElement("key", key));
        dictionary.Add(new XElement("string", value));
    }

    private static void Add(XElement dictionary, string key, int value)
    {
        dictionary.Add(new XElement("key", key));
        dictionary.Add(new XElement("integer", value));
    }

    private static void Add(XElement dictionary, string key, bool value)
    {
        dictionary.Add(new XElement("key", key));
        dictionary.Add(new XElement(value ? "true" : "false"));
    }

    private static void Add(XElement dictionary, string key, XElement value)
    {
        dictionary.Add(new XElement("key", key));
        dictionary.Add(value);
    }
}
