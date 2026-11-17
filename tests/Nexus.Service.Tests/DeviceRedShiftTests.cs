using System.Text.Json;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests;

public sealed class DeviceRedShiftTests
{
    private static DeviceColorAdjust Enabled(string extra = "") => DeviceColorAdjust.For(
        JsonSerializer.Deserialize("{\"redShift\":true" + extra + "}",
            PersistenceJsonContext.Default.LightingDevicePreference));

    [Theory]
    [InlineData(0x922500)]
    [InlineData(0x5C1700)]
    [InlineData(0xEBADFF)]
    [InlineData(0xDC74FF)]
    [InlineData(0xFFADEB)]
    [InlineData(0xFF74DC)]
    [InlineData(0xFF3ACE)]
    [InlineData(0xFF00BF)]
    [InlineData(0xC80096)]
    [InlineData(0x92006E)]
    [InlineData(0x5C0045)]
    [InlineData(0xFFADC2)]
    [InlineData(0xFF7497)]
    [InlineData(0xFF3A6B)]
    [InlineData(0xFF0040)]
    [InlineData(0xC80032)]
    [InlineData(0x920025)]
    [InlineData(0x5C0017)]
    [InlineData(0xFFD5BF)] // Peach outside the supplied examples.
    [InlineData(0xFFFEFF)] // Almost-white pink must not retain a blue channel.
    [InlineData(0x010000)] // Dark red is promoted to the requested saturated red.
    [InlineData(0xFF0000)]
    [InlineData(0x8000FF)] // Inside the violet edge, 270 degrees.
    [InlineData(0xFF7F00)] // Inside the peach edge, 30 degrees.
    public void Reddish_colours_are_exact_red(int rgb)
    {
        AssertColour(Enabled(), rgb, 0xFF0000);
    }

    [Theory]
    [InlineData(0x000000)]
    [InlineData(0x010101)]
    [InlineData(0xFFFFFF)]
    [InlineData(0x808080)]
    [InlineData(0x00FF00)]
    [InlineData(0x0000FF)]
    [InlineData(0x00FFFF)]
    [InlineData(0xFFFF00)]
    [InlineData(0xFF8000)] // Outside the peach edge.
    [InlineData(0x7F00FF)] // Outside the violet edge.
    public void Other_colours_and_neutrals_are_unchanged(int rgb)
    {
        AssertColour(Enabled(), rgb, rgb);
    }

    [Fact]
    public void Untouched_and_disabled_preferences_preserve_original_colours()
    {
        AssertColour(DeviceColorAdjust.For(new LightingDevicePreference()), 0xFF74DC, 0xFF74DC);
        var pref = JsonSerializer.Deserialize("{\"redShift\":false}", PersistenceJsonContext.Default.LightingDevicePreference);
        Assert.True(DeviceColorAdjust.For(pref).IsIdentity);
        Assert.False(Enabled().IsIdentity);
    }

    [Fact]
    public void Source_pink_cannot_be_desaturated_or_tuned_back_to_pink()
    {
        AssertColour(Enabled(",\"adjustSaturation\":0,\"adjustRed\":0.3,\"adjustBlue\":1.7"), 0xFF74DC, 0xFF0000);
    }

    [Fact]
    public void Pink_introduced_by_tuning_is_also_eliminated()
    {
        AssertColour(Enabled(",\"adjustRed\":1.7"), 0x7F00FF, 0xFF0000);
    }

    [Fact]
    public void Unrelated_tuned_colour_keeps_gain_headroom_before_brightness()
    {
        Enabled(",\"adjustGreen\":1.7").Apply(0, 200, 0, 0.5, out var r, out var g, out var b);
        Assert.Equal(0, r);
        Assert.Equal(170, g);
        Assert.Equal(0, b);
    }

    [Theory]
    [InlineData(0.5, 127, 0, 0)]
    [InlineData(1.0, 255, 255, 255)]
    [InlineData(0.0, 0, 0, 0)]
    public void Actual_dimmed_tuned_output_determines_pink_conversion(double multiplier, byte er, byte eg, byte eb)
    {
        Enabled(",\"adjustRed\":1.7,\"adjustGreen\":1.2,\"adjustBlue\":1.4")
            .Apply(255, 255, 255, multiplier, out var r, out var g, out var b);
        Assert.Equal(er, r);
        Assert.Equal(eg, g);
        Assert.Equal(eb, b);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 127)]
    [InlineData(1.0, 255)]
    public void Brightness_and_off_still_apply_to_forced_red(double multiplier, byte expected)
    {
        Enabled().Apply(255, 116, 220, multiplier, out var r, out var g, out var b);
        Assert.Equal(expected, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Theory]
    [InlineData("RGB", 0xFF0000)]
    [InlineData("RBG", 0xFF0000)]
    [InlineData("GRB", 0x00FF00)]
    [InlineData("GBR", 0x0000FF)]
    [InlineData("BRG", 0x00FF00)]
    [InlineData("BGR", 0x0000FF)]
    public void Logical_red_retains_wire_channel_order(string order, int expected)
    {
        AssertColour(Enabled(",\"colorOrder\":\"" + order + "\""), 0xFF74DC, expected);
    }

    [Fact]
    public void Native_persistence_round_trip_preserves_the_setting()
    {
        var pref = JsonSerializer.Deserialize("{\"redShift\":true}", PersistenceJsonContext.Default.LightingDevicePreference)!;
        var saved = JsonSerializer.Serialize(pref, PersistenceJsonContext.Default.LightingDevicePreference);
        using var doc = JsonDocument.Parse(saved);
        Assert.True(doc.RootElement.GetProperty("redShift").GetBoolean());
    }

    private static void AssertColour(DeviceColorAdjust adjust, int source, int expected)
    {
        adjust.Apply((byte)(source >> 16), (byte)(source >> 8), (byte)source, 1, out var r, out var g, out var b);
        Assert.Equal(expected, (r << 16) | (g << 8) | b);
    }
}
