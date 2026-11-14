using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelHomePageTests
{
    private const string LayoutJson = """
        {"layoutSchemaVersion":2,"surface":"y70","activePageId":"p1","homePageId":"p2",
         "pages":[{"id":"p1","widgets":[{"id":"w1","type":"clock","size":"2x2"}]},
                  {"id":"p2","widgets":[{"id":"w2","type":"weather","size":"2x2"}]}]}
        """;

    // Exercise the wire contract so the RED step fails on lost state, rather
    // than requiring a DTO property that does not exist yet.
    private static PanelLayoutDto Layout(string? home = "p2") => JsonSerializer.Deserialize(
        LayoutJson.Replace("\"homePageId\":\"p2\"", "\"homePageId\":" + JsonSerializer.Serialize(home)),
        AppJsonContext.Default.PanelLayoutDto)!;

    private static string? Home(PanelLayoutDto? layout)
    {
        if (layout is null) return null;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(layout, AppJsonContext.Default.PanelLayoutDto));
        return json.RootElement.TryGetProperty("homePageId", out var home) ? home.GetString() : null;
    }

    private static PanelDeviceRecord Record(PanelLayoutDto? layout) => new()
    {
        Id = "y70", DisplayName = "Y70", Layout = layout,
        Capabilities = new() { Surface = "y70", Touch = true },
    };

    private sealed class MemoryConfigStore : IConfigStore
    {
        public NexusSettings Settings { get; } = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => Settings;
        public void Update(Action<NexusSettings> mutator) { mutator(Settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }

    [Fact]
    public void Wire_and_settings_roundtrip_preserve_home_separately_from_the_active_page()
    {
        var settings = new NexusSettings();
        settings.PanelDevices["y70"] = Record(Layout());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, PersistenceJsonContext.Default.NexusSettings);
        var reloaded = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.NexusSettings)!.PanelDevices["y70"].Layout!;
        Assert.Equal("p2", Home(reloaded));
        Assert.Equal("p1", reloaded.ActivePageId);
        Assert.Equal(new[] { "p1", "p2" }, reloaded.Pages.Select(p => p.Id));
        Assert.Equal("w2", reloaded.Pages[1].Widgets[0].Id);
    }

    [Fact]
    public void Legacy_layouts_and_omitted_home_edits_keep_the_default_unset()
    {
        var legacy = JsonSerializer.Deserialize("""{"surface":"y70","pages":[{"id":"p1","widgets":[]}]}""",
            AppJsonContext.Default.PanelLayoutDto)!;
        Assert.Null(Home(legacy));
        Assert.Null(Home(PanelPresets.CloneLayout(legacy)));
    }

    [Fact]
    public void Patching_home_preserves_navigation_widgets_and_the_callers_input()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70"] = Record(Layout(null));
        var incoming = Layout();
        var result = new PanelDeviceRegistry(store).Patch("y70", new() { Layout = incoming })!;
        Assert.Equal("p2", Home(result.Layout));
        Assert.Equal("p1", result.Layout!.ActivePageId);
        Assert.Equal("w2", result.Layout.Pages[1].Widgets[0].Id);
        Assert.Equal("p2", Home(incoming));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("P2")]
    [InlineData("blank-page")]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_home_ids_are_cleared_at_layout_receipt_without_changing_the_input(string home)
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70"] = Record(Layout());
        var incoming = Layout(home);
        // Even an incorrectly stored blank-page entry cannot become home.
        if (home == "blank-page") incoming.Pages.Add(new() { Id = "blank-page" });
        var result = new PanelDeviceRegistry(store).Patch("y70", new() { Layout = incoming })!;
        Assert.Null(Home(result.Layout));
        Assert.Equal(home, Home(incoming));
        Assert.Equal("p1", result.Layout!.ActivePageId);
        Assert.Equal(incoming.Pages.Select(p => p.Id), result.Layout.Pages.Select(p => p.Id));
    }

    [Fact]
    public void Home_tracks_the_page_identity_across_reordering_and_is_cleared_after_deletion()
    {
        var layout = Layout();
        layout.Pages.Reverse();
        var reordered = PanelPresets.CloneLayout(layout)!;
        Assert.Equal("p2", Home(reordered));
        Assert.Equal("p1", reordered.ActivePageId);
        reordered.Pages.RemoveAt(0);
        var deleted = PanelPresets.CloneLayout(reordered)!;
        Assert.Null(Home(deleted));
        Assert.Equal("p1", Assert.Single(deleted.Pages).Id);
        Assert.Equal("p2", Home(reordered));
    }

    [Fact]
    public void Presets_capture_and_restore_each_looks_home_and_reset_clears_it()
    {
        var store = new MemoryConfigStore();
        store.Settings.PanelDevices["y70"] = Record(Layout());
        var registry = new PanelDeviceRegistry(store);
        var saved = Assert.Single(registry.CreatePreset("y70", "Home", out _)!.Presets);
        registry.CreatePreset("y70", "Other", out _);
        registry.Patch("y70", new() { Layout = Layout("p1") });
        registry.ActivatePreset("y70", saved.Id);
        Assert.Equal("p2", Home(registry.Get("y70")!.Layout));
        Assert.Equal("p1", Home(store.Settings.PanelDevices["y70"].Presets![1].Layout));
        registry.Patch("y70", new() { Layout = Layout("p1") });
        Assert.Equal("p2", Home(store.Settings.PanelDevices["y70"].Presets![0].Layout));
        Assert.Null(Home(registry.ResetToDefaults("y70")!.Layout));
    }

    [Fact]
    public void Profile_export_apply_and_import_preserve_a_valid_home_and_clear_an_invalid_home()
    {
        var source = new NexusSettings { HasPanelsData = true };
        source.PanelDevices["y70"] = Record(Layout());
        source.PanelDevices["y70"].Presets = [new() { Id = "preset", Name = "Saved", Layout = Layout("missing") }];
        var exported = ProfileSharing.ExtractShareable(source);
        Assert.Equal("p2", Home(exported.PanelDevices["y70"].Layout));
        Assert.Null(Home(exported.PanelDevices["y70"].Presets![0].Layout));
        var live = new NexusSettings();
        live.PanelDevices["y70"] = Record(Layout(null));
        PanelProfileData.ApplyToLive(live, exported);
        Assert.Equal("p2", Home(live.PanelDevices["y70"].Layout));
        var importedTarget = new NexusSettings();
        importedTarget.PanelDevices["y70"] = Record(Layout(null));
        Assert.True(PanelProfileData.MergeImportedLooks(importedTarget, exported, "Imported", out _));
        Assert.Contains(importedTarget.PanelDevices["y70"].Presets!, p => Home(p.Layout) == "p2");
        Assert.Equal("missing", Home(source.PanelDevices["y70"].Presets![0].Layout));
    }

    [Fact]
    public void Stable_id_rewriting_maps_home_to_the_same_page()
    {
        var layout = PanelLayoutDefaults.WithStableIds(Layout(), "device");
        Assert.Equal(layout.Pages[1].Id, Home(layout));
        Assert.NotEqual("p2", Home(layout));
    }

    [Fact]
    public void A_home_with_no_real_page_collection_is_not_resolved()
    {
        Assert.Null(PanelHomePagePolicy.Resolve(new() { HomePageId = "p2", Pages = null! }));
        Assert.Null(PanelHomePagePolicy.Resolve(new() { HomePageId = "p2", Pages = [null!] }));
    }

    [Fact]
    public void Layouts_with_different_home_pages_are_distinct_imported_looks()
    {
        var imported = new NexusSettings { HasPanelsData = true };
        imported.PanelDevices["y70"] = Record(Layout());
        var live = new NexusSettings();
        live.PanelDevices["y70"] = Record(Layout("p1"));
        Assert.True(PanelProfileData.MergeImportedLooks(live, imported, "Other home", out _));
        Assert.Equal("p2", Home(Assert.Single(live.PanelDevices["y70"].Presets!).Layout));
    }
}
