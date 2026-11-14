using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelHomePageRouteTests : IDisposable
{
    // The shared factory uses an in-process server, disposable settings, and
    // no hosted workers or machine/hardware startup.
    private readonly NexusAppFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("p2", "p2")]
    [InlineData("missing", null)]
    [InlineData("blank-page", null)]
    public async Task Home_is_normalized_saved_and_read_back_through_the_existing_device_route(string incoming, string? expected)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        var allocated = await client.PostAsJsonAsync("/panel/devices", new PanelDeviceCreateBody
        {
            DisplayName = "Y70", Capabilities = new() { Surface = "y70", Touch = true },
        });
        Assert.Equal(HttpStatusCode.OK, allocated.StatusCode);
        var id = (await allocated.Content.ReadFromJsonAsync<PanelDeviceRecord>())!.Id;
        using var body = new StringContent($$$"""
            {"layout":{"layoutSchemaVersion":2,"surface":"y70","activePageId":"p1","homePageId":"{{{incoming}}}",
             "pages":[{"id":"p1","widgets":[]},{"id":"p2","widgets":[]}]}}
            """, Encoding.UTF8, "application/json");
        var patched = await client.PostAsync($"/panel/devices/{id}", body);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal(expected, (await patched.Content.ReadFromJsonAsync<PanelDeviceRecord>())!.Layout!.HomePageId);
        var readback = (await client.GetFromJsonAsync<PanelDeviceRecord>($"/panel/devices/{id}"))!.Layout!;
        Assert.Equal(expected, readback.HomePageId);
        Assert.Equal("p1", readback.ActivePageId);
        Assert.Equal(new[] { "p1", "p2" }, readback.Pages.Select(page => page.Id));

        _factory.Services.GetRequiredService<IConfigStore>().FlushNow();
        using var reloaded = new JsonConfigStore(_factory.SettingsPath);
        Assert.Equal(expected, reloaded.Load().PanelDevices[id].Layout!.HomePageId);
    }
}
