using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class KeyReactionOverEffectTests
{
    private static KeyboardGeometry Geometry() =>
        KeyboardGeometry.Build([0f, 0.5f, 1f], [0f, 0f, 0f], ["A", "B", "C"], null);

    private static KeyboardGeometry GenericZoneGeometry()
    {
        var core = K55CoreReactionLayout.Create(K55CoreReactionTests.Core(), 0, 10)!.BuildGeometry("UK", null)!;
        return KeyboardGeometry.BuildMapped(core.X, core.Y, Enumerable.Repeat(true, 10).ToArray(),
            new Dictionary<string, int>(StringComparer.Ordinal) { ["Escape"] = 0, ["Number Pad Enter"] = 9 }, core.MaxX, core.MaxY);
    }

    private static KeyReaction Config(string effect = "fade", string color = "#000000", string background = "effect",
        float speed = 1f, float size = 1f) =>
        KeyReactionCatalog.Sanitize(new KeyReaction
        {
            Enabled = true, Effect = effect, Color = color, Background = background, Speed = speed, Size = size,
        });

    private static byte[] Frame(KeyReactionRenderer renderer, KeyboardGeometry geometry, KeyReaction config,
        double atMs, byte level = 200)
    {
        var frame = Enumerable.Repeat(level, geometry.LedCount * 3).ToArray();
        renderer.Render(geometry, config, atMs);
        renderer.Composite(frame, geometry.LedCount, config.Background);
        return frame;
    }

    private static byte[] Pixel(byte[] frame, int led) => frame[(led * 3)..(led * 3 + 3)];

    [Theory]
    [InlineData("#ffffff", 45, 45, 45)]
    [InlineData("#ff8040", 45, 23, 11)]
    [InlineData("#000000", 0, 0, 0)]
    public void Over_effect_dims_the_reaction_and_leaves_unaffected_keys_at_their_background(
        string color, byte red, byte green, byte blue)
    {
        var geometry = Geometry(); var config = Config(color: color);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);

        var frame = Frame(renderer, geometry, config, 0, 255);

        Assert.Equal(new[] { red, green, blue }, Pixel(frame, 0));
        Assert.Equal(new byte[] { 255, 255, 255 }, Pixel(frame, 2));
    }

    [Fact]
    public void Black_zone_fade_starts_off_and_gradually_returns_to_the_current_background()
    {
        var geometry = Geometry(); var config = Config(); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);

        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, 0), 0));
        Assert.Equal(new byte[] { 150, 150, 150 }, Pixel(Frame(renderer, geometry, config, 405), 0));
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(Frame(renderer, geometry, config, 730), 0));
        Assert.Equal(new byte[] { 80, 80, 80 }, Pixel(Frame(renderer, geometry, config, 780, 80), 0));
    }

    [Fact]
    public void Another_black_fade_press_restarts_the_darkening_without_stacking()
    {
        var geometry = Geometry(); var config = Config(); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);
        _ = Frame(renderer, geometry, config, 300);
        renderer.Press(geometry, 0, config, 300);

        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, 300), 0));
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(1f)]
    [InlineData(3f)]
    public void Black_fade_stays_fully_off_across_real_output_frames_at_every_speed(float speed)
    {
        var geometry = Geometry(); var config = Config(speed: speed);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);

        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, 33), 0));
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, 66), 0));
        Assert.Equal(new byte[] { 150, 150, 150 }, Pixel(Frame(renderer, geometry, config, 80 + 325 / speed), 0));
        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(Frame(renderer, geometry, config, 80 + 650 / speed), 0));
    }

    [Theory]
    [InlineData(0.25f, 0.5f)] [InlineData(0.25f, 1f)] [InlineData(0.25f, 3f)]
    [InlineData(1f, 0.5f)] [InlineData(1f, 1f)] [InlineData(1f, 3f)]
    [InlineData(3f, 0.5f)] [InlineData(3f, 1f)] [InlineData(3f, 3f)]
    public void Distant_black_ripple_holds_a_sampled_full_dark_crest_before_its_existing_tail(
        float speed, float size)
    {
        var geometry = GenericZoneGeometry();
        var config = Config("ripple", speed: speed, size: size); var renderer = new KeyReactionRenderer(seed: 1);
        var start = geometry.Resolve("Escape"); var target = geometry.Resolve("Number Pad Enter");
        var velocity = 0.018f * speed;
        var front = (geometry.X[target] - geometry.X[start]) / velocity;
        var width = 1.6f * size;
        renderer.Press(geometry, start, config, 0);

        Assert.InRange(Pixel(Frame(renderer, geometry, config, front - width / (2 * velocity)), target)[0], 99, 100);
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, front + 33), target));
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, front + 66), target));
        Assert.InRange(Pixel(Frame(renderer, geometry, config, front + 80 + width / (2 * velocity)), target)[0], 99, 100);
        Assert.Equal(new byte[] { 200, 200, 200 },
            Pixel(Frame(renderer, geometry, config, front + 80 + width / velocity + 1), target));
    }

    [Fact]
    public void Distant_black_ripple_peak_fully_dulls_a_generic_mapped_zone_then_returns_to_the_background()
    {
        var geometry = GenericZoneGeometry();
        var config = Config("ripple"); var renderer = new KeyReactionRenderer(seed: 1);
        var start = geometry.Resolve("Escape"); var target = geometry.Resolve("Number Pad Enter");
        var distance = geometry.X[target] - geometry.X[start];
        renderer.Press(geometry, start, config, 0);

        Assert.Equal(new byte[] { 200, 200, 200 }, Pixel(Frame(renderer, geometry, config, 0), target));
        Assert.Equal(new byte[] { 0, 0, 0 }, Pixel(Frame(renderer, geometry, config, distance / 0.018f), target));
        Assert.Equal(new byte[] { 200, 200, 200 },
            Pixel(Frame(renderer, geometry, config, (distance + 1.6f) / 0.018f + 81), target));
    }

    [Fact]
    public void Overlapping_coloured_ripples_cannot_build_a_bright_reaction_over_the_effect()
    {
        var geometry = Geometry(); var config = Config("ripple", "#ff0000");
        var renderer = new KeyReactionRenderer(seed: 1);
        for (var i = 0; i < 64; i++) renderer.Press(geometry, 0, config, 0);

        Assert.Equal(new byte[] { 45, 0, 0 }, Pixel(Frame(renderer, geometry, config, 0), 0));
    }

    [Theory]
    [InlineData("dim", 255, 0, 0, 90, 27, 27)]
    [InlineData("dark", 255, 0, 0, 63, 0, 0)]
    [InlineData("reveal", 200, 200, 200, 50, 50, 50)]
    public void Other_backgrounds_keep_their_existing_fade_colours(string background,
        byte startRed, byte startGreen, byte startBlue, byte midRed, byte midGreen, byte midBlue)
    {
        var geometry = Geometry(); var config = Config(color: "#ff0000", background: background);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);

        Assert.Equal(new[] { startRed, startGreen, startBlue }, Pixel(Frame(renderer, geometry, config, 0), 0));
        Assert.Equal(new[] { midRed, midGreen, midBlue }, Pixel(Frame(renderer, geometry, config, 325), 0));
    }

    [Theory]
    [InlineData("dim", 133, 20, 20)]
    [InlineData("dark", 113, 0, 0)]
    [InlineData("reveal", 88, 88, 88)]
    public void Other_backgrounds_keep_their_existing_ripple_distance_fade(
        string background, byte red, byte green, byte blue)
    {
        var geometry = Geometry(); var config = Config("ripple", "#ff0000", background);
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geometry, 0, config, 0);
        var distance = geometry.X[2] - geometry.X[0];

        Assert.Equal(new[] { red, green, blue }, Pixel(Frame(renderer, geometry, config, distance / 0.018f), 2));
    }

    [Fact]
    public void K55_live_overlay_darkens_only_the_mapped_zone_for_a_black_fade()
    {
        var frame = K55CoreReactionTests.Frame(); var overlay = new KeyReactiveOverlay();
        var config = new KeyReaction
        {
            Enabled = true, Effect = "fade", Color = "#000000", Background = "effect", KeyboardLayout = "UK",
        };
        overlay.Set(frame, config); frame.Fill(255, 255, 255);
        var now = Environment.TickCount64;
        overlay.PressKey("Number Pad Enter");

        Assert.True(overlay.Apply([frame], null, now));
        Assert.Equal(new byte[] { 0, 0, 0 }, frame.PaintBuffer[27..30].ToArray());
        Assert.Equal(new byte[] { 255, 255, 255 }, frame.PaintBuffer[..3].ToArray());
    }
}
