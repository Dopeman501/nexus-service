using System.Text.Json;
using Nexus.Service.Gallery;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Panel;

public sealed class D215PanelsPersistenceTests : IDisposable
{
    private const string AppId = "example.round";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-circle-state-" + Guid.NewGuid().ToString("N"));
    private readonly JsonConfigStore _store;

    public D215PanelsPersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static D215PanelDto Circle(string id = "circle-1", string type = "clock") => new()
    {
        Id = id, Type = type, X = 0.25, Y = 0.75, Diameter = 0.35,
        Config = new() { ["design"] = Json("\"analog\"") },
    };

    private static PanelLayoutDto Layout(params D215PanelDto[] circles) => new()
    {
        LayoutSchemaVersion = 2, Surface = "lcd-wide",
        Pages = [new() { Id = "p1", Widgets = [new() { Id = "base-clock", Type = "clock", Size = "4x2" }] }],
        WidgetPlaylist = new() { Enabled = true, Interval = 30, Types = ["clock", "weather"], Order = ["clock", "weather"] },
        D215Panels = new() { Enabled = true, Items = circles.ToList() },
    };

    private static PanelDeviceRecord Record(PanelLayoutDto layout, bool d215 = true) => new()
    {
        Id = "d215", DisplayName = "D215", Layout = layout,
        Capabilities = new() { Family = d215 ? "jungle-leopard-d215" : "other-lcd", Surface = "lcd-wide" },
    };

    [Fact]
    public void Source_generated_wire_and_settings_roundtrips_keep_independent_instances_and_the_legacy_playlist()
    {
        var composed = Layout(Circle(), Circle("circle-2", "monitoring"));
        composed.D215Widget = new() { Enabled = false, X = 0.3, Y = 0.6, Scale = 0.45 };
        var wire = JsonSerializer.Serialize(composed, AppJsonContext.Default.PanelLayoutDto);
        var fromWire = JsonSerializer.Deserialize(wire, AppJsonContext.Default.PanelLayoutDto)!;
        var settings = new NexusSettings();
        settings.PanelDevices["d215"] = Record(fromWire);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, PersistenceJsonContext.Default.NexusSettings);
        var reloaded = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.NexusSettings)!.PanelDevices["d215"].Layout!;

        Assert.Equal(2, reloaded.D215Panels!.Items.Count);
        Assert.Equal(0.35, reloaded.D215Panels.Items[0].Diameter);
        Assert.Equal("monitoring", reloaded.D215Panels.Items[1].Type);
        Assert.Equal("analog", reloaded.D215Panels.Items[0].Config!["design"].GetString());
        Assert.Equal("base-clock", reloaded.Pages[0].Widgets[0].Id);
        Assert.Equal(new[] { "clock", "weather" }, reloaded.WidgetPlaylist!.Types);
        Assert.False(reloaded.D215Widget!.Enabled);
        Assert.Equal((0.3, 0.6, 0.45), (reloaded.D215Widget.X, reloaded.D215Widget.Y, reloaded.D215Widget.Scale));
    }

    [Fact]
    public void Legacy_layouts_have_no_circle_mode()
    {
        var layout = JsonSerializer.Deserialize("""{"surface":"lcd-wide","pages":[]}""", AppJsonContext.Default.PanelLayoutDto)!;
        Assert.Null(layout.D215Panels);
        Assert.Null(layout.D215Widget);
    }

    [Fact]
    public void Preset_clone_owns_its_geometry_and_instance_configs()
    {
        var source = Layout(Circle());
        source.D215Widget = new() { Enabled = true, X = 0.4, Y = 0.6, Scale = 0.5 };
        var clone = PanelPresets.CloneLayout(source)!;
        clone.D215Panels!.Items[0].X = 0.9;
        clone.D215Panels.Items[0].Config!["design"] = Json("\"digital\"");
        clone.D215Widget!.Scale = 0.8;
        Assert.Equal(0.25, source.D215Panels!.Items[0].X);
        Assert.Equal("analog", source.D215Panels.Items[0].Config!["design"].GetString());
        Assert.Equal(0.5, source.D215Widget.Scale);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Profile_export_and_apply_preserve_circles_only_for_D215_targets(bool d215)
    {
        var source = new NexusSettings();
        source.PanelDevices["d215"] = Record(Layout(Circle()));
        source.PanelDevices["d215"].Layout!.D215Widget = new() { Enabled = true, X = 0.4, Y = 0.6, Scale = 0.5 };
        source.PanelDevices["d215"].Presets = [new() { Id = "saved", Name = "Saved", Layout = Layout(Circle("saved-circle")) }];
        source.PanelDevices["d215"].Presets![0].Layout!.D215Widget = new() { Enabled = false, X = 0.3, Y = 0.6, Scale = 0.7 };
        var file = ProfileSharing.ExtractShareable(source);
        Assert.NotNull(file.PanelDevices["d215"].Layout!.D215Panels);
        var live = new NexusSettings();
        live.PanelDevices["d215"] = Record(new PanelLayoutDto(), d215);

        PanelProfileData.ApplyToLive(live, file);

        var target = live.PanelDevices["d215"];
        Assert.Equal(d215, target.Layout!.D215Panels is not null);
        Assert.Equal(d215, target.Presets![0].Layout!.D215Panels is not null);
        Assert.Equal(d215, target.Layout.D215Widget is not null);
        Assert.Equal(d215, target.Presets[0].Layout!.D215Widget is not null);
        Assert.Equal("base-clock", target.Layout.Pages[0].Widgets[0].Id);
        Assert.NotNull(file.PanelDevices["d215"].Layout!.D215Panels);
        Assert.NotNull(file.PanelDevices["d215"].Layout!.D215Widget);
    }

    [Fact]
    public void Imported_D215_looks_and_presets_are_deep_copied()
    {
        var imported = new NexusSettings { HasPanelsData = true };
        imported.PanelDevices["d215"] = Record(Layout(Circle()));
        var live = new NexusSettings();
        live.PanelDevices["d215"] = Record(new PanelLayoutDto());
        Assert.True(PanelProfileData.MergeImportedLooks(live, imported, "Shared", out var skipped));
        Assert.Equal(0, skipped);
        var preset = Assert.Single(live.PanelDevices["d215"].Presets!);
        preset.Layout!.D215Panels!.Items[0].Config!["design"] = Json("\"digital\"");
        Assert.Equal("analog", imported.PanelDevices["d215"].Layout!.D215Panels!.Items[0].Config!["design"].GetString());
    }

    [Fact]
    public void Imported_looks_with_identical_circles_and_different_instance_ids_do_not_duplicate_presets()
    {
        var imported = new NexusSettings { HasPanelsData = true };
        imported.PanelDevices["d215"] = Record(Layout(Circle("imported-id")));
        var live = new NexusSettings();
        live.PanelDevices["d215"] = Record(Layout(Circle("local-id")));

        Assert.False(PanelProfileData.MergeImportedLooks(live, imported, "Shared", out var skipped));
        Assert.Equal(0, skipped);
        Assert.Null(live.PanelDevices["d215"].Presets);
        Assert.Equal("local-id", live.PanelDevices["d215"].Layout!.D215Panels!.Items[0].Id);
    }

    [Fact]
    public void SDK_settings_resolve_and_patch_each_circle_independently_including_empty_config_and_reset()
    {
        var appDir = Path.Combine(_dir, AppId);
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "manifest.json"), """{"schema":"nexus.app/1","id":"example.round","name":"Round","version":"1.0.0","runtime":"sdk","sizes":["2x2round"],"settings":[{"key":"color","type":"string","default":"red"}]}""");
        File.WriteAllText(Path.Combine(appDir, "widget.mjs"), "export const mount = () => {};");
        var a = Circle("a", "app:" + AppId);
        a.Config = null;
        var b = Circle("b", "app:" + AppId);
        b.Config = new() { ["color"] = Json("\"blue\"") };
        _store.Update(settings => settings.PanelDevices["d215"] = Record(Layout(a, b)));
        var registry = new AppRegistry(() => [new AppInstallPaths.Root(_dir, AppInstallPaths.Source.User)]);
        var service = new WidgetSettingsService(_store, registry);

        Assert.True(service.TryGetApp("a", out var app, out _));
        Assert.Equal(AppId, app);
        Assert.Equal("red", service.Get("a").Values["color"].GetString());
        service.Apply("a", new WidgetSettingsPatch { Set = new() { ["color"] = Json("\"green\"") } });
        Assert.Equal("green", a.Config!["color"].GetString());
        Assert.Equal("blue", service.Get("b").Values["color"].GetString());
        service.Apply("a", new WidgetSettingsPatch { Reset = ["color"] });
        Assert.Null(a.Config);
        Assert.Equal("red", service.Get("a").Values["color"].GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uninstall_retires_live_and_saved_circle_instances_without_dropping_other_content(bool placedLive)
    {
        var record = Record(placedLive ? Layout(Circle("a", "app:" + AppId), Circle("b", "clock")) : Layout(Circle("b", "clock")));
        record.Presets = [new() { Id = "saved", Layout = Layout(Circle("saved", "app:" + AppId)) }];
        _store.Update(settings => settings.PanelDevices[record.Id] = record);
        var registry = new PanelDeviceRegistry(_store);

        Assert.Equal(new[] { record.Id }, registry.RemoveWidgetType("app:" + AppId));

        Assert.Equal("b", Assert.Single(record.Layout!.D215Panels!.Items).Id);
        Assert.Empty(record.Presets[0].Layout!.D215Panels!.Items);
        Assert.Equal("base-clock", Assert.Single(record.Layout.Pages[0].Widgets).Id);
        Assert.Empty(registry.RemoveWidgetType("app:" + AppId));
    }

    [Fact]
    public void Legacy_app_prefix_migration_reaches_saved_and_live_circle_instances()
    {
        var record = Record(Layout(Circle("a", "marketplace:" + AppId)));
        record.Presets = [new() { Layout = Layout(Circle("b", "marketplace:" + AppId)) }];
        var settings = new NexusSettings();
        settings.PanelDevices[record.Id] = record;
        AppPrefixMigration.Apply(settings);
        Assert.Equal("app:" + AppId, record.Layout!.D215Panels!.Items[0].Type);
        Assert.Equal("app:" + AppId, record.Presets[0].Layout!.D215Panels!.Items[0].Type);
    }

    [Fact]
    public void Gallery_playlist_usage_counts_each_independent_circle()
    {
        var first = Circle("a", "gallery");
        first.Config = new() { ["playlistId"] = Json("\"photos\"") };
        var second = Circle("b", "gallery");
        second.Config = new() { ["playlistId"] = Json("\"photos\"") };
        var settings = new NexusSettings();
        settings.PanelDevices["d215"] = Record(Layout(first, second));
        Assert.Equal(2, Assert.Single(GalleryPlaylistUsage.Find(settings, "photos")).Count);
    }
}
