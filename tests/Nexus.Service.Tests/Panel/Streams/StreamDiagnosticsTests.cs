using Nexus.Service.Panel.Streams;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class StreamDiagnosticsTests
{
    [Fact]
    public void Ingest_and_transport_statistics_cannot_be_mistaken_for_serial_delivery()
    {
        var metrics = new StreamStageDiagnostics("private-device", 1_000, 0);
        metrics.Accepted(1_000, 16); metrics.Accepted(1_040, 16);
        metrics.Enqueued(1); metrics.Enqueued(2); metrics.Dequeued(1);
        metrics.Dropped(trimmed: 2, resync: 1, cleared: 3); metrics.RejectedRaw();
        metrics.TransportCompleted(true, 32, 1_000, 1_005);
        metrics.TransportCompleted(false, 32, 1_010, 1_025);
        var s = metrics.Snapshot();
        Assert.Equal((2L, 32L, 2L, 1L), (s.Accepted, s.AcceptedBytes, s.Enqueued, s.Dequeued));
        Assert.Equal((2L, 1L, 3L, 1L), (s.Trimmed, s.ResyncDropped, s.Cleared, s.RejectedRaw));
        Assert.Equal((1L, 1L, 32L), (s.TransportCalls, s.TransportFailures, s.TransportBytes));
        Assert.Equal(2, s.MaximumDepth);
        Assert.Equal(40, s.IngestInterval.MeanMilliseconds);
        Assert.Equal(10, s.TransportCall.MeanMilliseconds);
    }

    [Fact]
    public void Real_raw_session_reconciles_queue_trims_resync_and_close_without_changing_capacity()
    {
        var metrics = new StreamStageDiagnostics("private-device", System.Diagnostics.Stopwatch.Frequency, 0);
        var session = new StreamSession("private-device", new StreamedPanelDeviceInfo
        {
            Serial = "private-serial",
            Profile = new StreamedPanelProfile
            {
                Kind = "test", DisplayName = "test", Surface = "monitor", CssWidth = 2, CssHeight = 2,
                Codec = StreamCodec.RawBgra, KeepLatestRawFrame = true, RequireNativeRawFrames = true,
            },
        }, "private-panel", metrics);
        session.AcceptFrame(Frame(15));
        session.AcceptFrame(Frame(16)); session.AcceptFrame(Frame(16));
        Assert.Single(session.DequeueForTick()).Release();
        session.Enqueue(Frame(16));
        session.ResetForNewIngest();
        session.Enqueue(Frame(16));
        session.Close();
        var s = metrics.Snapshot();
        Assert.Equal(2, s.Accepted);
        Assert.Equal(4, s.Enqueued);
        Assert.Equal(1, s.Dequeued);
        Assert.Equal((1L, 1L, 1L), (s.Trimmed, s.ResyncDropped, s.Cleared));
        Assert.Equal(1, s.RejectedRaw);
        Assert.Equal(1, s.MaximumDepth);
        Assert.Equal(0, session.QueueDepthForTest);
    }

    [Fact]
    public void Reports_are_bounded_redacted_and_do_not_join_ingest_clocks_across_a_rebind()
    {
        var metrics = new StreamStageDiagnostics("private-device", 1_000, 0);
        metrics.Accepted(1_000, 16); metrics.ResetIngestClock(); metrics.Accepted(1_000_000, 16);
        Assert.Equal(0, metrics.Snapshot().IngestInterval.Count);
        Assert.Null(metrics.Report(29_999, 0, 1, StreamCodec.RawBgra, 2, 2, 30));
        var line = metrics.Report(30_000, 0, 1, StreamCodec.RawBgra, 2, 2, 30);
        Assert.NotNull(line);
        Assert.Contains("transportCallIsNotSerialCompletion=true", line);
        Assert.DoesNotContain("private-device", line);
        Assert.True(line.Length < 2000);
        Assert.Null(metrics.Report(1, 0, 1, StreamCodec.RawBgra, 2, 2, 30));
        Assert.Null(metrics.Report(30_001, 0, 1, StreamCodec.RawBgra, 2, 2, 30));
        Assert.NotNull(metrics.Report(30_002, 0, 1, StreamCodec.H264, 2, 2, 30, final: true));
        Assert.Null(metrics.Report(100_000, 0, 1, StreamCodec.RawBgra, 2, 2, 30, final: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamStageDiagnostics("private", 0, 0));
    }

    private static StreamFrame Frame(int length) => new() { Flags = StreamFraming.FlagIdr, Payload = new byte[length] };
}
