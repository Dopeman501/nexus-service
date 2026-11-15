using System.Text.Json;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public sealed class KeyReactionPaletteTests
{
    private static KeyReaction Config(params string[] colors) => new()
    {
        Enabled = true, KeyboardLayout = "UK", Effect = "fade", ColorMode = "custom", Color = "#aa5500",
        Colors = [.. colors], Background = "dark",
    };

    private static KeyboardGeometry Geometry() => KeyboardGeometry.BuildMapped(
        [0f, 2f, 4f], [0f, 0f, 0f], [true, true, true], new() { ["A"] = 0, ["B"] = 1, ["C"] = 2 }, 4f, 1f);

    private static byte[] Frame(KeyReactionRenderer renderer, KeyboardGeometry geo, KeyReaction cfg, double at, byte background = 0)
    {
        var rgb = Enumerable.Repeat(background, geo.LedCount * 3).ToArray();
        renderer.Render(geo, cfg, at);
        renderer.Composite(rgb, geo.LedCount, cfg.Background);
        return rgb;
    }

    private static byte[] PressPixel(KeyReactionRenderer renderer, KeyboardGeometry geo, KeyReaction cfg, double at, int led = 0)
    {
        renderer.Press(geo, led, cfg, at);
        return Frame(renderer, geo, cfg, at).Skip(led * 3).Take(3).ToArray();
    }

    [Fact]
    public void Sanitizer_normalizes_deduplicates_orders_and_copies_the_palette()
    {
        var input = Config("#AABBCC", "#000000", "#aabbcc", "bad", "# f0000", null!, "#112233");
        var clean = KeyReactionCatalog.Sanitize(input);
        Assert.Equal(new[] { "#aabbcc", "#000000", "#112233" }, clean.Colors);
        Assert.Equal("#aabbcc", clean.Color);
        Assert.NotSame(input.Colors, clean.Colors);
        Assert.Equal("#AABBCC", input.Colors![0]);
        clean.Colors![0] = "#ffffff";
        Assert.Equal("#AABBCC", input.Colors[0]);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("000000")] [InlineData("#fff")]
    [InlineData("#gg0000")] [InlineData("#0000000")] [InlineData(" #000000")] [InlineData("#00000 ")]
    public void Missing_empty_or_invalid_only_palette_preserves_legacy_colour(string? invalid)
    {
        var input = Config();
        input.Colors = invalid is null ? null : [invalid];
        var clean = KeyReactionCatalog.Sanitize(input);
        Assert.Null(clean.Colors);
        Assert.Equal("#aa5500", clean.Color);
        Assert.DoesNotContain("\"colors\"", JsonSerializer.Serialize(clean, AppJsonContext.Default.KeyReaction));
    }

    [Fact]
    public void Palette_limit_bounds_submitted_slots_without_mutating_the_input()
    {
        var input = Config(Enumerable.Range(0, 20).Select(i => $"#{i:x6}").ToArray());
        var clean = KeyReactionCatalog.Sanitize(input);
        Assert.Equal(16, clean.Colors!.Count);
        Assert.Equal("#00000f", clean.Colors[^1]);
        Assert.Equal(20, input.Colors!.Count);
        input.Colors = Enumerable.Repeat("invalid", 16).Append("#ff0000").ToList();
        Assert.Null(KeyReactionCatalog.Sanitize(input).Colors);
    }

    [Fact]
    public void Core_device_sanitization_preserves_an_independent_palette()
    {
        var input = Config("#000000", "#ff0000");
        var clean = KeyReactionCatalog.SanitizeForDevice(K55CoreReactionTests.Frame(), input);
        Assert.Equal(input.Colors, clean.Colors);
        Assert.NotSame(input.Colors, clean.Colors);
        Assert.Equal("#000000", clean.Color);
        Assert.True(clean.Enabled);
    }

    [Fact]
    public void Custom_presses_cycle_in_selection_order_and_wrap()
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00", "#0000ff"));
        var renderer = new KeyReactionRenderer(seed: 1);
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, cfg, 0));
        Assert.Equal(new byte[] { 0, 255, 0 }, PressPixel(renderer, geo, cfg, 1000));
        Assert.Equal(new byte[] { 0, 0, 255 }, PressPixel(renderer, geo, cfg, 2000));
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, cfg, 3000));
    }

    [Fact]
    public void Invalid_or_disabled_targets_do_not_consume_a_palette_colour()
    {
        var geo = KeyboardGeometry.BuildMapped([0f, 2f], [0f, 0f], [true, false], new() { ["A"] = 0, ["B"] = 1 }, 2f, 1f);
        var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00")); var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, -1, cfg, 0); renderer.Press(geo, 2, cfg, 0); renderer.Press(geo, 1, cfg, 0);
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, cfg, 0));
        Assert.Equal(new byte[] { 0, 255, 0 }, PressPixel(renderer, geo, cfg, 1000));
    }

    [Fact]
    public void Repeated_fade_retriggers_advance_the_colour_without_stacking()
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00")); var renderer = new KeyReactionRenderer(seed: 1);
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, cfg, 0));
        Assert.Equal(new byte[] { 0, 255, 0 }, PressPixel(renderer, geo, cfg, 10));
    }

    [Fact]
    public void Reset_and_semantic_palette_changes_restart_from_the_first_colour()
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00", "#0000ff")); var renderer = new KeyReactionRenderer(seed: 1);
        PressPixel(renderer, geo, cfg, 0);
        var equivalent = KeyReactionCatalog.Sanitize(cfg);
        Assert.Equal(new byte[] { 0, 255, 0 }, PressPixel(renderer, geo, equivalent, 1000));
        renderer.Reset();
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, equivalent, 2000));
        var changed = KeyReactionCatalog.Sanitize(Config("#0000ff", "#00ff00"));
        Assert.Equal(new byte[] { 0, 0, 255 }, PressPixel(renderer, geo, changed, 3000));
        // A changed list on the same configuration object must not evade detection.
        changed.Colors![0] = "#ffffff";
        Assert.Equal(new byte[] { 255, 255, 255 }, PressPixel(renderer, geo, changed, 4000));
    }

    [Theory]
    [InlineData("rainbow")] [InlineData("random")]
    public void Other_colour_modes_ignore_the_palette_and_custom_reentry_starts_first(string mode)
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00")); var renderer = new KeyReactionRenderer(seed: 7);
        PressPixel(renderer, geo, cfg, 0);
        cfg.ColorMode = mode;
        var control = new KeyReactionRenderer(seed: 7);
        var without = KeyReactionCatalog.Sanitize(cfg); without.Colors = null;
        Assert.Equal(PressPixel(control, geo, without, 1000), PressPixel(renderer, geo, cfg, 1000));
        cfg.ColorMode = "custom";
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, cfg, 2000));
    }

    [Fact]
    public void Legacy_single_colour_stays_single_and_does_not_gain_a_palette()
    {
        var geo = Geometry(); var cfg = Config(); cfg.Colors = null;
        var renderer = new KeyReactionRenderer(seed: 1);
        Assert.Equal(new byte[] { 170, 85, 0 }, PressPixel(renderer, geo, cfg, 0));
        Assert.Equal(new byte[] { 170, 85, 0 }, PressPixel(renderer, geo, cfg, 1000));
        Assert.Null(cfg.Colors);
    }

    [Fact]
    public void Card_slice_renderers_keep_independent_cycles_and_outside_keys_do_not_advance_them()
    {
        var left = K55CoreReactionLayout.Create(K55CoreReactionTests.Core(), 0, 2)!.BuildGeometry("UK", null)!;
        var right = K55CoreReactionLayout.Create(K55CoreReactionTests.Core(), 8, 2)!.BuildGeometry("UK", null)!;
        var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00"));
        var a = new KeyReactionRenderer(seed: 1); var b = new KeyReactionRenderer(seed: 1);
        a.Press(left, left.Resolve("Number Pad 1"), cfg, 0);
        b.Press(right, right.Resolve("Q"), cfg, 0);
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(a, left, cfg, 0, left.Resolve("Q")));
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(b, right, cfg, 0, right.Resolve("Number Pad 1")));
        Assert.Equal(new byte[] { 0, 255, 0 }, PressPixel(a, left, cfg, 1000, left.Resolve("Q")));
        Assert.Equal(new byte[] { 0, 255, 0 }, PressPixel(b, right, cfg, 1000, right.Resolve("Number Pad 1")));
    }

    [Fact]
    public void Black_entries_widen_only_their_own_core_over_effect_reaction()
    {
        var frame = K55CoreReactionTests.Frame(); var geo = KeyReactiveOverlay.GeometryOf(frame, Config())!;
        var cfg = KeyReactionCatalog.Sanitize(Config("#000000", "#ff0000")); cfg.Background = "effect";
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, 1, cfg, 0);
        var black = Frame(renderer, geo, cfg, 0, 255);
        foreach (var led in new[] { 0, 1, 2 }) Assert.Equal(new byte[] { 0, 0, 0 }, black.Skip(led * 3).Take(3));
        renderer.Press(geo, 1, cfg, 1000);
        var red = Frame(renderer, geo, cfg, 1000, 255);
        Assert.Equal(new byte[] { 45, 0, 0 }, red.Skip(3).Take(3));
        foreach (var led in new[] { 0, 2 }) Assert.Equal(new byte[] { 255, 255, 255 }, red.Skip(led * 3).Take(3));
    }

    [Fact]
    public void Mixed_palette_overlap_retains_older_coloured_light_while_black_widens_its_mask()
    {
        var geo = K55CoreReactionTests.Frame().ReactionLayout!.BuildGeometry("UK", null)!;
        var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#000000"));
        cfg.Effect = "ripple"; cfg.Background = "effect";
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, 1, cfg, 0);
        renderer.Press(geo, 1, cfg, 33);
        var frame = Frame(renderer, geo, cfg, 33, 255);
        Assert.True(frame[3] > 0); // Existing additive overlap retains the older red front.
        Assert.Equal(0, frame[4]); Assert.Equal(0, frame[5]);
        foreach (var led in new[] { 0, 2 })
        {
            Assert.InRange(frame[led * 3], 0, 44); // Red already reached these zones; black darkens their white base.
            Assert.Equal(0, frame[led * 3 + 1]); Assert.Equal(0, frame[led * 3 + 2]);
        }
    }

    [Fact]
    public void Each_reaction_keeps_its_captured_palette_colour_when_later_presses_advance()
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00"));
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, 0, cfg, 0); renderer.Press(geo, 2, cfg, 10);
        var frame = Frame(renderer, geo, cfg, 10);
        Assert.InRange(frame[0], 240, 254);
        Assert.Equal(0, frame[1]); Assert.Equal(0, frame[2]);
        Assert.Equal(new byte[] { 0, 255, 0 }, frame.Skip(6).Take(3));
    }

    [Fact]
    public void Explicit_palette_sparks_keep_the_selected_colour_including_black()
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#000000", "#00ff00")); cfg.Effect = "sparks";
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, 0, cfg, 0);
        Assert.All(Frame(renderer, geo, cfg, 150), value => Assert.Equal(0, value));
        renderer.Press(geo, 0, cfg, 1000);
        var green = Frame(renderer, geo, cfg, 1150);
        Assert.Contains(green.Where((_, i) => i % 3 == 1), v => v > 0);
        Assert.All(green.Where((_, i) => i % 3 != 1), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Heatmap_stays_thermal_and_does_not_consume_palette_steps()
    {
        var geo = Geometry(); var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00")); cfg.Effect = "heatmap";
        var renderer = new KeyReactionRenderer(seed: 1);
        renderer.Press(geo, 0, cfg, 0);
        var control = new KeyReactionRenderer(seed: 1); var legacy = KeyReactionCatalog.Sanitize(cfg); legacy.Colors = null;
        control.Press(geo, 0, legacy, 0);
        Assert.Equal(Frame(control, geo, legacy, 0), Frame(renderer, geo, cfg, 0));
        cfg.Effect = "fade";
        Assert.Equal(new byte[] { 255, 0, 0 }, PressPixel(renderer, geo, cfg, 1000));
    }

    [Theory]
    [InlineData("rainbow")] [InlineData("random")] [InlineData("custom")]
    public void Legacy_heatmap_keeps_colour_progression_when_switching_to_a_coloured_effect(string mode)
    {
        var geo = Geometry(); var cfg = Config(); cfg.Colors = null; cfg.ColorMode = mode; cfg.Effect = "heatmap";
        var renderer = new KeyReactionRenderer(seed: 7); var control = new KeyReactionRenderer(seed: 7);
        renderer.Press(geo, 0, cfg, 0);
        var fade = KeyReactionCatalog.Sanitize(cfg); fade.Effect = "fade";
        control.Press(geo, 0, fade, 0);
        Assert.Equal(PressPixel(control, geo, fade, 1000), PressPixel(renderer, geo, fade, 1000));
    }

    [Fact]
    public void Legacy_custom_sparks_keep_their_existing_satellite_variation()
    {
        var geo = Geometry(); var cfg = Config(); cfg.Colors = null; cfg.Color = "#000000"; cfg.Effect = "sparks";
        var renderer = new KeyReactionRenderer(seed: 1); renderer.Press(geo, 0, cfg, 0);
        Assert.Contains(Frame(renderer, geo, cfg, 150), value => value > 0);
    }

    [Fact]
    public void Source_generated_api_and_persistence_json_roundtrip_order_without_legacy_null_keys()
    {
        var cfg = KeyReactionCatalog.Sanitize(Config("#000000", "#ff0000"));
        var api = JsonSerializer.Serialize(cfg, AppJsonContext.Default.KeyReaction);
        Assert.Equal(cfg.Colors, JsonSerializer.Deserialize(api, AppJsonContext.Default.KeyReaction)!.Colors);
        var settings = new NexusSettings(); settings.Lighting.KeyReactions["fixture"] = cfg;
        var saved = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        Assert.Equal(cfg.Colors, JsonSerializer.Deserialize(saved, PersistenceJsonContext.Default.NexusSettings)!.Lighting.KeyReactions["fixture"].Colors);
        var export = new Nexus.Service.Models.Profiles.ProfileExport { Settings = settings };
        var profile = JsonSerializer.Serialize(export, PersistenceJsonContext.Default.ProfileExport);
        var copied = JsonSerializer.Deserialize(profile, PersistenceJsonContext.Default.ProfileExport)!.Settings!.Lighting.KeyReactions["fixture"];
        Assert.Equal(cfg.Colors, copied.Colors);
        Assert.NotSame(cfg.Colors, copied.Colors);
        cfg.Colors = null;
        Assert.DoesNotContain("\"colors\"", JsonSerializer.Serialize(cfg, AppJsonContext.Default.KeyReaction));
    }

    [Fact]
    public void Preview_is_deterministic_and_uses_multiple_palette_colours()
    {
        var geo = K55CoreReactionTests.Frame().ReactionLayout!.BuildGeometry("UK", null)!;
        var cfg = KeyReactionCatalog.Sanitize(Config("#ff0000", "#00ff00"));
        var preview = KeyReactionPreview.Render(geo, cfg);
        Assert.Equal(preview.Frames, KeyReactionPreview.Render(geo, cfg).Frames);
        var rgb = Convert.FromBase64String(preview.Frames);
        Assert.Contains(rgb.Where((_, i) => i % 3 == 0), v => v > 0);
        Assert.Contains(rgb.Where((_, i) => i % 3 == 1), v => v > 0);
        Assert.All(rgb.Where((_, i) => i % 3 == 2), value => Assert.Equal(0, value));
    }
}
