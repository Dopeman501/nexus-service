#if SLV3_FAKE_NATIVE
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;
using Io = Nexus.Service.Peripherals.LianLiWireless.Slv3WinUsbInterop;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3DiagnosticBoundaryTests
{
    [Fact]
    public void Native_read_exposes_structured_outcome_without_relabeling_zero_data_as_verified_busy()
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake-rx", Slv3DongleRole.Rx, diagnosticsEnabled: true);
        Io.Reads.Enqueue((true, new byte[64], 0));
        Assert.True(rx.RfSend(Slv3Protocol.BuildGetDev(1)));
        Assert.Equal(new byte[64], rx.RfRead(434));
        var observed = Observe(rx);
        Assert.Equal(Slv3ReadStop.AmbiguousZeroLeading, observed.ReadStop);
        Assert.Equal(64, observed.ReceivedRead);
        Assert.Equal(1, observed.ReadCalls);
        Assert.Equal(1, observed.Exchange);
        Assert.True(observed.IsGetDev);
    }

    [Theory]
    [InlineData(false, Slv3DongleRole.Rx)]
    [InlineData(true, Slv3DongleRole.Tx)]
    public void Disabled_or_non_rx_metadata_adds_no_observation(bool enabled, Slv3DongleRole role)
    {
        Io.Reset();
        using var transport = new Slv3Transport("fake", role, enabled);
        Io.Reads.Enqueue((true, new byte[64], 0));
        Assert.True(transport.RfSend(Slv3Protocol.BuildGetDev(1)));
        Assert.Equal(new byte[64], transport.RfRead(434));
        Assert.False(Observe(transport).Available);
        Assert.DoesNotContain(ServiceLog.Messages, x => x.StartsWith("[lianli-wireless-diag]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 121, Slv3ReadStop.NativeError)]
    [InlineData(false, 5, Slv3ReadStop.NativeError)]
    [InlineData(true, 0, Slv3ReadStop.EmptyRead)]
    public void Failed_native_reads_and_successful_empty_reads_are_distinct(bool ok, int error, object stop)
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        rx.RfSend(Slv3Protocol.BuildGetDev(1));
        Io.Reads.Enqueue((ok, Array.Empty<byte>(), error));
        Assert.Empty(rx.RfRead(434));
        var n = Observe(rx);
        Assert.Equal((Slv3ReadStop)stop, n.ReadStop);
        Assert.Equal(error, n.ReadError);
        Assert.Equal(0, n.ReceivedRead);
        Assert.Equal(0, n.NativeReadBytes);
    }

    [Fact]
    public void Flush_failure_and_short_successful_write_are_observed_without_changing_legacy_send_result()
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        Io.FlushSucceeds = false; Io.FlushError = 6; Io.WriteLength = 32;
        var getDev = Slv3Protocol.BuildGetDev(2);
        Assert.True(rx.RfSend(getDev));
        var n = Observe(rx);
        Assert.True(n.FlushAttempted);
        Assert.False(n.FlushSucceeded);
        Assert.Equal(6, n.FlushError);
        Assert.True(n.WriteSucceeded);
        Assert.Equal(64, n.WriteRequested);
        Assert.Equal(32u, n.WriteTransferred);
        Assert.Equal(getDev, Assert.Single(Io.Sent));
    }

    [Theory]
    [InlineData(121, true)]
    [InlineData(5, false)]
    public void Failed_writes_preserve_timeout_status_and_rx_handle_behavior(int error, bool timedOut)
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        Io.WriteSucceeds = false; Io.WriteError = error; Io.WriteLength = 0;
        Assert.False(rx.RfSend(Slv3Protocol.BuildGetDev(1)));
        Assert.Equal(timedOut, rx.LastWriteTimedOut);
        Assert.True(rx.IsOpen);
        Assert.Equal(error, Observe(rx).WriteError);
        Assert.False(Observe(rx).WriteSucceeded);
        Assert.Equal(Slv3ReadStop.NotObserved, Observe(rx).ReadStop);
        Assert.Equal(0, Io.ReadCalls);
    }

    [Fact]
    public void A_complete_page_preserves_all_returned_bytes_and_reports_native_padding_separately()
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        var native = Enumerable.Range(0, 448).Select(i => (byte)(i % 251)).ToArray();
        native[0] = 0x10;
        for (int i = 0; i < 7; i++) Io.Reads.Enqueue((true, native[(i * 64)..((i + 1) * 64)], 0));
        rx.RfSend(Slv3Protocol.BuildGetDev(1));
        Assert.Equal(native[..434], rx.RfRead(434));
        var n = Observe(rx);
        Assert.Equal(Slv3ReadStop.ExpectedLength, n.ReadStop);
        Assert.Equal(434, n.ExpectedRead);
        Assert.Equal(434, n.ReceivedRead);
        Assert.Equal(448, n.NativeReadBytes);
        Assert.Equal(7, n.ReadCalls);
        Assert.Empty(Io.Reads);
        Assert.True(n.SendStartedTicks > 0);
        Assert.True(n.SendElapsedTicks >= 0 && n.ReadElapsedTicks >= 0);
        Assert.True(n.SendLockWaitTicks >= 0 && n.ReadLockWaitTicks >= 0);
    }

    [Fact]
    public void A_short_read_and_partial_error_preserve_legacy_bytes_and_distinct_termination()
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        rx.RfSend(Slv3Protocol.BuildGetDev(1));
        Io.Reads.Enqueue((true, new byte[] { 0x10, 0, 0 }, 0));
        Assert.Equal(new byte[] { 0x10, 0, 0 }, rx.RfRead(434));
        Assert.Equal(Slv3ReadStop.ShortRead, Observe(rx).ReadStop);
        Assert.Equal(1, Observe(rx).ShortReads);
        rx.RfSend(Slv3Protocol.BuildGetDev(1));
        var first = new byte[64]; first[0] = 0x10;
        Io.Reads.Enqueue((true, first, 0));
        Io.Reads.Enqueue((false, Array.Empty<byte>(), 121));
        Assert.Equal(first, rx.RfRead(434));
        var n = Observe(rx);
        Assert.Equal(2, n.Exchange);
        Assert.Equal(Slv3ReadStop.NativeError, n.ReadStop);
        Assert.Equal(121, n.ReadError);
        Assert.Equal(64, n.NativeReadBytes);
        Assert.Equal(64, n.ReceivedRead);
        Assert.Equal(0, n.ShortReads);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void Valid_page_continuations_stay_ambiguous_and_leave_following_header_unread(int offset)
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        var page = Slv3GetDevDiagnosticsTests.Records(8);
        var tail = page[offset..(offset + 64)];
        Assert.Equal(0, tail[0]);
        Io.Reads.Enqueue((true, tail, 0));
        Io.Reads.Enqueue((true, new byte[] { 0x10, 0, 0, 0 }, 0));
        rx.RfSend(Slv3Protocol.BuildGetDev(1));
        Assert.Equal(tail, rx.RfRead(434));
        Assert.Equal(Slv3ReadStop.AmbiguousZeroLeading, Observe(rx).ReadStop);
        Assert.Equal(1, Io.ReadCalls);
        Assert.Single(Io.Reads);
    }

    [Fact]
    public void Short_zero_data_records_both_ambiguity_and_short_native_length()
    {
        Io.Reset();
        using var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        rx.RfSend(Slv3Protocol.BuildGetDev(1));
        Io.Reads.Enqueue((true, new byte[1], 0));
        Assert.Equal(new byte[1], rx.RfRead(434));
        var n = Observe(rx);
        Assert.Equal(Slv3ReadStop.AmbiguousZeroLeading, n.ReadStop);
        Assert.Equal(1, n.ShortReads);
        Assert.Equal(1u, n.LastReadTransferred);
    }

    [Fact]
    public void Invalid_requests_and_disposed_transport_do_not_issue_new_native_calls()
    {
        Io.Reset();
        var rx = new Slv3Transport("fake", Slv3DongleRole.Rx, true);
        Assert.Empty(rx.RfRead(0));
        Assert.Equal(Slv3ReadStop.InvalidLength, Observe(rx).ReadStop);
        rx.Dispose(); rx.Dispose();
        Assert.False(rx.RfSend(Slv3Protocol.BuildGetDev(1)));
        Assert.Empty(rx.RfRead(434));
        Assert.Equal(Slv3ReadStop.Disposed, Observe(rx).ReadStop);
        Assert.False(Observe(rx).WriteAttempted);
        Assert.Equal(1, Io.Frees);
        Assert.Equal(0, Io.WriteCalls);
        Assert.Equal(0, Io.ReadCalls);
    }

    [Fact]
    public void Hub_correlates_actual_transport_metadata_without_rejecting_legacy_short_write_or_failed_flush()
    {
        Io.Reset();
        var d = new Slv3GetDevDiagnostics();
        using var rx = new Slv3Transport("fake-rx", Slv3DongleRole.Rx, true);
        var net = new Slv3TestHub.FakeSlv3Network();
        var tx = new Slv3TestHub.FakeTxTransport(net);
        long now = 0;
        using var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(),
            port => port.Role == Slv3DongleRole.Tx ? tx : rx, () => now)
        {
            SleepMs = _ => { }, GetDevDiagnostics = d,
        };
        Assert.True(hub.EnsureConnected());
        Io.FlushSucceeds = false; Io.FlushError = 6; Io.WriteLength = 32;
        Io.Reads.Enqueue((true, Slv3GetDevDiagnosticsTests.Records(1), 0));
        now = 30_000;
        Assert.True(hub.PollTick());
        Assert.Equal(1, d.Statistics.NativeObserved);
        Assert.Equal(1, d.Statistics.AcceptedHeaders);
        Assert.Equal(1, d.Statistics.CompleteLists);
        Assert.Equal(1, d.Statistics.FlushFailures);
        Assert.Equal(1, d.Statistics.ShortWrites);
        Assert.Equal(1, d.Statistics.NativeShortReads);
        var report = Assert.Single(ServiceLog.Messages, text => text.StartsWith("[lianli-wireless-diag]", StringComparison.Ordinal));
        Assert.Contains("nativeExchange=1", report);
        Assert.Contains("nativeFlushAttempted=1 nativeFlushOk=0 nativeFlushError=6", report);
        Assert.Contains("writeRequested=64 writeTransferred=32", report);
    }

    private static Slv3IoObservation Observe(Slv3Transport transport) =>
        Assert.IsAssignableFrom<ISlv3TransportDiagnostics>(transport).LastIoObservation;

    [Fact]
    public void Recovery_diagnostic_calls_zero_leading_data_ambiguous_and_does_not_log_raw_records()
    {
        Io.Reset();
        var net = new Slv3TestHub.FakeSlv3Network();
        var tx = new Slv3TestHub.FakeTxTransport(net);
        var rx = new ZeroReplyTransport();
        using var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(),
            port => port.Role == Slv3DongleRole.Tx ? tx : rx) { SleepMs = _ => { } };
        Assert.True(hub.EnsureConnected());
        for (int i = 0; i < 60; i++) Assert.True(hub.PollTick());
        var warning = Assert.Single(ServiceLog.Messages, x => x.Contains("reopening RX", StringComparison.Ordinal));
        Assert.Contains("ambiguous", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consecutive", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0000000000000000", warning, StringComparison.Ordinal);
    }

    private sealed class ZeroReplyTransport : ISlv3Transport
    {
        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";
        public bool RfSend(ReadOnlySpan<byte> frame) => true;
        public byte[] RfRead(int expectedLen) => new byte[64];
        public void Dispose() { }
    }
}
#endif
