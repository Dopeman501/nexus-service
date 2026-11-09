using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Strimer;

public class StrimerEffectLooksTests
{
    [Theory]
    [InlineData("ch0", "ch0")]
    [InlineData("ch1", "ch1")]
    [InlineData("p0", "p0")]
    [InlineData("p11", "p11")]
    [InlineData("p12", null)]
    [InlineData("p01", null)]
    [InlineData("ch2", null)]
    [InlineData("all", null)]
    [InlineData(null, null)]
    public void NormalizeTarget_accepts_only_cables_and_the_twelve_ports(string? input, string? expected)
    {
        Assert.Equal(expected, StrimerEffectLooks.NormalizeTarget(input));
    }

    [Fact]
    public void A_target_with_no_look_of_its_own_shows_its_cable_then_the_shared_effect()
    {
        var ls = new StrimerLightingSettings { Mode = "wave" };
        Assert.Equal("wave", StrimerEffectLooks.Resolve(ls, "p8").Mode.Key);

        ls.Targets["ch1"] = new StrimerTargetLook { Mode = "tide" };
        Assert.Equal("tide", StrimerEffectLooks.Resolve(ls, "p8").Mode.Key);
        Assert.Equal("wave", StrimerEffectLooks.Resolve(ls, "p2").Mode.Key);
    }

    [Fact]
    public void Seeding_copies_what_the_target_showed_including_its_speed_and_palette()
    {
        var ls = new StrimerLightingSettings { Mode = "mixing", Direction = 1, Brightness = 3 };
        ls.ModeColors["mixing"] = new List<string> { "#010203", "#040506" };
        var seeded = StrimerEffectLooks.Seed(ls, "ch0");

        Assert.Equal("mixing", seeded.Mode);
        Assert.Equal(1, seeded.Direction);
        Assert.Equal(3, seeded.Brightness);
        Assert.Equal(2, seeded.ModeSpeeds["mixing"]);
        Assert.Equal(new List<string> { "#010203", "#040506" }, seeded.ModeColors["mixing"]);
    }

    [Fact]
    public void The_shared_effect_shows_the_effect_mode_while_the_Lighting_page_drives_the_cable()
    {
        var ls = new StrimerLightingSettings { Mode = "custom", EffectMode = "drizzling" };
        var look = StrimerEffectLooks.Shared(ls);
        Assert.Equal("drizzling", look.Mode.Key);
        Assert.Equal(4, look.Speed);
        Assert.Equal(6, look.Colors.Length);
    }

    [Fact]
    public void Settings_saved_before_per_cable_effects_load_as_every_port_using_the_saved_effect()
    {
        var ls = System.Text.Json.JsonSerializer.Deserialize(
            "{\"mode\":\"breathing\",\"speed\":3,\"colors\":[\"#112233\"]}",
            Nexus.Service.Serialization.PersistenceJsonContext.Default.StrimerLightingSettings)!;
        Assert.Empty(ls.Targets);
        var plan = StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false));
        Assert.Equal(12, plan.Count);
        Assert.All(plan, z => Assert.Equal(0x02, z.Mode.EffectByte));
        Assert.Equal(0x11, plan[0].Colors[0]);
    }
}
