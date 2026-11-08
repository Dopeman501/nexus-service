using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelAudioTriggerPreviewTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds;
        internal long WallShift;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1700000000000 + Milliseconds + WallShift);
    }

    private static PanelAudioTriggerPreviewRequest Request(string session, long sequence, string? color = "#abcdef") => new()
    {
        SessionId = session, Sequence = sequence,
        Background = color is null ? null : new D215PanelBackgroundDto { Mode = "solid", Color = color },
    };

    [Fact]
    public void Request_budget_accepts_256_per_monotonic_second_and_resets_at_the_boundary()
    {
        var clock = new Clock();
        var store = new PanelAudioTriggerPreviewStore(clock);
        for (var request = 0; request < 256; request++) Assert.True(store.TryAcceptRequest());
        Assert.False(store.TryAcceptRequest());
        clock.WallShift = 10000000;
        clock.Milliseconds = 999;
        Assert.False(store.TryAcceptRequest());
        clock.Milliseconds = 1000;
        for (var request = 0; request < 256; request++) Assert.True(store.TryAcceptRequest());
        Assert.False(store.TryAcceptRequest());
        clock.Milliseconds = 10000;
        Assert.True(store.TryAcceptRequest());
    }

    [Fact]
    public void Concurrent_requests_share_one_bounded_budget()
    {
        var store = new PanelAudioTriggerPreviewStore(new Clock());
        var accepted = 0;
        Parallel.For(0, 1024, _ => { if (store.TryAcceptRequest()) Interlocked.Increment(ref accepted); });
        Assert.Equal(256, accepted);
    }

    [Fact]
    public void Lease_expires_without_client_cleanup_and_renewal_uses_monotonic_time()
    {
        var clock = new Clock();
        var store = new PanelAudioTriggerPreviewStore(clock);
        var session = Guid.NewGuid().ToString();
        Assert.True(store.TryUpdate("one", Request(session, 0), out var frame));
        Assert.Equal(1700000006000, frame!.ExpiresAtUnixMs);
        clock.Milliseconds = 5999;
        clock.WallShift = 10000000;
        Assert.NotNull(store.Get("one"));
        Assert.True(store.TryUpdate("one", Request(session, 1), out var renewed));
        Assert.True(renewed!.Revision > frame.Revision);
        clock.Milliseconds = 11999;
        Assert.Null(store.Get("one"));
    }

    [Fact]
    public void State_clones_input_and_readback_and_keeps_device_ownership_separate()
    {
        var store = new PanelAudioTriggerPreviewStore(new Clock());
        var session = Guid.NewGuid().ToString();
        var request = Request(session, 0);
        Assert.True(store.TryUpdate("one", request, out var frame));
        request.Background!.Color = "#ffffff";
        frame!.Background!.Color = "#111111";
        Assert.Equal("#abcdef", store.Get("one")!.Background!.Color);
        var read = store.Get("one")!;
        read.Background!.Color = "#222222";
        Assert.Equal("#abcdef", store.Get("one")!.Background!.Color);
        Assert.Null(store.Get("two"));
        Assert.False(store.TryUpdate("two", Request(session, 1, null), out _));
        Assert.NotNull(store.Get("one"));
    }

    [Fact]
    public void Retired_sessions_cannot_reacquire_until_the_bounded_tombstone_window_passes()
    {
        var clock = new Clock();
        var store = new PanelAudioTriggerPreviewStore(clock);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        Assert.True(store.TryUpdate("one", Request(first, 5), out _));
        Assert.True(store.TryUpdate("one", Request(second, 0), out _));
        Assert.False(store.TryUpdate("one", Request(first, 6), out _));
        Assert.False(store.TryUpdate("one", Request(first, 7, null), out _));
        Assert.False(store.TryUpdate("one", Request(second, 0), out _));
        Assert.True(store.TryUpdate("one", Request(second, 1, null), out var clear));
        Assert.Null(clear!.Background);
        Assert.Null(store.Get("one"));
        clock.Milliseconds = 30001;
        Assert.True(store.TryUpdate("one", Request(first, 8), out _));
    }

    [Fact]
    public void Collection_and_session_limits_bound_memory_and_expired_entries_are_reclaimed()
    {
        var clock = new Clock();
        var store = new PanelAudioTriggerPreviewStore(clock);
        for (var index = 0; index < PanelAudioTriggerPreviewStore.MaximumDevices; index++)
            Assert.True(store.TryUpdate($"device-{index}", Request(Guid.NewGuid().ToString(), 0), out _));
        Assert.False(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        clock.Milliseconds = 36001;
        Assert.True(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        for (var index = 0; index < PanelAudioTriggerPreviewStore.MaximumRetiredSessions; index++)
            Assert.True(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        Assert.False(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
    }
}
