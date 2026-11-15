using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class K55BlackNeighbourReactionTests
{
    private static KeyboardGeometry Core(bool[]? disabled = null) =>
        K55CoreReactionLayout.Create(K55CoreReactionTests.Core(), 0, 10)!.BuildGeometry("UK", disabled)!;

    private static KeyboardGeometry Projection(params int[] physical) =>
        K55CoreReactionLayout.Create(K55CoreReactionTests.Core(), 0, physical.Length,
            physical.Select(index => new ZoneSlice { Segment = 0, Start = index, Count = 1 }).ToArray())!
            .BuildGeometry("UK", null)!;

    private static KeyboardGeometry GenericMapped(KeyboardGeometry source) => KeyboardGeometry.BuildMapped(
        source.X, source.Y, Enumerable.Range(0, source.LedCount).Select(source.IsActive).ToArray(),
        new Dictionary<string, int>(StringComparer.Ordinal), source.MaxX, source.MaxY);

    private static KeyReaction Config(string effect, string color = "#000000", string background = "effect",
        float speed = 1, float size = 1, string colorMode = "custom") => KeyReactionCatalog.Sanitize(new KeyReaction
        {
            Enabled = true, Effect = effect, Color = color, Background = background,
            Speed = speed, Size = size, ColorMode = colorMode,
        });

    private static byte[] Frame(KeyReactionRenderer renderer, KeyboardGeometry geometry, KeyReaction config, double at)
    {
        var frame = Enumerable.Repeat((byte)200, geometry.LedCount * 3).ToArray();
        renderer.Render(geometry, config, at);
        renderer.Composite(frame, geometry.LedCount, config.Background);
        return frame;
    }

    private static byte[] Pixel(byte[] frame, int led) => frame[(led * 3)..(led * 3 + 3)];

    [Theory]
    [InlineData("fade", 0)] [InlineData("fade", 1)] [InlineData("fade", 7)] [InlineData("fade", 9)]
    [InlineData("ripple", 0)] [InlineData("ripple", 1)] [InlineData("ripple", 7)] [InlineData("ripple", 9)]
    public void Black_Over_effect_starts_with_the_pressed_zone_and_its_physical_neighbours_fully_dark(
        string effect, int origin)
    {
        var geometry = Core(); var config = Config(effect); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, origin, config, 0);

        var frame = Frame(renderer, geometry, config, 0);

        for (var i = 0; i < 10; i++)
            Assert.Equal(Enumerable.Repeat((byte)(Math.Abs(i - origin) <= 1 ? 0 : 200), 3), Pixel(frame, i));
    }

    [Fact]
    public void Neighbouring_fade_zones_share_the_same_hold_reverse_fade_and_recovery()
    {
        var geometry = Core(); var config = Config("fade"); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 1, config, 0);

        foreach (var (at, expected) in new[] { (66, 0), (405, 150), (730, 200) })
        {
            var frame = Frame(renderer, geometry, config, at);
            foreach (var led in new[] { 0, 1, 2 })
                Assert.Equal(Enumerable.Repeat((byte)expected, 3), Pixel(frame, led));
            Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 3));
        }
    }

    [Theory]
    [InlineData(0.25f, 0.5f)] [InlineData(0.25f, 1f)] [InlineData(0.25f, 3f)]
    [InlineData(1f, 0.5f)] [InlineData(1f, 1f)] [InlineData(1f, 3f)]
    [InlineData(3f, 0.5f)] [InlineData(3f, 1f)] [InlineData(3f, 3f)]
    public void Ripple_widens_its_distant_crest_by_physical_adjacency_at_every_speed_and_size(float speed, float size)
    {
        var geometry = Core(); var config = Config("ripple", speed: speed, size: size);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);
        var front = (geometry.X[8] - geometry.X[0]) / (0.018f * speed);

        foreach (var offset in new[] { 33, 66 })
        {
            var frame = Frame(renderer, geometry, config, front + offset);
            foreach (var led in new[] { 7, 8, 9 }) Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(frame, led));
            Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 0));
        }
        var end = (geometry.MaxDistanceFrom(geometry.X[0], geometry.Y[0]) + 1.6f * size) / (0.018f * speed) + 81;
        Assert.All(Frame(renderer, geometry, config, end), value => Assert.Equal(200, value));
    }

    [Theory]
    [InlineData("fade", "#010000")] [InlineData("fade", "#ff0000")] [InlineData("fade", "#ffffff")]
    [InlineData("fade", "#000100")] [InlineData("fade", "#000001")]
    [InlineData("ripple", "#010000")] [InlineData("ripple", "#ff0000")] [InlineData("ripple", "#ffffff")]
    [InlineData("ripple", "#000100")] [InlineData("ripple", "#000001")]
    public void Nonblack_Over_reactions_retain_the_existing_mask(string effect, string color)
    {
        CompareWithGeneric(Config(effect, color));
    }

    [Theory]
    [InlineData("fade", "dark")] [InlineData("fade", "dim")] [InlineData("fade", "reveal")]
    [InlineData("ripple", "dark")] [InlineData("ripple", "dim")] [InlineData("ripple", "reveal")]
    public void Other_backgrounds_retain_the_existing_black_mask(string effect, string background)
    {
        CompareWithGeneric(Config(effect, background: background));
    }

    private static void CompareWithGeneric(KeyReaction config)
    {
        var core = Core(); var generic = GenericMapped(core);
        var a = new KeyReactionRenderer(seed: 1); var b = new KeyReactionRenderer(seed: 1);
        a.Press(core, 1, config, 0); b.Press(generic, 1, config, 0);
        foreach (var at in new[] { 0, 33, 66, 405, 730 })
            Assert.Equal(Frame(b, generic, config, at), Frame(a, core, config, at));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void Explicit_mapping_without_the_K55_capability_does_not_widen_black(string effect)
    {
        var geometry = GenericMapped(Core()); var config = Config(effect);
        var renderer = new KeyReactionRenderer(seed: 1); renderer.Press(geometry, 1, config, 0);
        var frame = Frame(renderer, geometry, config, 0);
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(frame, 1));
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 0));
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 2));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void Reordered_card_uses_physical_neighbours_instead_of_local_array_neighbours(string effect)
    {
        var geometry = Projection(8, 0, 9); var config = Config(effect);
        var renderer = new KeyReactionRenderer(seed: 1); renderer.Press(geometry, 0, config, 0);
        var frame = Frame(renderer, geometry, config, 0);
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(frame, 0));
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 1));
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(frame, 2));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void Missing_physical_zones_do_not_cause_widening_to_jump_a_card_slice_gap(string effect)
    {
        var geometry = Projection(0, 2); var config = Config(effect);
        var renderer = new KeyReactionRenderer(seed: 1); renderer.Press(geometry, 0, config, 0);
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(Frame(renderer, geometry, config, 0), 1));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void Disabled_neighbours_remain_untouched_and_do_not_bridge_to_another_physical_zone(string effect)
    {
        var geometry = Core([false, true, false, false, false, false, false, false, false, false]);
        var config = Config(effect); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);
        var frame = Frame(renderer, geometry, config, 66);
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 1));
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(frame, 2));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void Changing_current_colour_to_black_does_not_widen_a_captured_coloured_reaction(string effect)
    {
        var core = Core(); var generic = GenericMapped(core);
        var a = new KeyReactionRenderer(seed: 1); var b = new KeyReactionRenderer(seed: 1);
        var red = Config(effect, "#ff0000"); var black = Config(effect);
        a.Press(core, 1, red, 0); b.Press(generic, 1, red, 0);
        Assert.Equal(Frame(b, generic, black, 66), Frame(a, core, black, 66));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void A_captured_black_reaction_stays_black_when_current_colour_mode_changes_to_rainbow(string effect)
    {
        var geometry = Core(); var black = Config(effect);
        var renderer = new KeyReactionRenderer(seed: 1); renderer.Press(geometry, 1, black, 0);
        var rainbow = Config(effect, "#ffffff", colorMode: "rainbow");
        var frame = Frame(renderer, geometry, rainbow, 66);
        foreach (var led in new[] { 0, 1, 2 }) Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(frame, led));
    }

    [Theory]
    [InlineData("fade")] [InlineData("ripple")]
    public void Widening_cannot_touch_output_outside_the_ten_public_zones(string effect)
    {
        var geometry = Core(); var config = Config(effect); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 9, config, 0);
        var frame = Enumerable.Repeat((byte)97, 33).ToArray();
        renderer.Render(geometry, config, 0); renderer.Composite(frame, 10, config.Background);
        Assert.Equal(new byte[] { 97, 97, 97 }, frame[30..]);
    }
}
