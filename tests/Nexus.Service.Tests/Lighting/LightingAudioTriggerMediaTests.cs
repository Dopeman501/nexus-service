using System.Text.Json;
using System.Collections.Concurrent;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Media;
using Nexus.Service.Models.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public sealed class LightingAudioTriggerMediaTests
{
    private static LightingAudioTriggerSettings Read(string look) =>
        JsonSerializer.Deserialize("{\"looks\":[" + look + "]}", AppJsonContext.Default.LightingAudioTriggerSettings)!;

    [Fact]
    public void Media_snapshot_round_trips_without_changing_old_colour_and_shader_contracts()
    {
        var value = Read("{\"mode\":\"media\",\"mediaId\":\"my-clip_123\"}");
        Assert.True(LightingAudioTriggerPolicy.IsValid(value));
        var clone = LightingAudioTriggerPolicy.Clone(value)!;
        Assert.Equal("my-clip_123", JsonSerializer.SerializeToElement(clone, AppJsonContext.Default.LightingAudioTriggerSettings)
            .GetProperty("looks")[0].GetProperty("mediaId").GetString());
        Assert.True(LightingAudioTriggerPolicy.IsValid(Read("{\"mode\":\"static\",\"color\":\"#123456\"}")));
        Assert.True(LightingAudioTriggerPolicy.IsValid(Read("{\"mode\":\"animate\",\"effect\":\"plasma\"}")));
        var settings = new Nexus.Service.Persistence.NexusSettings();
        settings.Lighting.AudioTrigger = value;
        var preset = LightingPresetLooks.Capture(settings.Lighting);
        value.Looks.Clear();
        LightingPresetLooks.Apply(settings.Lighting, preset);
        Assert.Equal("my-clip_123", settings.Lighting.AudioTrigger!.Looks[0].MediaId);
        Assert.NotSame(preset.AudioTrigger, settings.Lighting.AudioTrigger);
    }

    [Theory]
    [InlineData("{\"mode\":\"media\",\"mediaId\":\"../clip\"}")]
    [InlineData("{\"mode\":\"media\",\"mediaId\":\"\"}")]
    [InlineData("{\"mode\":\"media\"}")]
    [InlineData("{\"mode\":\"media\",\"mediaId\":\"clip\",\"color\":\"#123456\"}")]
    [InlineData("{\"mode\":\"media\",\"mediaId\":\"clip\",\"effect\":\"plasma\"}")]
    [InlineData("{\"mode\":\"media\",\"mediaId\":\"clip\",\"state\":{}}")]
    [InlineData("{\"mode\":\"static\",\"color\":\"#123456\",\"mediaId\":\"clip\"}")]
    [InlineData("{\"mode\":\"animate\",\"effect\":\"plasma\",\"mediaId\":\"clip\"}")]
    public void Media_fields_are_exclusive_and_ids_are_local_library_names(string look) =>
        Assert.False(LightingAudioTriggerPolicy.IsValid(Read(look)));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MediaFramesEffect Frame(byte red, byte green, byte blue) => new([red, green, blue], 1, 1, 0);

    [Fact]
    public async Task Render_is_nonblocking_and_only_latest_requested_clip_loads_after_a_superseded_read()
    {
        var started = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<string>();
        var active = 0;
        var peak = 0;
        using var resources = new LightingAudioTriggerMediaResources(async (id, token) =>
        {
            calls.Enqueue(id);
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            try
            {
                if (id == "first") { started.TrySetResult(); await release.Task; }
                return id == "last" ? Frame(0, 0, 255) : Frame(255, 0, 0);
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var canvas = new CanvasBuffer();
        Assert.False(resources.TryRender("first", canvas, 1000));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(resources.TryRender("middle", canvas, 1000));
        Assert.False(resources.TryRender("last", canvas, 1000));
        release.TrySetResult();
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["first", "last"], calls.ToArray());
        Assert.Equal(1, peak);
        Assert.True(resources.TryRender("last", canvas, 1000));
        Assert.Equal(((byte)0, (byte)0, (byte)255), canvas.GetPixel(80, 40));
        Assert.False(resources.TryRender("first", canvas, 1001)); // superseded result was never published
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, calls.Count(id => id == "first"));
    }

    [Fact]
    public async Task Cache_holds_two_clips_and_evicts_the_least_recent_before_loading_another()
    {
        var calls = new ConcurrentQueue<string>();
        using var resources = new LightingAudioTriggerMediaResources((id, _) =>
        {
            calls.Enqueue(id);
            return Task.FromResult(Frame(1, 2, 3));
        });
        var canvas = new CanvasBuffer();
        async Task Load(string id)
        {
            resources.TryRender(id, canvas, 1000);
            await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(resources.TryRender(id, canvas, 1000));
        }
        await Load("one");
        await Load("two");
        await Load("one");
        Assert.Equal(2, calls.Count);
        await Load("three");
        await Load("one");
        Assert.Equal(3, calls.Count);
        await Load("two");
        Assert.Equal(4, calls.Count);
    }

    [Fact]
    public async Task Reset_cancels_old_generation_and_same_id_cannot_publish_its_old_frames()
    {
        var started = Signal();
        var release = Signal();
        var firstToken = CancellationToken.None;
        var calls = 0;
        using var resources = new LightingAudioTriggerMediaResources(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Frame(0, 255, 0);
            firstToken = token;
            started.TrySetResult();
            await release.Task; // deliberately ignores cancellation like a late I/O completion
            return Frame(255, 0, 0);
        });
        var canvas = new CanvasBuffer();
        resources.TryRender("clip", canvas, 0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        resources.Reset();
        resources.TryRender("clip", canvas, 1);
        Assert.True(firstToken.IsCancellationRequested);
        release.TrySetResult();
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(resources.TryRender("clip", canvas, 2));
        Assert.Equal(((byte)0, (byte)255, (byte)0), canvas.GetPixel(0, 0));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failures_do_not_retry_each_frame_and_dispose_cancels_loading_without_resurrection()
    {
        var calls = 0;
        using var resources = new LightingAudioTriggerMediaResources((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("synthetic missing media");
        });
        var canvas = new CanvasBuffer();
        resources.TryRender("broken", canvas, 0);
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(resources.TryRender("broken", canvas, 1));
        Assert.False(resources.TryRender("broken", canvas, 2));
        Assert.Equal(1, calls);
        resources.Reset();
        resources.TryRender("broken", canvas, 3);
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, calls);

        var started = Signal();
        var cancellation = Signal();
        using var pending = new LightingAudioTriggerMediaResources(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancellation.TrySetResult(); throw; }
            return Frame(0, 0, 0);
        });
        pending.TryRender("clip", canvas, 0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        pending.Dispose();
        await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await pending.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        pending.Dispose();
        Assert.False(pending.TryRender("clip", canvas, 1));
    }

    [Fact]
    public async Task Imported_RGB_frames_play_at_authored_rate_and_stop_reset_releases_loaded_resources()
    {
        using var fixture = new LibraryFixture();
        fixture.Write(160, 90, 2, 10);
        using var resources = new LightingAudioTriggerMediaResources(fixture.Library);
        using var controller = new LightingAudioTriggerController(
            look => look.Mode == "media" ? new LightingAudioTriggerMediaEffect(resources, look.MediaId!) : Frame(0, 255, 0), resources.Reset);
        var settings = new LightingAudioTriggerSettings { Enabled = true, SwitchDelay = 0,
            Looks = [new() { Color = "#00ff00" }, new() { Mode = "media", MediaId = "clip" }] };
        controller.Configure(settings);
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 1000);
        controller.Tick(true, 1000, 1000);
        controller.PublishSample(1, 1, 1, 1200);
        controller.Tick(true, 1200, 1200);
        Assert.Equal(((byte)0, (byte)255, (byte)0), controller.Canvas!.GetPixel(0, 0)); // pending keeps prior frame
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        controller.Tick(true, 1201, 1201);
        Assert.Equal(((byte)255, (byte)0, (byte)0), controller.Canvas!.GetPixel(0, 0));
        controller.Tick(true, 1301, 1301);
        Assert.Equal(((byte)0, (byte)0, (byte)255), controller.Canvas!.GetPixel(0, 0));
        controller.SetRunning(false);
        controller.Tick(true, 1302, 1302);
        Assert.Null(controller.Canvas);
        Assert.False(resources.TryRender("clip", new CanvasBuffer(), 0)); // stop cleared both slots
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(1, 90, 1, 0, 0)]
    [InlineData(160, 90, 301, 30, 0)]
    [InlineData(160, 90, 2, 0, 0)]
    [InlineData(160, 90, 2, 31, 0)]
    [InlineData(160, 90, 1, 0, -1)]
    [InlineData(160, 90, 1, 0, 1)]
    public async Task Corrupt_metadata_truncated_and_oversized_frames_never_become_ready(int width, int height, int frames, int fps, int delta)
    {
        using var fixture = new LibraryFixture();
        fixture.Write(width, height, frames, fps, delta);
        using var resources = new LightingAudioTriggerMediaResources(fixture.Library);
        var canvas = new CanvasBuffer();
        resources.TryRender("clip", canvas, 0);
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(resources.TryRender("clip", canvas, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_and_oversized_metadata_is_rejected_before_frame_allocation(bool oversized)
    {
        using var fixture = new LibraryFixture();
        fixture.Write(160, 90, 1, 0);
        File.WriteAllText(Path.Combine(fixture.Library.GetItemDir("clip"), "meta.json"), oversized ? new string(' ', 16385) : "{");
        using var resources = new LightingAudioTriggerMediaResources(fixture.Library);
        var canvas = new CanvasBuffer();
        Assert.False(resources.TryRender("clip", canvas, 0));
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(resources.TryRender("clip", canvas, 1));
    }

    private sealed class LibraryFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-trigger-media-" + Guid.NewGuid().ToString("N"));
        internal MediaLibrary Library { get; }
        internal LibraryFixture() => Library = new MediaLibrary(_root);
        internal void Write(int width, int height, int frames, int fps, int delta = 0)
        {
            Directory.CreateDirectory(Library.GetItemDir("clip"));
            var item = new MediaItem { Id = "clip", Width = width, Height = height, Frames = frames, Fps = fps };
            File.WriteAllText(Path.Combine(Library.GetItemDir("clip"), "meta.json"), JsonSerializer.Serialize(item, AppJsonContext.Default.MediaItem));
            var bytes = new byte[width * height * 3 * frames + delta];
            for (var index = 0; index < bytes.Length; index += 3)
                bytes[index + (index < width * height * 3 ? 0 : Math.Min(2, bytes.Length - index - 1))] = 255;
            File.WriteAllBytes(Library.GetFramesBinPath("clip"), bytes);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
