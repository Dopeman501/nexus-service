using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelAudioTriggerRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record() => _factory.Services.GetRequiredService<PanelDeviceRegistry>()
        .Allocate("Audio", new PanelDeviceCapabilities { Surface = "lcd-wide" }, canReuse: _ => false).Id;

    [Fact]
    public async Task Audio_selection_roundtrips_without_replacing_the_normal_background()
    {
        using var client = Client();
        var id = Record();
        const string body = "{\"backgroundMode\":\"solid\",\"backgroundColor\":\"#123456\",\"backgroundAudioTrigger\":{\"enabled\":true,\"intensity\":70,\"switchDelay\":0.5,\"shuffle\":true,\"backgrounds\":[{\"mode\":\"solid\",\"color\":\"#abcdef\"}]}}";
        using var saved = await client.PostAsync($"/panel/devices/{id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var get = await client.GetAsync($"/panel/devices/{id}");
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var record = json.RootElement;
        Assert.Equal("#123456", record.GetProperty("backgroundColor").GetString());
        Assert.Equal(70, record.GetProperty("backgroundAudioTrigger").GetProperty("intensity").GetDouble());
    }

    [Theory]
    [InlineData("{\"intensity\":-1}")]
    [InlineData("{\"intensity\":101}")]
    [InlineData("{\"switchDelay\":-0.1}")]
    [InlineData("{\"switchDelay\":11}")]
    [InlineData("{\"intensity\":\"50\"}")]
    [InlineData("{\"backgrounds\":null}")]
    [InlineData("{\"backgrounds\":[null]}")]
    [InlineData("{\"backgrounds\":[{}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"media\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"media\",\"mediaId\":\"valid\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"media\",\"mediaId\":\"\",\"mediaType\":\"static\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"media\",\"mediaId\":\"valid\",\"mediaType\":\"video\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"media\",\"slideshow\":true}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"solid\",\"color\":\"url(bad)\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"shader\",\"effect\":\"../bad\"}]}")]
    public async Task Invalid_audio_configuration_is_rejected_even_when_disabled(string settings)
    {
        using var client = Client();
        var id = Record();
        using var response = await client.PostAsync($"/panel/devices/{id}", new StringContent("{\"backgroundAudioTrigger\":" + settings + "}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
