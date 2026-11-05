using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Panel;

public sealed class D215PanelsPolicyTests
{
    private static readonly AppRegistry Apps = new(() => []);
    private static readonly PanelDeviceCapabilities Target = new() { Family = "jungle-leopard-d215", Surface = "lcd-wide" };

    private static PanelLayoutDto Layout(double x = 0.5, double y = 0.5, double diameter = 0.4) => new()
    {
        D215Panels = new() { Items = [new() { Id = "circle", Type = "clock", X = x, Y = y, Diameter = diameter }] },
    };

    [Theory]
    [InlineData(double.NaN, 0.5, 0.4)]
    [InlineData(double.PositiveInfinity, 0.5, 0.4)]
    [InlineData(0.5, double.NaN, 0.4)]
    [InlineData(0.5, double.NegativeInfinity, 0.4)]
    [InlineData(0.5, 0.5, double.NaN)]
    [InlineData(0.5, 0.5, double.PositiveInfinity)]
    public void Nonfinite_geometry_never_reaches_a_renderer(double x, double y, double diameter)
    {
        Assert.False(D215PanelsPolicy.IsValid(Layout(x, y, diameter), Target, Apps, null));
    }

    [Theory]
    [InlineData(0, 0, 0.12)]
    [InlineData(1, 1, 1)]
    public void Normalized_centers_and_diameter_endpoints_are_valid_and_render_time_clamping_owns_the_circle_bounds(double x, double y, double diameter)
    {
        Assert.True(D215PanelsPolicy.IsValid(Layout(x, y, diameter), Target, Apps, null));
    }

    [Theory]
    [InlineData(" ", "clock")]
    [InlineData("circle", "")]
    [InlineData("circle", "app:../escape")]
    [InlineData("circle", "app:UPPER.app")]
    public void Invalid_instance_ids_and_app_identifiers_are_rejected(string id, string type)
    {
        var layout = Layout();
        layout.D215Panels!.Items[0].Id = id;
        layout.D215Panels.Items[0].Type = type;
        Assert.False(D215PanelsPolicy.IsValid(layout, Target, Apps, null));
    }

    [Fact]
    public void Excessive_ids_are_rejected()
    {
        var layout = Layout();
        layout.D215Panels!.Items[0].Id = new string('a', 129);
        Assert.False(D215PanelsPolicy.IsValid(layout, Target, Apps, null));
    }

    [Fact]
    public void Sanitizing_an_imported_invalid_composition_retains_the_ordinary_layout()
    {
        var layout = Layout(diameter: 1.1);
        var safe = D215PanelsPolicy.ForTarget(layout, Target)!;
        Assert.Null(safe.D215Panels);
        Assert.NotNull(layout.D215Panels);
        Assert.Equal(layout.Surface, safe.Surface);
    }

    [Fact]
    public void Legacy_layouts_are_untouched_even_for_other_devices()
    {
        var legacy = new PanelLayoutDto();
        Assert.True(D215PanelsPolicy.IsValid(legacy, null, Apps, null));
        Assert.Same(legacy, D215PanelsPolicy.ForTarget(legacy, null));
    }
}
