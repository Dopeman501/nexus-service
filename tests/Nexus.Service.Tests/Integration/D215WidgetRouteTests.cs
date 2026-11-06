using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

public sealed class D215WidgetRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record(bool d215 = true) => _factory.Services.GetRequiredService<PanelDeviceRegistry>().Allocate("Widget transform", new PanelDeviceCapabilities
    {
        Surface = "lcd-wide", Family = d215 ? "jungle-leopard-d215" : "another-lcd", CssWidth = 1680, CssHeight = 756,
    }, canReuse: _ => false).Id;

    private static string Body(string widget) =>
        "{\"layout\":{\"layoutSchemaVersion\":2,\"surface\":\"lcd-wide\",\"pages\":[{\"id\":\"page\",\"widgets\":[{\"id\":\"clock\",\"type\":\"clock\",\"size\":\"4x2\",\"col\":0,\"row\":0}]}],"
        + "\"widgetPlaylist\":{\"enabled\":true,\"interval\":15,\"types\":[\"clock\",\"calendar\"]},"
        + "\"d215Panels\":{\"enabled\":false,\"items\":[{\"id\":\"circle\",\"type\":\"monitoring\",\"x\":0.5,\"y\":0.5,\"diameter\":0.4}]},\"d215Widget\":" + widget + "}}";

    private static Task<HttpResponseMessage> Post(HttpClient client, string record, string body) =>
        client.PostAsync($"/panel/devices/{record}", new StringContent(body, Encoding.UTF8, "application/json"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Widget_visibility_and_transform_roundtrip_without_changing_the_playlist_or_circles(bool enabled)
    {
        using var client = Client();
        var id = Record();
        var widget = "{\"enabled\":" + enabled.ToString().ToLowerInvariant() + ",\"x\":0.25,\"y\":0.7,\"scale\":0.45}";
        using var response = await Post(client, id, Body(widget));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var get = await client.GetAsync($"/panel/devices/{id}");
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var layout = json.RootElement.GetProperty("layout");
        var saved = layout.GetProperty("d215Widget");
        Assert.Equal(enabled, saved.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.25, saved.GetProperty("x").GetDouble());
        Assert.Equal(0.7, saved.GetProperty("y").GetDouble());
        Assert.Equal(0.45, saved.GetProperty("scale").GetDouble());
        Assert.False(layout.GetProperty("d215Panels").GetProperty("enabled").GetBoolean());
        Assert.Single(layout.GetProperty("d215Panels").GetProperty("items").EnumerateArray());
        Assert.True(layout.GetProperty("widgetPlaylist").GetProperty("enabled").GetBoolean());
        Assert.Equal("clock", layout.GetProperty("pages")[0].GetProperty("widgets")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("{\"enabled\":true,\"x\":-0.1,\"y\":0.5,\"scale\":0.5}")]
    [InlineData("{\"enabled\":true,\"x\":1.1,\"y\":0.5,\"scale\":0.5}")]
    [InlineData("{\"enabled\":true,\"x\":0.5,\"y\":-0.1,\"scale\":0.5}")]
    [InlineData("{\"enabled\":true,\"x\":0.5,\"y\":1.1,\"scale\":0.5}")]
    [InlineData("{\"enabled\":false,\"x\":0.5,\"y\":0.5,\"scale\":0.119}")]
    [InlineData("{\"enabled\":true,\"x\":0.5,\"y\":0.5,\"scale\":1.1}")]
    [InlineData("{\"enabled\":true,\"x\":\"0.5\",\"y\":0.5,\"scale\":0.5}")]
    [InlineData("{\"enabled\":true,\"x\":0.5,\"y\":0.5}")]
    public async Task Invalid_or_missing_widget_geometry_is_rejected(string widget)
    {
        using var client = Client();
        using var response = await Post(client, Record(), Body(widget));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Other_families_cannot_store_the_D215_widget_transform()
    {
        using var client = Client();
        using var response = await Post(client, Record(d215: false),
            "{\"layout\":{\"surface\":\"lcd-wide\",\"pages\":[],\"d215Widget\":{\"enabled\":false,\"x\":0.5,\"y\":0.5,\"scale\":1}}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
