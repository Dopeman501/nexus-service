using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>/lighting/key-reactive* through the real pipeline and source-generated JSON, against one injected keyboard card.</summary>
public class KeyReactiveRoutesTests
{
    private const string Card = "kb:dev:z0";
    private const string Device = "kb:dev";

    private static (KeyReactiveHostFactory Factory, HttpClient Client) Boot(bool core = false)
    {
        var factory = new KeyReactiveHostFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        var n = 40;
        var u = new float[n];
        var v = new float[n];
        var names = new string?[n];
        for (var i = 0; i < n; i++)
        {
            u[i] = (i % 10) / 9f;
            v[i] = (i / 10) / 3f;
            names[i] = "Key: " + "1234567890QWERTYUIOPASDFGHJKL;ZXCVBNM,./"[i];
        }
        var frame = core
            ? new DeviceFrame(7, Card, 10)
            {
                Archetype = "keyboard", DeviceId = Device,
                ReactionLayout = K55CoreReactionLayout.Create(Lighting.K55CoreReactionTests.Core(), 0, 10),
            }
            : new DeviceFrame(7, Card, n) { Archetype = "keyboard", LedU = u, LedV = v, LedKeys = names, DeviceId = Device };
        factory.Services.GetRequiredService<LightingEngine>().UpdateDevices(new[]
        {
            frame,
        });
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Ordered_custom_palette_roundtrips_put_get_store_and_preview(bool core)
    {
        var (factory, client) = Boot(core);
        using (factory)
        {
            const string body = """{"enabled":true,"keyboardLayout":"UK","effect":"fade","colorMode":"custom","color":"#123456","colors":["#000000","#FF0000","#000000","bad","#00ff00"],"background":"effect"}""";
            var put = await client.PutAsync($"/lighting/key-reactive/{Card}", Json(body));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using var saved = System.Text.Json.JsonDocument.Parse(await put.Content.ReadAsStringAsync());
            Assert.Equal("#000000", saved.RootElement.GetProperty("color").GetString());
            Assert.Equal(new[] { "#000000", "#ff0000", "#00ff00" },
                saved.RootElement.GetProperty("colors").EnumerateArray().Select(c => c.GetString()));
            using var get = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            var config = get.RootElement.GetProperty("devices")[0].GetProperty("config");
            Assert.Equal(saved.RootElement.GetProperty("colors").GetRawText(), config.GetProperty("colors").GetRawText());
            Assert.Equal(new[] { "#000000", "#ff0000", "#00ff00" },
                factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions[Device].Colors);
            var preview = await client.PostAsync($"/lighting/key-reactive/{Card}/preview", Json(body));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var frames = System.Text.Json.JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.NotEmpty(Convert.FromBase64String(frames.RootElement.GetProperty("frames").GetString()!));
        }
    }

    [Fact]
    public async Task Lists_the_card_and_stores_its_config_under_the_device()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var list = await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync();
            Assert.Contains($"\"id\":\"{Card}\"", list);
            Assert.Contains($"\"deviceId\":\"{Device}\"", list);
            Assert.Contains("\"frameIndex\":7", list);
            Assert.Contains("\"namedKeys\":40", list);

            var put = await client.PutAsync($"/lighting/key-reactive/{Card}",
                Json("{\"enabled\":true,\"effect\":\"sparks\",\"colorMode\":\"random\",\"color\":\"#00ff00\",\"speed\":9,\"size\":1,\"background\":\"dim\"}"));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var saved = await put.Content.ReadAsStringAsync();
            Assert.Contains("\"speed\":3", saved);
            Assert.Contains("\"effect\":\"sparks\"", saved);

            var stored = factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions;
            Assert.True(stored.ContainsKey(Device));
            Assert.False(stored.ContainsKey(Card));
        }
    }

    [Fact]
    public async Task Preview_and_press_answer_for_a_keyboard_and_404_otherwise()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var body = "{\"enabled\":true,\"effect\":\"ripple\",\"colorMode\":\"rainbow\",\"color\":\"#ff0000\",\"speed\":1,\"size\":1,\"background\":\"effect\"}";
            var preview = await client.PostAsync($"/lighting/key-reactive/{Card}/preview", Json(body));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var text = await preview.Content.ReadAsStringAsync();
            Assert.Contains("\"frameCount\":", text);
            Assert.Contains("\"frames\":\"", text);

            var press = await client.PostAsync($"/lighting/key-reactive/{Card}/press", Json("{\"key\":\"G\"}"));
            Assert.Equal(HttpStatusCode.OK, press.StatusCode);

            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/lighting/key-reactive/nope", Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/lighting/key-reactive/nope/press", Json("{}"))).StatusCode);
        }
    }

    [Fact]
    public async Task Core_is_discoverable_without_layout_but_enable_press_and_preview_require_a_selection()
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            Assert.Null(factory.Services.GetService<RgbBridge>());
            Assert.Null(factory.Services.GetService<OpenRgbProcessManager>());
            var list = await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync();
            Assert.Contains("\"zoneLighting\":true", list);
            Assert.Contains("\"supportedEffects\":[\"fade\",\"ripple\"]", list);
            Assert.Contains("\"keyboardLayouts\":[\"US\",\"UK\",\"BR\",\"JP\"]", list);
            Assert.Contains("\"ledCount\":10", list);
            var invalid = """{"enabled":true,"effect":"fade","keyboardLayout":"DE"}""";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync($"/lighting/key-reactive/{Card}", Json(invalid))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/lighting/key-reactive/{Card}/press", Json("{}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/lighting/key-reactive/{Card}/preview", Json(invalid))).StatusCode);
            Assert.False(factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions.ContainsKey(Device));
        }
    }

    [Fact]
    public async Task Core_put_and_preview_share_native_supported_effects_and_explicit_regional_layout()
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            var body = """{"enabled":true,"effect":"sparks","keyboardLayout":"UK","color":"#ff0000"}""";
            var put = await client.PutAsync($"/lighting/key-reactive/{Card}", Json(body));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            var text = await put.Content.ReadAsStringAsync();
            Assert.Contains("\"effect\":\"ripple\"", text);
            Assert.Contains("\"keyboardLayout\":\"UK\"", text);
            var stored = factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions[Device];
            Assert.Equal("UK", stored.KeyboardLayout);
            Assert.Equal("ripple", stored.Effect);
            var preview = await client.PostAsync($"/lighting/key-reactive/{Card}/preview", Json(body));
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var result = System.Text.Json.JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement;
            Assert.True(result.GetProperty("zoneLighting").GetBoolean());
            Assert.Equal(21.5f, result.GetProperty("width").GetSingle());
            Assert.Equal(5f, result.GetProperty("height").GetSingle());
            Assert.Equal(10, result.GetProperty("x").GetArrayLength());
            var expected = KeyReactionPreview.Fps * 3200 / 1000 * 10 * 3;
            Assert.Equal(expected, System.Convert.FromBase64String(result.GetProperty("frames").GetString()!).Length);
        }
    }

    [Fact]
    public async Task Imported_core_configs_are_resolved_without_get_writing_back_to_persistence()
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.Lighting.KeyReactions[Device] = new KeyReaction { Enabled = true, Effect = "heatmap", KeyboardLayout = "UK" });
            var response = await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync();
            Assert.Contains("\"effect\":\"ripple\"", response);
            Assert.Equal("heatmap", store.Load().Lighting.KeyReactions[Device].Effect);
            store.Update(s => s.Lighting.KeyReactions[Device] = new KeyReaction { Enabled = true, Effect = "fade" });
            response = await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync();
            Assert.Contains("\"enabled\":false", response);
            Assert.True(store.Load().Lighting.KeyReactions[Device].Enabled);
        }
    }

    [Fact]
    public async Task Scoped_requests_reject_a_retired_profile_before_mutation_or_press()
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            using var initial = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            var epoch = initial.RootElement.GetProperty("epoch").GetString()!;
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.AudioTriggerControlEpoch = Guid.NewGuid().ToString("N"));
            var body = """{"enabled":true,"effect":"fade","keyboardLayout":"UK"}""";
            var path = $"/lighting/key-reactive/{Card}";
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"{path}?expectedEpoch={epoch}", Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{path}/preview?expectedEpoch={epoch}", Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"{path}/press?expectedEpoch={epoch}", Json("{}"))).StatusCode);
            Assert.False(store.Load().Lighting.KeyReactions.ContainsKey(Device));
            var current = store.Load().AudioTriggerControlEpoch;
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"{path}?expectedEpoch={current}", Json(body))).StatusCode);
            Assert.Equal("UK", store.Load().Lighting.KeyReactions[Device].KeyboardLayout);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync($"{path}?expectedEpoch=invalid", Json(body))).StatusCode);
        }
    }

    [Fact]
    public async Task Epoch_is_rechecked_inside_the_conditional_store_transaction()
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            using var state = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            var epoch = state.RootElement.GetProperty("epoch").GetString()!;
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var changes = 0; store.OnChanged += () => changes++;
            factory.BeforeNextConditionalUpdate = current => current.Update(settings => settings.AudioTriggerControlEpoch = Guid.NewGuid().ToString("N"));
            var body = """{"enabled":true,"effect":"fade","keyboardLayout":"UK"}""";
            var response = await client.PutAsync($"/lighting/key-reactive/{Card}?expectedEpoch={epoch}", Json(body));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.False(store.Load().Lighting.KeyReactions.ContainsKey(Device));
            Assert.Equal(1, changes); // Only the injected profile activation; the rejected write is silent.
        }
    }

    [Fact]
    public async Task Core_revision_is_stable_and_rejects_retired_projection_on_all_scoped_operations()
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            using var initial = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            var epoch = initial.RootElement.GetProperty("epoch").GetString()!;
            var revision = initial.RootElement.GetProperty("devices")[0].GetProperty("layoutRevision").GetString()!;
            Assert.Equal("c1:0123456789:000", revision);
            using var repeated = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            Assert.Equal(revision, repeated.RootElement.GetProperty("devices")[0].GetProperty("layoutRevision").GetString());
            var frame = new DeviceFrame(7, Card, 2)
            {
                Archetype = "keyboard", DeviceId = Device, LedDisabled = [false, true],
                ReactionLayout = K55CoreReactionLayout.Create(Lighting.K55CoreReactionTests.Core(), 8, 2),
            };
            factory.Services.GetRequiredService<LightingEngine>().UpdateDevices([frame]);
            var body = """{"enabled":true,"effect":"fade","keyboardLayout":"UK"}""";
            var suffix = $"?expectedEpoch={epoch}&expectedLayoutRevision={revision}";
            var path = $"/lighting/key-reactive/{Card}";
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync(path + suffix, Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + "/preview" + suffix, Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path + "/press" + suffix, Json("{\"led\":0}"))).StatusCode);
            Assert.False(factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions.ContainsKey(Device));
            using var refreshed = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            var current = refreshed.RootElement.GetProperty("devices")[0].GetProperty("layoutRevision").GetString();
            Assert.Equal("c1:89:002", current);
            suffix = $"?expectedEpoch={epoch}&expectedLayoutRevision={current}";
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsync(path + suffix, Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path + "/preview" + suffix, Json(body))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path + "/press" + suffix, Json("{\"led\":0}"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path + "?expectedLayoutRevision=bad", Json(body))).StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Core_projection_is_rechecked_inside_the_store_scope(bool preview)
    {
        var (factory, client) = Boot(core: true);
        using (factory)
        {
            using var state = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/lighting/key-reactive")).Content.ReadAsStringAsync());
            var epoch = state.RootElement.GetProperty("epoch").GetString()!;
            var revision = state.RootElement.GetProperty("devices")[0].GetProperty("layoutRevision").GetString()!;
            var engine = factory.Services.GetRequiredService<LightingEngine>();
            Action<IConfigStore> replace = _ => engine.UpdateDevices([new DeviceFrame(7, Card, 2)
            {
                Archetype = "keyboard", DeviceId = Device,
                ReactionLayout = K55CoreReactionLayout.Create(Lighting.K55CoreReactionTests.Core(), 8, 2),
            }]);
            if (preview) factory.BeforeNextRead = replace;
            else factory.BeforeNextConditionalUpdate = replace;
            var path = $"/lighting/key-reactive/{Card}" + (preview ? "/preview" : "")
                + $"?expectedEpoch={epoch}&expectedLayoutRevision={revision}";
            var body = """{"enabled":true,"effect":"fade","keyboardLayout":"UK"}""";
            var response = preview ? await client.PostAsync(path, Json(body)) : await client.PutAsync(path, Json(body));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.False(factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.KeyReactions.ContainsKey(Device));
        }
    }

    private sealed class KeyReactiveHostFactory : NexusAppFactory
    {
        internal Action<IConfigStore>? BeforeNextConditionalUpdate;
        internal Action<IConfigStore>? BeforeNextRead;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<RgbBridge>();
                services.RemoveAll<OpenRgbProcessManager>();
                services.RemoveAll<ILightingDeviceProvider>();
                services.AddSingleton<ILightingDeviceProvider>(sp => new StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
                var inner = (IConfigStore)services.Last(descriptor => descriptor.ServiceType == typeof(IConfigStore)).ImplementationInstance!;
                services.RemoveAll<IConfigStore>();
                services.AddSingleton<IConfigStore>(new ConditionalRaceStore(inner, this));
            });
        }
    }

    private sealed class ConditionalRaceStore(IConfigStore inner, KeyReactiveHostFactory owner) : IConfigStore, IDisposable
    {
        public string SettingsPath => inner.SettingsPath;
        public NexusSettings Load() => inner.Load();
        public void Read(Action<NexusSettings> reader)
        {
            System.Threading.Interlocked.Exchange(ref owner.BeforeNextRead, null)?.Invoke(inner);
            inner.Read(reader);
        }
        public void Update(Action<NexusSettings> mutator) => inner.Update(mutator);
        public bool TryUpdate(Func<NexusSettings, bool> mutator)
        {
            System.Threading.Interlocked.Exchange(ref owner.BeforeNextConditionalUpdate, null)?.Invoke(inner);
            return inner.TryUpdate(mutator);
        }
        public void FlushNow() => inner.FlushNow();
        public void Reload() => inner.Reload();
        public event Action? OnChanged { add => inner.OnChanged += value; remove => inner.OnChanged -= value; }
        public void Dispose() { if (inner is IDisposable disposable) disposable.Dispose(); }
    }
}
