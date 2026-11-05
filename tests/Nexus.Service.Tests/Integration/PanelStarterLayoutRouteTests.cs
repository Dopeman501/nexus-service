using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// A panel with no saved layout is served a starter. Its ids must be the same on every read:
/// clients key an open widget on them and refetch on any panel/device frame.
/// </summary>
public sealed class PanelStarterLayoutRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<string[]> IdsAsync(HttpClient client, string id)
    {
        var res = await client.GetAsync($"/panel/devices/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var layout = (await res.Content.ReadFromJsonAsync<PanelDeviceRecord>())!.Layout!;
        return layout.Pages.Select(p => p.Id).Concat(layout.Pages.SelectMany(p => p.Widgets).Select(w => w.Id)).ToArray();
    }

    [Theory]
    [InlineData("y70", 1920, 515)]
    [InlineData("phone", 412, 915)]
    [InlineData("q60", 1280, 480)]
    [InlineData("monitor", 3840, 720)]
    public async Task An_unedited_record_returns_the_same_widget_ids_on_every_read(string surface, int width, int height)
    {
        var client = Client();
        var created = await client.PostAsJsonAsync("/panel/devices", new PanelDeviceCreateBody
        {
            DisplayName = "Starter",
            Capabilities = new PanelDeviceCapabilities { Surface = surface, CssWidth = width, CssHeight = height },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<PanelDeviceRecord>())!.Id;

        var first = await IdsAsync(client, id);
        var second = await IdsAsync(client, id);

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Host_created_portrait_record_has_one_clock_and_stable_widget_ids()
    {
        var client = Client();
        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var record = registry.Allocate("Jungle Leopard D215", new PanelDeviceCapabilities
        {
            Surface = "lcd-portrait", Family = "jungle-leopard-d215", CssWidth = 756, CssHeight = 1680, Dpr = 1,
        }, canReuse: _ => false);

        var returned = await client.GetFromJsonAsync<PanelDeviceRecord>($"/panel/devices/{record.Id}");
        var layout = Assert.IsType<PanelLayoutDto>(returned?.Layout);
        Assert.Equal("lcd-portrait", layout.Surface);
        var clock = Assert.Single(Assert.Single(layout.Pages).Widgets);
        Assert.Equal("clock", clock.Type);
        Assert.Equal("2x4", clock.Size);
        Assert.Equal(new[] { layout.Pages[0].Id, clock.Id }, await IdsAsync(client, record.Id));
    }

    [Fact]
    public async Task Clients_cannot_allocate_or_modify_the_host_owned_portrait_capabilities()
    {
        var client = Client();
        var capabilities = new PanelDeviceCapabilities { Surface = "lcd-portrait", Family = "jungle-leopard-d215", CssWidth = 756, CssHeight = 1680 };
        var created = await client.PostAsJsonAsync("/panel/devices", new PanelDeviceCreateBody { Capabilities = capabilities });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var portrait = registry.Allocate("Jungle Leopard D215", capabilities, canReuse: _ => false);
        var other = registry.Allocate("Unrelated display", new PanelDeviceCapabilities { Surface = "lcd-wide" });

        var patch = await client.PostAsJsonAsync($"/panel/devices/{portrait.Id}", new PanelDevicePatch
        {
            Capabilities = new PanelDeviceCapabilities { Surface = "lcd-wide", CssWidth = 1200 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
        Assert.Equal(756, registry.Get(portrait.Id)?.Capabilities?.CssWidth);

        var claim = await client.PostAsJsonAsync($"/panel/devices/{other.Id}", new PanelDevicePatch { Capabilities = capabilities });
        Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode);

        var theme = await client.PostAsJsonAsync($"/panel/devices/{portrait.Id}", new PanelDevicePatch { ThemeMode = "dark" });
        Assert.Equal(HttpStatusCode.OK, theme.StatusCode);
        Assert.Equal("dark", registry.Get(portrait.Id)?.ThemeMode);
    }
}
