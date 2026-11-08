using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

public sealed class D215RestartRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseline = new();
    private readonly WebApplicationFactory<Program> _factory;
    public D215RestartRouteTests()
    {
        _factory = _baseline.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<StreamedPanelStore>();
            services.AddSingleton(new StreamedPanelStore(Path.ChangeExtension(_baseline.SettingsPath, ".streams.json")));
        }));
    }
    public void Dispose() { _factory.Dispose(); _baseline.Dispose(); }
    private HttpClient Desktop()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Phone_and_query_tokens_cannot_authorize_restart()
    {
        const string route = "/panel/devices/stored/d215-restart";
        using var phone = TestPhoneSession.CreateClient(_factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync(route, new { })).StatusCode);
        using var query = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        Assert.Equal(HttpStatusCode.Unauthorized, (await query.PostAsJsonAsync(route + "?token=" + Uri.EscapeDataString(token), new { })).StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"port\":\"COM3\"}")]
    [InlineData("{\"value\":true}")]
    [InlineData("{\"command\":1}")]
    public async Task Restart_accepts_only_an_empty_object(string body)
    {
        using var client = Desktop();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/panel/devices/stored/d215-restart",
            new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Missing_foreign_and_unmapped_saved_records_are_unavailable()
    {
        using var client = Desktop();
        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var saved = registry.Allocate("Stored D215", new PanelDeviceCapabilities { Family = "jungle-leopard-d215", Surface = "lcd-wide" }, _ => false);
        var other = registry.Allocate("Other", new PanelDeviceCapabilities { Surface = "lcd-wide" }, _ => false);
        foreach (var id in new[] { "missing", saved.Id, other.Id })
        {
            using var response = await client.PostAsJsonAsync($"/panel/devices/{id}/d215-restart", new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public void Restart_contract_has_native_aot_metadata()
    {
        Assert.Equal("{}", JsonSerializer.Serialize(new D215RestartRequest(), AppJsonContext.Default.D215RestartRequest));
        Assert.Contains("\"status\":\"uncertain\"", JsonSerializer.Serialize(new D215RestartResponse("uncertain"), AppJsonContext.Default.D215RestartResponse));
    }

    [Fact]
    public async Task A_stored_identity_can_resolve_without_an_active_stream_but_cannot_bypass_the_control_gate()
    {
        using var client = Desktop();
        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var saved = registry.Allocate("Stored D215", new PanelDeviceCapabilities { Family = "jungle-leopard-d215", Surface = "lcd-wide" }, _ => false);
        var store = _factory.Services.GetRequiredService<StreamedPanelStore>();
        store.Save(new Dictionary<string, StreamedPanelRecord> { ["verified-usb-identity"] = new() { PanelDeviceId = saved.Id } });
        using var response = await client.PostAsJsonAsync($"/panel/devices/{saved.Id}/d215-restart", new { });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(saved.Id, registry.Get(saved.Id)?.Id);
    }
}
