using System.Text;
using System.Xml.Linq;
using Adminbot.Services.AppleMobileConfig;
using Xunit;

public sealed class AppleMobileConfigGeneratorTests
{
    [Theory]
    [InlineData(IpProtocolMode.IPv4, 1)]
    [InlineData(IpProtocolMode.IPv6, 2)]
    [InlineData(IpProtocolMode.IPv4AndIPv6, 3)]
    public void Protocol_modes_emit_expected_masks_everywhere(IpProtocolMode protocol, int expectedMask)
    {
        var document = GenerateAndParse(new ApnProfileOptions { Apn = "test", Protocol = protocol });
        var cellular = GetCellular(document);
        var apn = GetDict(GetValue(cellular, "APNs"));
        var attach = GetDict(GetValue(cellular, "AttachAPN"));

        Assert.Equal(expectedMask.ToString(), GetValue(apn, "AllowedProtocolMask").Value);
        Assert.Equal(expectedMask.ToString(), GetValue(attach, "AllowedProtocolMask").Value);
        Assert.Equal(expectedMask.ToString(), GetValue(apn, "AllowedProtocolMaskInDomesticRoaming").Value);
        Assert.Equal(expectedMask.ToString(), GetValue(apn, "AllowedProtocolMaskInRoaming").Value);
    }

    [Fact]
    public void Generated_profile_has_valid_root_payload_and_distinct_uuids()
    {
        var bytes = new AppleMobileConfigGenerator().GenerateApnProfile(new ApnProfileOptions { Apn = "test" });
        var utf8 = new UTF8Encoding(false, true).GetString(bytes);
        var document = XDocument.Parse(utf8, LoadOptions.PreserveWhitespace);
        var root = document.Root!.Element("dict")!;
        var cellular = GetCellular(document);

        Assert.Equal("plist", document.Root!.Name.LocalName);
        Assert.Equal("1.0", document.Root.Attribute("version")!.Value);
        Assert.Equal("Configuration", GetValue(root, "PayloadType").Value);
        Assert.Equal("com.apple.cellular", GetValue(cellular, "PayloadType").Value);
        Assert.Equal("test", GetValue(GetDict(GetValue(cellular, "APNs")), "Name").Value);
        Assert.Equal("test", GetValue(GetDict(GetValue(cellular, "AttachAPN")), "Name").Value);
        Assert.Equal("PAP", GetValue(GetDict(GetValue(cellular, "APNs")), "AuthenticationType").Value);

        var rootUuid = Guid.Parse(GetValue(root, "PayloadUUID").Value);
        var childUuid = Guid.Parse(GetValue(cellular, "PayloadUUID").Value);
        Assert.NotEqual(rootUuid, childUuid);
        Assert.NotEmpty(GetValue(root, "PayloadIdentifier").Value);
        Assert.NotEmpty(GetValue(cellular, "PayloadIdentifier").Value);
    }

    [Fact]
    public void User_values_are_xml_escaped_and_round_trip_exactly()
    {
        const string apn = "test&demo";
        const string username = "abc<def";
        const string password = "p&<>\"'";

        var document = GenerateAndParse(new ApnProfileOptions
        {
            Apn = apn,
            Username = username,
            Password = password,
            DisplayName = "A&B <Test>"
        });
        var cellular = GetCellular(document);
        var apnDict = GetDict(GetValue(cellular, "APNs"));
        var attach = GetDict(GetValue(cellular, "AttachAPN"));

        Assert.Equal(apn, GetValue(apnDict, "Name").Value);
        Assert.Equal(username, GetValue(apnDict, "Username").Value);
        Assert.Equal(password, GetValue(apnDict, "Password").Value);
        Assert.Equal(username, GetValue(attach, "Username").Value);
        Assert.Equal(password, GetValue(attach, "Password").Value);
        Assert.Equal("A&B <Test>", GetValue(cellular, "PayloadDisplayName").Value);
    }

    [Fact]
    public void Chap_authentication_is_supported_on_apns_and_attach_apn()
    {
        var document = GenerateAndParse(new ApnProfileOptions
        {
            Apn = "test",
            AuthenticationType = ApnAuthenticationType.Chap
        });
        var cellular = GetCellular(document);

        Assert.Equal("CHAP", GetValue(GetDict(GetValue(cellular, "APNs")), "AuthenticationType").Value);
        Assert.Equal("CHAP", GetValue(GetDict(GetValue(cellular, "AttachAPN")), "AuthenticationType").Value);
    }

    [Fact]
    public void Optional_credentials_are_omitted_when_empty()
    {
        var document = GenerateAndParse(new ApnProfileOptions
        {
            Apn = "test",
            Username = null,
            Password = ""
        });
        var cellular = GetCellular(document);

        foreach (var dict in new[]
                 {
                     GetDict(GetValue(cellular, "APNs")),
                     GetDict(GetValue(cellular, "AttachAPN"))
                 })
        {
            Assert.Null(TryGetValue(dict, "Username"));
            Assert.Null(TryGetValue(dict, "Password"));
        }
    }

    [Fact]
    public void Xlat464_is_opt_in_and_only_emitted_on_apns_item()
    {
        var without = GetCellular(GenerateAndParse(new ApnProfileOptions { Apn = "test" }));
        Assert.Null(TryGetValue(GetDict(GetValue(without, "APNs")), "EnableXLAT464"));

        var enabled = GetCellular(GenerateAndParse(new ApnProfileOptions { Apn = "test", EnableXlat464 = true }));
        Assert.Equal("true", GetValue(GetDict(GetValue(enabled, "APNs")), "EnableXLAT464").Name.LocalName);
        Assert.Null(TryGetValue(GetDict(GetValue(enabled, "AttachAPN")), "EnableXLAT464"));
    }

    [Fact]
    public void Validation_errors_never_echo_password_content()
    {
        const string secret = "S3CR3T-DO-NOT-LEAK";
        var generator = new AppleMobileConfigGenerator();

        var invalidProtocol = Assert.Throws<ArgumentOutOfRangeException>(() =>
            generator.GenerateApnProfile(new ApnProfileOptions
            {
                Apn = "test",
                Password = secret,
                Protocol = (IpProtocolMode)999
            }));
        Assert.DoesNotContain(secret, invalidProtocol.ToString(), StringComparison.Ordinal);

        var invalidXmlPassword = Assert.Throws<ArgumentException>(() =>
            generator.GenerateApnProfile(new ApnProfileOptions
            {
                Apn = "test",
                Password = secret + "\u0001"
            }));
        Assert.DoesNotContain(secret, invalidXmlPassword.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Roaming_masks_are_omitted_when_configuration_is_disabled()
    {
        var cellular = GetCellular(GenerateAndParse(new ApnProfileOptions
        {
            Apn = "test",
            ConfigureRoamingProtocol = false
        }));
        var apn = GetDict(GetValue(cellular, "APNs"));

        Assert.Null(TryGetValue(apn, "AllowedProtocolMaskInDomesticRoaming"));
        Assert.Null(TryGetValue(apn, "AllowedProtocolMaskInRoaming"));
        Assert.Equal("3", GetValue(apn, "AllowedProtocolMask").Value);
    }

    [Fact]
    public void Every_generation_uses_fresh_root_and_cellular_uuids()
    {
        var first = GenerateAndParse(new ApnProfileOptions { Apn = "test" });
        var second = GenerateAndParse(new ApnProfileOptions { Apn = "test" });

        var firstRoot = first.Root!.Element("dict")!;
        var secondRoot = second.Root!.Element("dict")!;
        var firstRootUuid = Guid.Parse(GetValue(firstRoot, "PayloadUUID").Value);
        var secondRootUuid = Guid.Parse(GetValue(secondRoot, "PayloadUUID").Value);
        var firstChildUuid = Guid.Parse(GetValue(GetCellular(first), "PayloadUUID").Value);
        var secondChildUuid = Guid.Parse(GetValue(GetCellular(second), "PayloadUUID").Value);

        Assert.NotEqual(firstRootUuid, secondRootUuid);
        Assert.NotEqual(firstChildUuid, secondChildUuid);
        Assert.NotEqual(firstRootUuid, firstChildUuid);
        Assert.NotEqual(secondRootUuid, secondChildUuid);
    }

    [Fact]
    public void Manual_dual_stack_example_contains_apns_and_attach_mask_three()
    {
        var document = GenerateAndParse(new ApnProfileOptions
        {
            Apn = "test",
            AuthenticationType = ApnAuthenticationType.Pap,
            Protocol = IpProtocolMode.IPv4AndIPv6
        });
        var cellular = GetCellular(document);
        Assert.Equal("3", GetValue(GetDict(GetValue(cellular, "APNs")), "AllowedProtocolMask").Value);
        Assert.Equal("3", GetValue(GetDict(GetValue(cellular, "AttachAPN")), "AllowedProtocolMask").Value);
    }

    private static XDocument GenerateAndParse(ApnProfileOptions options)
    {
        var bytes = new AppleMobileConfigGenerator().GenerateApnProfile(options);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
    }

    private static XElement GetCellular(XDocument document)
    {
        var root = document.Root!.Element("dict")!;
        var content = GetValue(root, "PayloadContent");
        return content.Elements("dict").Single();
    }

    private static XElement GetDict(XElement value)
        => value.Name.LocalName == "array" ? value.Elements("dict").Single() : value;

    private static XElement GetValue(XElement dictionary, string key)
        => TryGetValue(dictionary, key) ?? throw new Xunit.Sdk.XunitException($"Missing plist key: {key}");

    private static XElement? TryGetValue(XElement dictionary, string key)
    {
        var elements = dictionary.Elements().ToList();
        for (var index = 0; index < elements.Count - 1; index++)
        {
            if (elements[index].Name.LocalName == "key" && elements[index].Value == key)
                return elements[index + 1];
        }
        return null;
    }
}
