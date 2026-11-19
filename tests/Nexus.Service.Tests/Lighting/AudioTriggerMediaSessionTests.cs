using System.Text.Json;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Media;
using Nexus.Service.Models.Media;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public sealed class AudioTriggerMediaSessionTests
{
    [Fact]
    public async Task A_repeated_tick_followed_by_a_two_frame_advance_is_already_prefetched_for_all_ids()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 32).Select(index => "clip" + index).ToArray();
        foreach (var id in ids) fixture.Write(id, 5);
        var session = new AudioTriggerMediaSession(fixture.Library);
        try
        {
            session.ReconcileDemand(ids);
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
            var canvas = new CanvasBuffer();
            foreach (var (tick, frame) in new[] { (1000d, 0), (1032d, 0), (1067d, 2), (1101d, 3) })
            {
                foreach (var id in ids)
                {
                    Assert.True(session.TryRender(id, canvas, tick));
                    Assert.Equal((byte)frame, canvas.GetPixel(0, 0).r);
                }
                await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.InRange(session.Usage.WindowBuffers, 1, 96);
            Assert.InRange(session.Usage.DecodedBytes, 1, 4_147_200);
        }
        finally { session.Dispose(); await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task Repeated_resets_cannot_allocate_replacement_buffers_while_both_old_readers_remain_live()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 32).Select(index => "clip" + index).ToArray();
        foreach (var id in ids) fixture.Write(id, 3);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var block = false;
        var session = new AudioTriggerMediaSession(fixture.Library, beforeRead: async (_, _, _) =>
        {
            if (!Volatile.Read(ref block)) return;
            if (Interlocked.Increment(ref held) == 2) both.TrySetResult();
            await release.Task; // Models an operation whose cancellation notification does not complete its use.
        });
        try
        {
            session.ReconcileDemand(ids);
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Volatile.Write(ref block, true);
            var canvas = new CanvasBuffer();
            foreach (var id in ids) Assert.True(session.TryRender(id, canvas, 1000));
            await both.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var retained = session.Usage.DecodedBytes;
            for (var index = 0; index < 50; index++)
            {
                session.Reset();
                session.ReconcileDemand(ids);
                Assert.Equal(2, session.Usage.Readers);
                Assert.Equal(retained, session.Usage.DecodedBytes);
                Assert.InRange(session.Usage.Pending, 0, 32);
                Assert.InRange(session.Usage.WindowBuffers, 0, 96);
            }
            Volatile.Write(ref block, false);
            release.TrySetResult();
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var id in ids) Assert.True(session.TryRender(id, canvas, 2000));
        }
        finally
        {
            Volatile.Write(ref block, false);
            release.TrySetResult();
            session.Dispose();
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Thirtytwo_active_media_ids_are_fair_and_share_ninetysix_frame_buffers()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 32).Select(index => "clip" + index).ToArray();
        foreach (var id in ids) fixture.Write(id, 3);
        var session = new AudioTriggerMediaSession(fixture.Library);
        try
        {
            session.ReconcileDemand(ids);
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(20));
            var canvas = new CanvasBuffer();
            for (var frame = 0; frame < 6; frame++)
            {
                foreach (var id in ids)
                {
                    Assert.True(session.TryRender(id, canvas, 1000 + frame * 34));
                    Assert.Equal((byte)(frame % 3), canvas.GetPixel(0, 0).r);
                }
                await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.InRange(session.Usage.WindowBuffers, 1, 96);
            Assert.InRange(session.Usage.DecodedBytes, 1, 4_147_200);
            Assert.Equal(32, session.Usage.ActiveIds);
            session.ReconcileDemand(Array.Empty<string>());
            session.ReconcileDemand(ids);
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(session.Usage.WindowBuffers, 1, 96);
        }
        finally { session.Dispose(); await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public async Task Two_hot_clips_keep_immediate_return_clock_pixels_and_survive_original_deletion(int frames)
    {
        using var fixture = new Fixture();
        var bytes = fixture.Write("a", frames);
        fixture.Write("b", frames);
        using var referenceA = new MediaFramesEffect(bytes, 160, 90, 30);
        using var referenceB = new MediaFramesEffect(bytes, 160, 90, 30);
        var expected = new CanvasBuffer();
        var actual = new CanvasBuffer();
        var session = new AudioTriggerMediaSession(fixture.Library);
        try
        {
            session.ReconcileDemand(new[] { "a", "b" });
            await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var (id, tick) in new[] { ("a", 1000d), ("b", 1000d), ("a", 1034d), ("b", 1500d),
                ("a", 1900d), ("b", 6000d), ("a", 9000d) })
            {
                var reference = id == "a" ? referenceA : referenceB;
                reference.RenderFrame(expected, tick);
                session.ReconcileDemand(new[] { id });
                Assert.True(session.TryRender(id, actual, tick));
                Assert.True(expected.Pixels.SequenceEqual(actual.Pixels));
            }
            Assert.InRange(session.Usage.DecodedBytes, 1, AudioTriggerMediaSession.MaximumDecodedBytes);
            Assert.True(fixture.Library.DeleteItem("a"));
            Assert.True(session.TryRender("a", actual, 9034));
        }
        finally { session.Dispose(); await session.PendingLoad.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-media-session-" + Guid.NewGuid().ToString("N"));
        internal MediaLibrary Library { get; }
        internal Fixture() => Library = new(Path.Combine(Root, "library"));
        internal byte[] Write(string id, int frames)
        {
            Directory.CreateDirectory(Library.GetItemDir(id));
            var item = new MediaItem { Id = id, Width = 160, Height = 90, Frames = frames, Fps = 30 };
            File.WriteAllText(Path.Combine(Library.GetItemDir(id), "meta.json"), JsonSerializer.Serialize(item, AppJsonContext.Default.MediaItem));
            var bytes = new byte[frames * 43_200];
            for (var frame = 0; frame < frames; frame++) Array.Fill(bytes, (byte)frame, frame * 43_200, 43_200);
            File.WriteAllBytes(Library.GetFramesBinPath(id), bytes);
            return bytes;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
