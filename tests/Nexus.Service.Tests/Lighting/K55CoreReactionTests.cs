using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class K55CoreReactionTests
{
    internal static RgbDevice Core(string location = "HID: \\?\\hid#vid_1b1c&pid_1bfe&mi_01#fixture") => new()
    {
        Name = "Corsair K55 CORE RGB", Vendor = "Corsair", Type = 5, LedCount = 10, Location = location,
        Zones = [new RgbZone { Name = "Keyboard", LedCount = 10, ZoneType = 1 }],
    };

    internal static DeviceFrame Frame(K55CoreReactionLayout? layout = null, bool[]? disabled = null) => new(0, "core:card", layout?.LedCount ?? 10)
    {
        DeviceId = "core:device", Archetype = "keyboard", ReactionLayout = layout ?? K55CoreReactionLayout.Create(Core(), 0, 10),
        LedDisabled = disabled,
    };

    private static KeyReaction Config(string? layout = "UK", string effect = "fade") => new()
    {
        Enabled = true, Effect = effect, KeyboardLayout = layout, Color = "#ff0000", Background = "effect",
    };

    [Fact]
    public void Capability_matches_numeric_identity_and_real_slot_count_not_name()
    {
        Assert.NotNull(K55CoreReactionLayout.Create(Core(), 0, 10));
        Assert.NotNull(K55CoreReactionLayout.Create(Core("HID: VID_1B1C&PID_1BFE&MI_01"), 0, 10));
        Assert.NotNull(K55CoreReactionLayout.Create(Core("VID_1B1C&PID_1BFE"), 0, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core("xVID_1B1C&PID_1BFE"), 0, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core("HID: VID_1B1C&PID_1BFE0&MI_01"), 0, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core("HID: VID_1B1C&PID_1BA4&MI_01"), 0, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core("Corsair K55 CORE RGB"), 0, 10));
        var wrong = Core(); wrong.LedCount = 11;
        Assert.Null(K55CoreReactionLayout.Create(wrong, 0, 10));
        wrong = Core(); wrong.Type = 4;
        Assert.Null(K55CoreReactionLayout.Create(wrong, 0, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core(), -1, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core(), 1, 10));
        Assert.Null(K55CoreReactionLayout.Create(Core(), 0, 0));
    }

    [Theory]
    [InlineData("US", 104)] [InlineData("UK", 105)] [InlineData("BR", 106)] [InlineData("JP", 108)]
    public void Full_regional_key_membership_matches_the_shipped_manifest(string region, int keys)
    {
        Assert.Equal(keys, K55CoreReactionLayout.Create(Core(), 0, 10)!.BuildGeometry(region, null)!.NamedKeys);
    }

    [Fact]
    public void Normal_lighting_and_reactive_preview_share_full_size_zone_positions()
    {
        var device = Core();
        var (u, v) = LedUvComputer.ComputeDefaults(device);
        Assert.Equal(10, u.Length);
        Assert.Equal(0.6f / 21.5f, u[0]);
        Assert.Equal(20.9f / 21.5f, u[9]);
        Assert.All(v, coordinate => Assert.Equal(0.5f, coordinate));
        var geo = K55CoreReactionLayout.Create(device, 0, 10)!.BuildGeometry("UK", null)!;
        for (var i = 0; i < 10; i++) Assert.Equal(geo.X[i], u[i] * 21.5f, 4);
        var card = Assert.Single(OpenRgbZoneSupport.BuildCards([device], new NexusSettings(), true).Devices);
        Assert.Equal(10, card.LedCount);
        Assert.Equal("keyboard", card.Type);
        Assert.Contains("icue", card.ConflictAppIds);
    }

    [Theory]
    [InlineData("Escape", 0)] [InlineData("Q", 1)] [InlineData("T", 2)]
    [InlineData("Space", 3)] [InlineData("I", 4)] [InlineData("P", 5)]
    [InlineData("Enter", 6)] [InlineData("Up Arrow", 7)]
    [InlineData("Number Pad 1", 8)] [InlineData("Number Pad Enter", 9)]
    public void Uk_physical_keys_resolve_to_shipped_zone_map(string key, int physical)
    {
        var geo = K55CoreReactionLayout.Create(Core(), 0, 10)!.BuildGeometry("UK", null)!;
        Assert.Equal(physical, geo.Resolve(key));
        Assert.Equal(21.5f, geo.MaxX);
        Assert.Equal(5f, geo.MaxY);
    }

    [Theory]
    [InlineData("US", "\\", 6)] [InlineData("UK", "\\", 6)]
    [InlineData("UK", "\\ (ISO)", 0)] [InlineData("BR", "\\ (ISO)", 0)]
    [InlineData("JP", "\\ (ISO)", -1)] [InlineData("US", "\\ (ISO)", -1)]
    public void Region_is_explicit_and_unknown_keys_do_not_use_nearest_zone(string region, string key, int expected)
    {
        var layout = K55CoreReactionLayout.Create(Core(), 0, 10)!;
        var geo = layout.BuildGeometry(region, null)!;
        Assert.Equal(expected, geo.Resolve(key));
        Assert.Equal(-1, geo.Resolve("Media Play/Pause"));
        Assert.Equal(-1, geo.Resolve("F24"));
        Assert.Null(layout.BuildGeometry("DE", null));
        Assert.Null(layout.BuildGeometry(null, null));
    }

    [Fact]
    public void Partition_maps_physical_zone_to_local_index_and_disabled_zones_remain_off()
    {
        var layout = K55CoreReactionLayout.Create(Core(), 0, 3,
            [new ZoneSlice { Segment = 0, Start = 8, Count = 2 }, new ZoneSlice { Segment = 0, Start = 0, Count = 1 }])!;
        var geo = layout.BuildGeometry("UK", [false, true, false])!;
        Assert.Equal(0, geo.Resolve("Number Pad 1"));
        Assert.Equal(2, geo.Resolve("Escape"));
        Assert.Equal(-1, geo.Resolve("Q"));
        Assert.Equal(-1, geo.Resolve("Number Pad Enter"));
        Assert.False(geo.IsActive(1));
        Assert.Null(K55CoreReactionLayout.Create(Core(), 0, 2, [new ZoneSlice { Segment = 1, Count = 2 }]));
        Assert.Null(K55CoreReactionLayout.Create(Core(), 0, 2, [new ZoneSlice { Segment = 0, Start = 9, Count = 2 }]));
        Assert.Null(K55CoreReactionLayout.Create(Core(), 0, 2, [new ZoneSlice { Segment = 0, Count = 1 }]));
        Assert.Null(K55CoreReactionLayout.Create(Core(), 0, 2,
            [new ZoneSlice { Segment = 0, Count = 1 }, new ZoneSlice { Segment = 0, Count = 1 }]));
        var segmented = Core(); segmented.Zones = [new RgbZone { LedCount = 5 }, new RgbZone { LedCount = 5 }];
        var mutable = new ZoneSlice { Segment = 1, Start = 3, Count = 2 };
        var immutable = K55CoreReactionLayout.Create(segmented, 0, 2, [mutable])!;
        mutable.Start = 0;
        Assert.Equal(1, immutable.BuildGeometry("UK", null)!.Resolve("Number Pad Enter"));
        Assert.Equal(-1, immutable.BuildGeometry("UK", [true, true])!.Resolve("Number Pad Enter"));
    }

    [Fact]
    public void Only_explicit_core_capability_bypasses_generic_per_key_threshold()
    {
        Assert.True(KeyReactiveOverlay.IsKeyboard(Frame()));
        var unknown = new DeviceFrame(0, "other", 10) { Archetype = "keyboard", LedU = new float[10], LedV = new float[10] };
        Assert.False(KeyReactiveOverlay.IsKeyboard(unknown));
        Assert.Equal(40, KeyReactionCatalog.MinKeyLeds);
        Assert.Single(KeyReactiveOverlay.Keyboards([Frame()]));
    }

    [Fact]
    public void Effective_config_disables_unknown_layout_and_limits_imported_effects_without_rewriting_store()
    {
        var store = new InMemoryConfigStore(); var overlay = new KeyReactiveOverlay(store); var frame = Frame();
        overlay.Set("core:device", Config(null));
        Assert.False(overlay.Get(frame).Enabled);
        Assert.Null(KeyReactiveOverlay.GeometryOf(frame, overlay.Get(frame)));
        overlay.Set("core:device", Config("UK", "sparks"));
        Assert.Equal("ripple", overlay.Get(frame).Effect);
        Assert.Equal("sparks", store.Load().Lighting.KeyReactions["core:device"].Effect);
        Assert.Equal("UK", overlay.Get(frame).KeyboardLayout);
        Assert.Equal("ripple", overlay.Set(frame, Config("UK", "rowSweep")).Effect);
    }

    [Fact]
    public void Live_fade_lights_only_mapped_zone_and_layout_change_retires_old_reactions()
    {
        var overlay = new KeyReactiveOverlay(); var frame = Frame();
        overlay.Set(frame, Config()); frame.Fill(0, 40, 0);
        overlay.PressKey("Number Pad Enter");
        Assert.True(overlay.Apply([frame], null, Environment.TickCount64));
        Assert.InRange(frame.PaintBuffer[27], 36, 45);
        Assert.Equal(40, frame.PaintBuffer[1]);
        overlay.Set(frame, Config("US")); frame.Fill(0, 40, 0);
        overlay.Apply([frame], null, Environment.TickCount64);
        Assert.Equal(0, frame.PaintBuffer[27]);
        Assert.Equal(40, frame.PaintBuffer[28]);
    }

    [Fact]
    public void Unknown_layout_cannot_dark_out_normal_lighting_even_when_imported_as_enabled()
    {
        var overlay = new KeyReactiveOverlay(); var frame = Frame();
        overlay.Set("core:device", new KeyReaction { Enabled = true, KeyboardLayout = "DE", Background = "dark" });
        frame.Fill(1, 2, 3); overlay.PressKey("A");
        Assert.False(overlay.Apply([frame], null, Environment.TickCount64));
        Assert.False(overlay.PaintStandalone([frame], Environment.TickCount64));
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.PaintBuffer[..3]);
    }

    [Fact]
    public void Same_card_with_new_physical_projection_retires_old_reactions()
    {
        var overlay = new KeyReactiveOverlay();
        var right = Frame(K55CoreReactionLayout.Create(Core(), 8, 2));
        overlay.Set(right, Config());
        right.Fill(0, 40, 0); overlay.PressKey("Number Pad Enter");
        Assert.True(overlay.Apply([right], null, Environment.TickCount64));
        Assert.InRange(right.PaintBuffer[3], 36, 45);
        var left = Frame(K55CoreReactionLayout.Create(Core(), 0, 2));
        left.Fill(0, 40, 0);
        Assert.False(overlay.Apply([left], null, Environment.TickCount64));
        Assert.Equal(40, left.PaintBuffer[4]);
        overlay.PressKey("Escape");
        Assert.True(overlay.Apply([left], null, Environment.TickCount64));
        Assert.InRange(left.PaintBuffer[0], 36, 45);
    }

    [Fact]
    public void Unchanged_controller_refresh_preserves_a_live_reaction_but_real_disabled_changes_retire_it()
    {
        var device = Core(); var frame = Frame(); var settings = new NexusSettings();
        var initial = Nexus.Service.Lighting.Mappings.LedLayoutResolver.ResolveOpenRgb(device, -1, frame.Id, settings);
        Nexus.Service.Lighting.Mappings.LedLayoutResolver.ApplyToFrame(frame, initial);
        var overlay = new KeyReactiveOverlay(); overlay.Set(frame, Config());
        frame.Fill(0, 40, 0); overlay.PressKey("Q");
        Assert.True(overlay.Apply([frame], null, Environment.TickCount64));
        var refreshed = Nexus.Service.Lighting.Mappings.LedLayoutResolver.ResolveOpenRgb(device, -1, frame.Id, settings);
        RgbBridge.RefreshCoreLayout(frame, K55CoreReactionLayout.Create(device, 0, 10), refreshed);
        frame.Fill(0, 40, 0);
        Assert.True(overlay.Apply([frame], null, Environment.TickCount64));
        Assert.InRange(frame.PaintBuffer[3], 36, 45);
        refreshed.Disabled = [false, true, false, false, false, false, false, false, false, false];
        RgbBridge.RefreshCoreLayout(frame, K55CoreReactionLayout.Create(device, 0, 10), refreshed);
        frame.Fill(0, 40, 0);
        Assert.False(overlay.Apply([frame], null, Environment.TickCount64));
    }

    [Fact]
    public void Known_core_demand_tracks_effective_layout_connection_and_hardware_source_without_settings_writes()
    {
        var store = new InMemoryConfigStore(); var writes = 0;
        store.OnChanged += () => writes++;
        var overlay = new KeyReactiveOverlay(store);
        var engine = new LightingEngine { KeyReactive = overlay };
        var frame = Frame();
        store.Update(s => s.Lighting.KeyReactions["core:device"] = Config(null));
        engine.UpdateDevices([frame]);
        Assert.False(overlay.Armed);
        Assert.False(overlay.IsEnabled("core:device"));
        Assert.False(overlay.IsEnabled(frame));
        Assert.Equal(1, writes);
        store.Update(s => s.Lighting.KeyReactions["core:device"] = Config("UK"));
        Assert.True(overlay.Armed);
        Assert.True(overlay.IsEnabled("core:device"));
        Assert.True(overlay.IsEnabled(frame));
        overlay.SetHardwareKeySource("core:device", true);
        Assert.False(overlay.Armed);
        overlay.SetHardwareKeySource("core:device", false);
        Assert.True(overlay.Armed);
        engine.UpdateDevices([]);
        Assert.False(overlay.Armed);
        Assert.False(overlay.IsEnabled("core:device"));
        engine.UpdateDevices([frame]);
        Assert.True(overlay.Armed);
        Assert.Equal(2, writes);
        overlay.Set("legacy:board", Config(null));
        engine.UpdateDevices([]);
        Assert.True(overlay.Armed);
        Assert.True(overlay.IsEnabled("legacy:board"));
    }

    [Fact]
    public void Profile_change_retires_old_queued_presses_and_live_reactions()
    {
        var store = new InMemoryConfigStore(); var overlay = new KeyReactiveOverlay(store); var frame = Frame();
        var engine = new LightingEngine { KeyReactive = overlay }; engine.UpdateDevices([frame]);
        overlay.Set(frame, Config());
        frame.Fill(0, 40, 0); overlay.PressKey("Q");
        Assert.True(overlay.Apply([frame], null, Environment.TickCount64));
        overlay.PressKey("P");
        store.Update(s => s.AudioTriggerControlEpoch = Guid.NewGuid().ToString("N"));
        frame.Fill(0, 40, 0);
        Assert.False(overlay.Apply([frame], null, Environment.TickCount64));
        Assert.Equal(40, frame.PaintBuffer[4]);
        Assert.Equal(40, frame.PaintBuffer[16]);
        var currentEpoch = store.Load().AudioTriggerControlEpoch;
        overlay.PressOn(frame.Id, "P", expectedEpoch: new string('a', 32));
        Assert.False(overlay.Apply([frame], null, Environment.TickCount64));
        overlay.PressOn(frame.Id, "P", expectedEpoch: currentEpoch);
        Assert.True(overlay.Apply([frame], null, Environment.TickCount64));
    }

    [Fact]
    public void Queued_simulated_index_does_not_move_to_a_new_same_card_projection()
    {
        var overlay = new KeyReactiveOverlay(); var engine = new LightingEngine { KeyReactive = overlay };
        var before = Frame(K55CoreReactionLayout.Create(Core(), 0, 2));
        engine.UpdateDevices([before]); overlay.Set(before, Config());
        overlay.PressOn(before.Id, key: null, led: 0);
        var after = Frame(K55CoreReactionLayout.Create(Core(), 8, 2));
        engine.UpdateDevices([after]); after.Fill(0, 40, 0);
        Assert.False(overlay.Apply([after], null, Environment.TickCount64));
        Assert.Equal(40, after.PaintBuffer[1]);
        overlay.PressKey("Number Pad 1");
        Assert.True(overlay.Apply([after], null, Environment.TickCount64));
        Assert.InRange(after.PaintBuffer[0], 36, 45);
    }

    [Fact]
    public void Queued_simulated_index_is_retired_by_disabled_mask_change_but_os_keys_use_current_geometry()
    {
        var overlay = new KeyReactiveOverlay(); var engine = new LightingEngine { KeyReactive = overlay };
        var before = Frame(); engine.UpdateDevices([before]); overlay.Set(before, Config());
        overlay.PressOn(before.Id, key: null, led: 1);
        var after = Frame(disabled: [true, false, false, false, false, false, false, false, false, false]);
        engine.UpdateDevices([after]); after.Fill(0, 40, 0);
        Assert.False(overlay.Apply([after], null, Environment.TickCount64));
        overlay.PressKey("Q");
        Assert.True(overlay.Apply([after], null, Environment.TickCount64));
        Assert.InRange(after.PaintBuffer[3], 36, 45);
    }
}
