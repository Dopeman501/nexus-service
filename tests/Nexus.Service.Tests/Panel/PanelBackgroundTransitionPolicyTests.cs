using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelBackgroundTransitionPolicyTests
{
    private static readonly string[] CurtainsIds =
        ["wave", "liquid", "ripple", "swirl", "zoom", "chromatic", "pixelate", "horizontal", "vertical", "diagonal", "twist", "melt"];

    [Fact]
    public void Exact_catalogue_matches_pinned_dashboard_package_and_authored_curtains_presets()
    {
        // Workspace compatibility check: the sibling dashboard must have its pinned dependencies installed.
        var metadataPath = FindPinnedMetadata();
        using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(metadataPath)!, "package.json")));
        Assert.Equal("3.2.0", package.RootElement.GetProperty("version").GetString());
        var glKeys = metadata.RootElement.EnumerateArray().Select(entry => "gl-transitions:" + entry.GetProperty("name").GetString()).ToArray();
        Assert.Equal(214, glKeys.Length);
        var expected = glKeys.Concat(CurtainsIds.Select(id => "curtains:" + id)).Append("default").Append("none").ToHashSet(StringComparer.Ordinal);
        Assert.Equal(228, expected.Count);
        Assert.True(expected.SetEquals(PanelBackgroundTransitionCatalogue.Effects), "Native and dashboard transition catalogues must have identical keys.");
        foreach (var key in expected)
            Assert.True(PanelBackgroundTransitionPolicy.IsValid(new() { Effect = key }), key);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(800)]
    [InlineData(5000)]
    public void Duration_bounds_and_both_json_contexts_preserve_integer_values(int durationMs)
    {
        var transition = new PanelBackgroundTransitionDto { Effect = "curtains:wave", DurationMs = durationMs };
        Assert.True(PanelBackgroundTransitionPolicy.IsValid(transition));
        var json = JsonSerializer.Serialize(transition, AppJsonContext.Default.PanelBackgroundTransitionDto);
        var restored = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.PanelBackgroundTransitionDto)!;
        Assert.Equal(durationMs, restored.DurationMs);
        Assert.Equal(transition.Effect, restored.Effect);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(5001)]
    [InlineData(int.MaxValue)]
    public void Invalid_durations_are_rejected_and_omitted_from_imports(int durationMs)
    {
        var transition = new PanelBackgroundTransitionDto { DurationMs = durationMs };
        Assert.False(PanelBackgroundTransitionPolicy.IsValid(transition));
        Assert.Null(PanelBackgroundTransitionPolicy.Clone(transition));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ripple")]
    [InlineData("motion:fade")]
    [InlineData("anime:fade")]
    [InlineData("codrops:displacement")]
    [InlineData("curtains:unknown")]
    [InlineData("gl-transitions:unknown")]
    [InlineData("gl-transitions:crosszoom")]
    [InlineData("GL-transitions:CrossZoom")]
    [InlineData("curtains:wave ")]
    [InlineData("../curtains:wave")]
    [InlineData("https://example.com/shader.glsl")]
    public void Unsupported_malformed_or_nonexact_effect_keys_are_rejected(string? effect)
    {
        var transition = new PanelBackgroundTransitionDto { Effect = effect! };
        Assert.False(PanelBackgroundTransitionPolicy.IsValid(transition));
        Assert.Null(PanelBackgroundTransitionPolicy.Clone(transition));
    }

    [Fact]
    public void Missing_settings_are_valid_and_valid_clones_are_independent()
    {
        Assert.True(PanelBackgroundTransitionPolicy.IsValid(null));
        Assert.Null(PanelBackgroundTransitionPolicy.Clone(null));
        var transition = new PanelBackgroundTransitionDto();
        var copy = PanelBackgroundTransitionPolicy.Clone(transition)!;
        Assert.NotSame(transition, copy);
        Assert.Equal("default", copy.Effect);
        Assert.Equal(800, copy.DurationMs);
    }

    private static string FindPinnedMetadata()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "nexus-web", "node_modules", "gl-transitions", "gl-transitions.json");
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("Workspace catalogue parity requires the sibling nexus-web pinned gl-transitions dependency; run npm ci there first.");
    }
}
