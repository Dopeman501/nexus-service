using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Integration;

/// <summary>Minimal route-only TestServer. Product Program, workers, files and actuator services are never constructed.</summary>
public sealed class EfficiencyDiagnosticRouteTests
{
    private const string Run="22222222222222222222222222222222";
    private sealed class Store : IConfigStore
    {
        private readonly NexusSettings _settings=new() { Auth=new() {Token="private-unit-fixture"} };
        public NexusSettings Load()=>_settings;
        public void Update(Action<NexusSettings> action)=>throw new InvalidOperationException("No settings writes permitted");
        public void FlushNow()=>throw new InvalidOperationException();
        public string SettingsPath=>"unused";
        public void Reload()=>throw new InvalidOperationException();
        public event Action? OnChanged {add{} remove{}}
    }
    private static async Task<WebApplication> Host(IPAddress? remote=null,Func<long>? clock=null)
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new TokenService(new Store()));
        builder.Services.AddSingleton(new EfficiencyDiagnosticSession(clock:clock,schedule:false));
        var app=builder.Build();app.Use((ctx,next)=>{ctx.Connection.RemoteIpAddress=remote??IPAddress.Loopback;return next();});
        app.MapEfficiencyDiagnosticEndpoints();await app.StartAsync();return app;
    }
    [Theory]
    [InlineData(null,null,"localhost")]
    [InlineData("Bearer wrong",null,"localhost")]
    [InlineData("Bearer private-unit-fixture","https://evil.example","localhost")]
    [InlineData("Bearer private-unit-fixture",null,"evil.example")]
    public async Task Explicit_desktop_bearer_local_host_and_origin_are_required(string? auth,string? origin,string host)
    {
        await using var app=await Host();using var client=app.GetTestClient();
        using var request=new HttpRequestMessage(HttpMethod.Get,"/diagnostics/efficiency/session");
        request.Headers.Host=host;if(auth is not null)request.Headers.TryAddWithoutValidation("Authorization",auth);
        if(origin is not null)request.Headers.TryAddWithoutValidation("Origin",origin);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(request)).StatusCode);
    }
    [Fact] public async Task No_query_token_or_remote_address_can_activate()
    {
        await using var app=await Host(IPAddress.Parse("192.0.2.1"));using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","private-unit-fixture");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/diagnostics/efficiency/session")).StatusCode);
        await using var local=await Host();using var query=local.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await query.GetAsync("/diagnostics/efficiency/session?token=private-unit-fixture")).StatusCode);
    }
    [Theory] [InlineData("null")] [InlineData("{")] [InlineData("{}")]
    public async Task Invalid_and_incomplete_requests_do_not_activate(string body)
    {
        await using var app=await Host();using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","private-unit-fixture");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/diagnostics/efficiency/session",new StringContent(body,Encoding.UTF8,"application/json"))).StatusCode);
        Assert.False(app.Services.GetRequiredService<EfficiencyDiagnosticSession>().Snapshot().NativeActive);
    }
    [Fact] public async Task Bound_body_is_rejected_without_settings_or_host_construction()
    {
        await using var app=await Host();using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","private-unit-fixture");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/diagnostics/efficiency/session",new StringContent(new string('x',1025),Encoding.UTF8,"application/json"))).StatusCode);
    }
    [Fact] public async Task Authorized_route_starts_in_memory_and_reports_unacknowledged_overlay()
    {
        await using var app=await Host();using var client=app.GetTestClient();var owner=app.Services.GetRequiredService<EfficiencyDiagnosticSession>();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","private-unit-fixture");
        var response=await client.PostAsJsonAsync("/diagnostics/efficiency/session",new EfficiencyLeaseRequest{ExpectedServiceGeneration=owner.Generation,RunId=Run,DurationSeconds=30});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.True(json.RootElement.GetProperty("nativeActive").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("overlayInstance",out var ack) && ack.ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await client.GetAsync("/diagnostics/efficiency/session")).StatusCode);
    }
    [Fact] public async Task Logical_ack_and_cancellation_are_bound_to_the_current_run()
    {
        long now=0;await using var app=await Host(clock:()=>now);using var client=app.GetTestClient();
        var owner=app.Services.GetRequiredService<EfficiencyDiagnosticSession>();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","private-unit-fixture");
        var request=new EfficiencyLeaseRequest{ExpectedServiceGeneration=owner.Generation,RunId=Run,DurationSeconds=30};
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/diagnostics/efficiency/session",request)).StatusCode);
        var active=owner.Snapshot();now=250;
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/diagnostics/efficiency/session/ack",new EfficiencyLeaseAck{ServiceGeneration=owner.Generation,Revision=active.Revision,RunId=Run,OverlayInstance="44444444444444444444444444444444"})).StatusCode);
        Assert.Equal("44444444444444444444444444444444",owner.Snapshot().OverlayInstance);
        now=500;request.ExpectedRevision=active.Revision;
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/diagnostics/efficiency/session/cancel",request)).StatusCode);
        Assert.False(owner.Snapshot().NativeActive);
    }
}
