using System;
using System.Text;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215DeviceInfoTests
{
    private const string ValidData = """
        "model":"D215-NOR-JD9365-6.67inch-hor","version":"3.4","brightness":80,"diplay_on":false
        """;

    [Theory]
    [InlineData("")]
    [InlineData("\"status\":200,")]
    public void TryParse_accepts_the_tested_identity_with_optional_success_status(string status)
    {
        var json = Encoding.UTF8.GetBytes($$"""{ {{status}} "cmd":"info", "data":{ {{ValidData}} } }""");

        Assert.True(D215DeviceInfo.TryParse(json, out var info));
        Assert.NotNull(info);
        Assert.True(info.IsSupported);
        Assert.Equal("D215-NOR-JD9365-6.67inch-hor", info.Model);
        Assert.Equal("3.4", info.Firmware);
        Assert.Equal(80, info.Brightness);
        Assert.False(info.DisplayOn);
    }

    [Theory]
    [InlineData("D215-OTHER", "3.4")]
    [InlineData("D215-NOR-JD9365-6.67inch-hor", "3.6")]
    public void TryParse_distinguishes_other_models_and_firmware_from_invalid_replies(string model, string firmware)
    {
        var json = Encoding.UTF8.GetBytes($$"""
            {"cmd":"info","data":{"model":"{{model}}","version":"{{firmware}}","brightness":100,"diplay_on":true} }
            """);

        Assert.True(D215DeviceInfo.TryParse(json, out var info));
        Assert.NotNull(info);
        Assert.False(info.IsSupported);
        Assert.True(info.DisplayOn);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"cmd\":\"live\",\"data\":{%DATA%}}")]
    [InlineData("{\"status\":500,\"cmd\":\"info\",\"data\":{%DATA%}}")]
    [InlineData("{\"status\":\"200\",\"cmd\":\"info\",\"data\":{%DATA%}}")]
    [InlineData("{\"cmd\":\"info\",\"data\":[]}")]
    [InlineData("{\"cmd\":\"info\",\"data\":{}}")]
    [InlineData("{\"cmd\":\"info\",\"data\":{%DATA%,\"version\":\"3.6\"}}")]
    [InlineData("{\"cmd\":\"info\",\"data\":{%DATA%}")]
    [InlineData("{\"cmd\":\"info\",\"data\":{%DATA%}} []")]
    public void TryParse_rejects_an_invalid_reply(string template)
    {
        var json = Encoding.UTF8.GetBytes(template.Replace("%DATA%", ValidData, StringComparison.Ordinal));

        Assert.False(D215DeviceInfo.TryParse(json, out var info));
        Assert.Null(info);
    }

    [Theory]
    [InlineData("\"brightness\":80", "\"brightness\":101")]
    [InlineData("\"brightness\":80", "\"brightness\":\"80\"")]
    [InlineData("\"diplay_on\":false", "\"display_on\":false")]
    [InlineData("\"diplay_on\":false", "\"diplay_on\":0")]
    [InlineData("\"version\":\"3.4\"", "\"version\":\"\\ud800\"")]
    public void TryParse_rejects_invalid_info_fields(string original, string replacement)
    {
        var data = ValidData.Replace(original, replacement, StringComparison.Ordinal);
        var json = Encoding.UTF8.GetBytes($$"""{ "cmd":"info", "data":{ {{data}} } }""");

        Assert.False(D215DeviceInfo.TryParse(json, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void TryParse_rejects_invalid_utf8_even_in_an_unknown_field()
    {
        var json = Encoding.UTF8.GetBytes($$"""
            { "cmd":"info", "ignored":"!", "data":{ {{ValidData}} } }
            """);
        json[Array.IndexOf(json, (byte)'!')] = 0xFF;

        Assert.False(D215DeviceInfo.TryParse(json, out _));
    }

    [Fact]
    public void TryParse_rejects_an_otherwise_valid_payload_over_the_frame_limit()
    {
        var padding = new string('x', 4096);
        var json = Encoding.UTF8.GetBytes($$"""
            { "cmd":"info", "ignored":"{{padding}}", "data":{ {{ValidData}} } }
            """);

        Assert.False(D215DeviceInfo.TryParse(json, out _));
    }
}
