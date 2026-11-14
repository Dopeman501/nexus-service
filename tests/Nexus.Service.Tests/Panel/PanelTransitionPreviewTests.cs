using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelTransitionPreviewTests
{
    private sealed class Clock : TimeProvider
    {
        internal long Milliseconds;
        internal long WallShift;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1700000000000 + Milliseconds + WallShift);
    }

    private static PanelTransitionPreviewRequest Request(string session, long sequence, string? previewId = null, int duration = 1350) => new()
    {
        SessionId = session, Sequence = sequence,
        Preview = new() { PreviewId = previewId ?? Guid.NewGuid().ToString(), Effect = "curtains:wave", DurationMs = duration },
    };
    private static PanelTransitionPreviewRequest Clear(string session, long sequence) => new() { SessionId = session, Sequence = sequence, Preview = null };

    [Fact]
    public void Budget_is_shared_bounded_and_monotonic_across_concurrent_requests()
    {
        var clock = new Clock();
        var store = new PanelTransitionPreviewStore(clock);
        var accepted = 0;
        Parallel.For(0, 1024, _ => { if (store.TryAcceptRequest()) Interlocked.Increment(ref accepted); });
        Assert.Equal(256, accepted);
        clock.Milliseconds = 999; clock.WallShift = 100000000;
        Assert.False(store.TryAcceptRequest());
        clock.Milliseconds = 1000;
        for (var i = 0; i < 256; i++) Assert.True(store.TryAcceptRequest());
        Assert.False(store.TryAcceptRequest());
    }

    [Theory]
    [InlineData(80)]
    [InlineData(1350)]
    [InlineData(5000)]
    public void One_shot_uses_server_start_and_monotonic_expiry_without_renewal(int duration)
    {
        var clock = new Clock();
        var store = new PanelTransitionPreviewStore(clock);
        Assert.True(store.TryUpdate("one", Request(Guid.NewGuid().ToString(), 0, duration: duration), out var frame));
        Assert.Equal(1700000000150, frame!.StartedAtUnixMs);
        Assert.Equal(1700000006150 + duration, frame.ExpiresAtUnixMs);
        clock.Milliseconds = 6149 + duration; clock.WallShift = 100000000;
        Assert.NotNull(store.Get("one"));
        clock.Milliseconds++;
        Assert.Null(store.Get("one"));
    }

    [Fact]
    public void Repeated_preview_id_never_restarts_or_extends_the_preview_even_with_a_new_sequence()
    {
        var clock = new Clock();
        var store = new PanelTransitionPreviewStore(clock);
        var session = Guid.NewGuid().ToString();
        var first = Request(session, 1);
        Assert.True(store.TryUpdate("one", first, out var frame));
        clock.Milliseconds = 1000;
        Assert.False(store.TryUpdate("one", first with { Sequence = 2 }, out _));
        Assert.Equal(frame, store.Get("one"));
        Assert.True(store.TryUpdate("one", Request(session, 3), out _));
        Assert.False(store.TryUpdate("one", first with { Sequence = 4 }, out _));
    }

    [Fact]
    public void Newer_editor_wins_and_old_cleanup_sequences_and_repeated_ids_cannot_replace_it()
    {
        var clock = new Clock();
        var store = new PanelTransitionPreviewStore(clock);
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        var input = Request(first, 5);
        Assert.True(store.TryUpdate("one", input, out var original));
        Assert.NotSame(input.Preview, original!.Preview);
        Assert.NotSame(original.Preview, store.Get("one")!.Preview);
        Assert.Null(store.Get("other"));
        Assert.True(store.TryUpdate("one", Request(second, 0), out var latest));
        Assert.False(store.TryUpdate("one", Request(first, 6), out _));
        Assert.False(store.TryUpdate("one", Clear(first, 7), out _));
        Assert.False(store.TryUpdate("one", Request(second, 0), out _));
        Assert.Equal(latest, store.Get("one"));
        Assert.True(store.TryUpdate("one", Clear(second, 1), out var clear));
        Assert.Null(clear!.Preview);
        Assert.Equal(0, clear.StartedAtUnixMs);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), clear.ExpiresAtUnixMs);
        Assert.Null(store.Get("one"));
        clock.Milliseconds = 30001;
        Assert.True(store.TryUpdate("one", Request(first, 8), out _));
    }

    [Fact]
    public void Invalid_requests_and_unowned_release_do_not_create_or_replace_entries()
    {
        var store = new PanelTransitionPreviewStore(new Clock());
        var session = Guid.NewGuid().ToString();
        Assert.False(store.TryUpdate("unknown", Clear(session, 0), out _));
        Assert.False(store.TryUpdate("one", Request("bad", 0), out _));
        Assert.False(store.TryUpdate("one", Request(session, -1), out _));
        Assert.False(store.TryUpdate("one", Request(session, 9007199254740992), out _));
        Assert.False(store.TryUpdate("one", Request(Guid.Empty.ToString(), 0), out _));
        Assert.False(store.TryUpdate("one", Request(session, 0, Guid.Empty.ToString()), out _));
        Assert.False(store.TryUpdate("one", Request(session, 0, "bad"), out _));
        Assert.False(store.TryUpdate("one", Request(session, 0, duration: 79), out _));
        Assert.False(store.TryUpdate("one", Request(session, 0, duration: 5001), out _));
        var invalidEffect = Request(session, 0);
        Assert.False(store.TryUpdate("one", invalidEffect with { Preview = invalidEffect.Preview! with { Effect = "default" } }, out _));
        Assert.False(PanelTransitionPreviewStore.IsValid(null));
        Assert.Null(store.Get("one"));
    }

    [Fact]
    public void Device_session_and_preview_id_collections_are_bounded_and_expired_entries_reclaimed()
    {
        var clock = new Clock();
        var store = new PanelTransitionPreviewStore(clock);
        for (var i = 0; i < PanelTransitionPreviewStore.MaximumDevices; i++)
            Assert.True(store.TryUpdate($"device-{i}", Request(Guid.NewGuid().ToString(), 0), out _));
        Assert.False(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        clock.Milliseconds = 41151;
        Assert.True(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        for (var i = 0; i < PanelTransitionPreviewStore.MaximumRetiredSessions; i++)
            Assert.True(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        Assert.False(store.TryUpdate("excess", Request(Guid.NewGuid().ToString(), 0), out _));
        var session = Guid.NewGuid().ToString();
        for (var i = 0; i < PanelTransitionPreviewStore.MaximumPreviewIds; i++)
            Assert.True(store.TryUpdate("ids", Request(session, i), out _));
        Assert.False(store.TryUpdate("ids", Request(session, PanelTransitionPreviewStore.MaximumPreviewIds), out _));
        clock.Milliseconds += 30001;
        Assert.True(store.TryUpdate("ids", Request(session, PanelTransitionPreviewStore.MaximumPreviewIds + 1), out _));
    }
}
