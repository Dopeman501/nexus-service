using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

public sealed class D215PanelsRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _apps = Path.Combine(Path.GetTempPath(), "nexus-d215-panels-" + Guid.NewGuid().ToString("N"));

    public D215PanelsRouteTests()
    {
        Directory.CreateDirectory(_apps);
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppRegistry>();
            services.AddSingleton(new AppRegistry(() => [new AppInstallPaths.Root(_apps, AppInstallPaths.Source.User)]));
        }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
        Directory.Delete(_apps, recursive: true);
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record(bool d215 = true) => _factory.Services.GetRequiredService<PanelDeviceRegistry>().Allocate("Panels", new PanelDeviceCapabilities
    {
        Surface = "lcd-wide", Family = d215 ? "jungle-leopard-d215" : "another-lcd", CssWidth = 1680, CssHeight = 756,
    }, canReuse: _ => false).Id;

    private static string Item(string id = "circle-1", string type = "clock", string geometry = "\"x\":0.5,\"y\":0.5,\"diameter\":0.4") =>
        "{\"id\":\"" + id + "\",\"type\":\"" + type + "\"," + geometry + ",\"config\":{\"design\":\"digital\"}}";

    private static string Body(string items, bool enabled = true) =>
        "{\"layout\":{\"layoutSchemaVersion\":2,\"surface\":\"lcd-wide\",\"pages\":[{\"id\":\"p1\",\"widgets\":[{\"id\":\"grid-clock\",\"type\":\"clock\",\"size\":\"4x2\",\"col\":0,\"row\":0}]}],\"d215Panels\":{\"enabled\":"
        + enabled.ToString().ToLowerInvariant() + ",\"items\":" + items + "}}}";

    private static Task<HttpResponseMessage> Post(HttpClient client, string record, string body) =>
        client.PostAsync($"/panel/devices/{record}", new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Independent_round_panels_persist_without_replacing_the_legacy_widget(int count)
    {
        using var client = Client();
        var id = Record();
        var types = new[] { "clock", "calendar", "monitoring", "media", "weather", "gallery" };
        var items = "[" + string.Join(',', types.Take(count).Select((type, index) => Item("circle-" + index, type))) + "]";
        using var response = await Post(client, id, Body(items));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var get = await client.GetAsync($"/panel/devices/{id}");
        var layout = (await Read(get)).GetProperty("layout");
        Assert.Equal(count, layout.GetProperty("d215Panels").GetProperty("items").GetArrayLength());
        Assert.Equal("grid-clock", layout.GetProperty("pages")[0].GetProperty("widgets")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"\",\"type\":\"clock\",\"x\":0.5,\"y\":0.5,\"diameter\":0.4}]")]
    [InlineData("[{\"id\":\"circle\",\"type\":\"deck\",\"x\":0.5,\"y\":0.5,\"diameter\":0.4}]")]
    [InlineData("[{\"id\":\"circle\",\"type\":\"app:missing.app\",\"x\":0.5,\"y\":0.5,\"diameter\":0.4}]")]
    public async Task Invalid_panel_arrays_are_rejected(string items)
    {
        using var client = Client();
        using var response = await Post(client, Record(), Body(items));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("\"x\":-0.1,\"y\":0.5,\"diameter\":0.4")]
    [InlineData("\"x\":1.1,\"y\":0.5,\"diameter\":0.4")]
    [InlineData("\"x\":0.5,\"y\":-0.1,\"diameter\":0.4")]
    [InlineData("\"x\":0.5,\"y\":1.1,\"diameter\":0.4")]
    [InlineData("\"x\":0.5,\"y\":0.5,\"diameter\":0.119")]
    [InlineData("\"x\":0.5,\"y\":0.5,\"diameter\":1.01")]
    [InlineData("\"x\":\"0.5\",\"y\":0.5,\"diameter\":0.4")]
    public async Task Out_of_range_or_non_numeric_geometry_is_rejected(string geometry)
    {
        using var client = Client();
        using var response = await Post(client, Record(), Body("[" + Item(geometry: geometry) + "]"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("grid-clock")]
    public async Task Ambiguous_instance_ids_are_rejected(string collision)
    {
        using var client = Client();
        var items = collision == "duplicate" ? "[" + Item(collision) + "," + Item(collision) + "]" : "[" + Item(collision) + "]";
        using var response = await Post(client, Record(), Body(items));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_seventh_panel_is_rejected_even_when_the_mode_is_disabled()
    {
        using var client = Client();
        var items = "[" + string.Join(',', Enumerable.Range(0, 7).Select(index => Item("circle-" + index))) + "]";
        using var response = await Post(client, Record(), Body(items, enabled: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Other_device_families_cannot_store_D215_panels()
    {
        using var client = Client();
        using var response = await Post(client, Record(d215: false), Body("[]", enabled: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Installed_round_SDK_instances_are_supported_and_existing_missing_apps_survive_edits()
    {
        const string appId = "example.round";
        InstallApp(appId);
        using var client = Client();
        var id = Record();
        using var added = await Post(client, id, Body("[" + Item(type: "app:" + appId) + "]"));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Directory.Delete(Path.Combine(_apps, appId), recursive: true);
        _factory.Services.GetRequiredService<AppRegistry>().Refresh();
        using var kept = await Post(client, id, Body("[" + Item(type: "app:" + appId, geometry: "\"x\":0.7,\"y\":0.3,\"diameter\":0.25") + "]"));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        using var forged = await Post(client, id, Body("[" + Item("new-instance", "app:" + appId) + "]"));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [Theory]
    [InlineData("2x2", "lcd-round", false, false)]
    [InlineData("2x2round", "y70", false, false)]
    [InlineData("2x2round", "lcd-round", true, true)]
    public async Task SDK_shape_surface_and_single_instance_contracts_are_enforced(string size, string surface, bool single, bool duplicate)
    {
        InstallApp("example.round", size, surface, single);
        using var client = Client();
        var items = "[" + Item(type: "app:example.round") + (duplicate ? "," + Item("circle-2", "app:example.round") : "") + "]";
        using var response = await Post(client, Record(), Body(items));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("app:")]
    [InlineData("marketplace:")]
    public async Task A_single_instance_SDK_app_cannot_be_duplicated_from_the_retained_widget_layout(string prefix)
    {
        InstallApp("example.round", single: true);
        using var client = Client();
        var body = Body("[" + Item(type: "app:example.round") + "]")
            .Replace("\"id\":\"grid-clock\",\"type\":\"clock\"", "\"id\":\"grid-clock\",\"type\":\"" + prefix + "example.round\"");
        using var response = await Post(client, Record(), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private void InstallApp(string appId, string size = "2x2round", string surface = "lcd-round", bool single = false)
    {
        var folder = Path.Combine(_apps, appId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "nexus.app/1", id = appId, name = "Circle", version = "1.0.0", runtime = "sdk",
            sizes = new[] { size }, surfaces = new[] { surface }, single_instance = single,
        }));
        File.WriteAllText(Path.Combine(folder, "widget.mjs"), "export const mount = () => {};");
    }
}
