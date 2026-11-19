using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3GetDevDiagnosticsTests
{
    [Theory]
    [InlineData(false, "10000000", Slv3GetDevReplyKind.SendFailure)]
    [InlineData(true, "", Slv3GetDevReplyKind.EmptyReply)]
    [InlineData(true, "00", Slv3GetDevReplyKind.AmbiguousZeroLeading)]
    [InlineData(true, "001CAABBCC", Slv3GetDevReplyKind.AmbiguousZeroLeading)]
    [InlineData(true, "10", Slv3GetDevReplyKind.InvalidHeader)]
    [InlineData(true, "100100", Slv3GetDevReplyKind.InvalidHeader)]
    [InlineData(true, "11000000", Slv3GetDevReplyKind.InvalidHeader)]
    [InlineData(true, "10000000", Slv3GetDevReplyKind.AcceptedHeader)]
    public void Categories_describe_only_observed_data(bool sent, string hex, object expected) =>
        Assert.Equal((Slv3GetDevReplyKind)expected, Slv3GetDevDiagnostics.Classify(sent, Convert.FromHexString(hex)));

    [Fact]
    public void Classifications_accumulate_without_becoming_recovery_decisions()
    {
        var d = new Slv3GetDevDiagnostics();
        d.Observe(0, 1, false, Array.Empty<byte>());
        d.Observe(1, 1, true, Array.Empty<byte>());
        d.Observe(2, 1, true, new byte[] { 0 });
        d.Observe(3, 1, true, new byte[] { 0x11 });
        d.Observe(4, 1, true, Records(0));
        var s = d.Statistics;
        Assert.Equal(5, s.Exchanges);
        Assert.Equal(1, s.SendFailures);
        Assert.Equal(1, s.EmptyReplies);
        Assert.Equal(1, s.AmbiguousReplies);
        Assert.Equal(1, s.InvalidHeaders);
        Assert.Equal(1, s.AcceptedHeaders);
        Assert.Equal(5, s.NativeUnavailable);
    }

    [Fact]
    public void Header_acceptance_is_separate_from_incomplete_multi_page_records()
    {
        var d = new Slv3GetDevDiagnostics();
        var all = Records(12);
        d.Observe(0, 1, true, all.AsSpan(0, 434));
        Assert.Equal(1, d.Statistics.AcceptedHeaders);
        Assert.Equal(0, d.Statistics.CompleteLists);
        Assert.Equal(1, d.Statistics.PartialLists);
        Assert.Equal(12, d.Statistics.LastDeclaredRecords);
        Assert.Equal(10, d.Statistics.LastAvailableRecords);
        d.Observe(1, 2, true, all);
        Assert.Equal(2, d.Statistics.AcceptedHeaders);
        Assert.Equal(1, d.Statistics.CompleteLists);
        Assert.Equal(12, d.Statistics.LastValidRecords);
        Assert.Equal(2, d.Statistics.LastPages);
    }

    [Fact]
    public void Invalid_record_and_empty_list_never_claim_a_valid_record_reply()
    {
        var d = new Slv3GetDevDiagnostics();
        var invalid = Records(1);
        invalid[^1] = 0;
        d.Observe(0, 1, true, invalid);
        Assert.Equal(1, d.Statistics.AcceptedHeaders);
        Assert.Equal(1, d.Statistics.InvalidRecords);
        Assert.Equal(0, d.Statistics.CompleteLists);
        d.Observe(1, 1, true, Records(0));
        Assert.Equal(1, d.Statistics.CompleteLists);
        Assert.Equal(0, d.Statistics.RecordReplies);
    }

    [Fact]
    public void Native_snapshots_require_a_new_exchange_in_the_current_handle_generation()
    {
        var d = new Slv3GetDevDiagnostics();
        d.HandleOpened(0);
        var native = new Slv3IoObservation { Available = true, Exchange = 1, IsGetDev = true };
        d.Observe(0, 1, true, Records(0), native);
        d.Observe(1, 1, true, Records(0), native);
        d.Observe(2, 1, true, Records(0), native with { Exchange = 2, IsGetDev = false });
        Assert.Equal(1, d.Statistics.NativeObserved);
        Assert.Equal(2, d.Statistics.NativeUncorrelated);
        d.HandleOpened(3);
        d.Observe(4, 1, true, Records(0), native);
        Assert.Equal(2, d.Statistics.NativeObserved);
        Assert.Equal(2, d.Statistics.HandleGeneration);
        Assert.Equal(1, d.Statistics.HeadersSinceOpen);
    }

    [Fact]
    public void Older_native_exchange_is_not_counted_as_a_new_correlated_exchange()
    {
        var d = new Slv3GetDevDiagnostics();
        d.HandleOpened(0);
        var native = new Slv3IoObservation { Available = true, IsGetDev = true, Exchange = 2 };
        d.Observe(0, 1, true, Records(0), native);
        d.Observe(1, 1, true, Records(0), native with { Exchange = 1 });
        Assert.Equal(1, d.Statistics.NativeObserved);
        Assert.Equal(1, d.Statistics.NativeUncorrelated);
        Assert.Equal(2, d.Statistics.LastNativeExchange);
    }

    [Fact]
    public void Reopen_clears_previous_handle_native_metadata_without_resetting_lifetime_counts()
    {
        var d = new Slv3GetDevDiagnostics();
        d.HandleOpened(0);
        d.Observe(1, 1, true, Records(0), new Slv3IoObservation
        {
            Available = true, IsGetDev = true, Exchange = 7, WriteRequested = 64,
        });
        d.HandleOpened(2);
        Assert.Equal(1, d.Statistics.NativeObserved);
        Assert.Equal(0, d.Statistics.LastNativeExchange);
        Assert.False(d.Statistics.LastNative.Available);
    }

    [Fact]
    public void Flush_write_read_errors_short_and_empty_results_remain_distinct()
    {
        var d = new Slv3GetDevDiagnostics();
        var native = new Slv3IoObservation
        {
            Available = true, IsGetDev = true, Exchange = 1,
            FlushAttempted = true, FlushSucceeded = false, FlushError = 6,
            WriteAttempted = true, WriteSucceeded = true, WriteRequested = 64, WriteTransferred = 32,
            ReadStop = Slv3ReadStop.NativeError, ReadError = 121, NativeReadBytes = 64,
        };
        d.Observe(0, 1, true, Records(0), native);
        d.Observe(1, 1, false, Array.Empty<byte>(), native with
        {
            Exchange = 2, FlushSucceeded = true, FlushError = 0, WriteSucceeded = false,
            WriteError = 5, ReadStop = Slv3ReadStop.NotObserved, ReadError = 0, NativeReadBytes = 0,
        });
        d.Observe(2, 1, true, Array.Empty<byte>(), native with
        {
            Exchange = 3, FlushAttempted = false, FlushError = 0,
            WriteTransferred = 64, ReadStop = Slv3ReadStop.EmptyRead, ReadError = 0, NativeReadBytes = 0,
        });
        d.Observe(3, 1, true, new byte[] { 0x11, 0, 0 }, native with
        {
            Exchange = 4, FlushSucceeded = true, FlushError = 0, WriteTransferred = 64,
            ReadStop = Slv3ReadStop.ShortRead, ReadError = 0, ShortReads = 1, NativeReadBytes = 3,
        });
        var s = d.Statistics;
        Assert.Equal(1, s.FlushFailures);
        Assert.Equal(6, s.LastFlushError);
        Assert.Equal(1, s.NativeWriteFailures);
        Assert.Equal(5, s.LastWriteError);
        Assert.Equal(1, s.ShortWrites);
        Assert.Equal(1, s.NativeReadErrors);
        Assert.Equal(121, s.LastReadError);
        Assert.Equal(1, s.NativeEmptyReads);
        Assert.Equal(1, s.NativeShortReads);
        Assert.Equal(67, s.NativeReadBytes);
        var report = Assert.IsType<string>(d.TryReport(30_000));
        Assert.Contains("lastReadError=121", report);
        Assert.Contains("nativeReadError=0", report);
        Assert.Contains("nativeFlushAttempted=1 nativeFlushOk=1 nativeFlushError=0", report);
        Assert.Contains("nativeWriteAttempted=1 nativeWriteOk=1 nativeWriteError=0", report);
    }

    [Fact]
    public void Handle_open_does_not_claim_a_subsequent_header_or_record_response()
    {
        var d = new Slv3GetDevDiagnostics();
        d.HandleOpened(0);
        d.Observe(10, 1, true, Records(1));
        d.HandleOpened(20);
        Assert.Equal(1, d.Statistics.AcceptedHeaders);
        Assert.Equal(0, d.Statistics.HeadersSinceOpen);
        Assert.Equal(0, d.Statistics.CompleteListsSinceOpen);
        Assert.Equal(0, d.Statistics.RecordRepliesSinceOpen);
        var report = Assert.IsType<string>(d.TryReport(30_000));
        Assert.Contains("acceptedHeaderAgeMs=29990", report);
        Assert.Contains("completeListAgeMs=29990", report);
        Assert.Contains("recordReplyAgeMs=29990", report);
        Assert.Contains("handleAgeMs=29980", report);
        Assert.Contains("freshnessVerified=0", report);
    }

    [Fact]
    public void Reporting_is_rate_limited_without_timers_catchup_or_payload_identity()
    {
        var d = new Slv3GetDevDiagnostics();
        Assert.Null(d.TryReport(100_000));
        d.HandleOpened(0);
        var record = Records(1);
        Convert.FromHexString("A1B2C3D4E5F6").CopyTo(record, 4);
        d.Observe(1, 1, true, record);
        Assert.Null(d.TryReport(29_999));
        var report = Assert.IsType<string>(d.TryReport(30_000));
        Assert.DoesNotContain("A1B2C3D4E5F6", report, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(report.Length, 1, 2500);
        Assert.Null(d.TryReport(30_000));
        Assert.NotNull(d.TryReport(100_000));
        Assert.Null(d.TryReport(100_001));
        Assert.Null(d.TryReport(129_999));
        Assert.NotNull(d.TryReport(130_000));
    }

    [Fact]
    public void Unseen_telemetry_reports_unknown_age_and_counter_text_is_accumulated()
    {
        var d = new Slv3GetDevDiagnostics();
        d.Observe(0, 1, true, new byte[] { 0 });
        var report = Assert.IsType<string>(d.TryReport(30_000));
        Assert.Contains("acceptedHeaderAgeMs=-1", report);
        Assert.Contains("completeListAgeMs=-1", report);
        Assert.Contains("recordReplyAgeMs=-1", report);
        var ambiguous = Slv3GetDevDiagnostics.DescribeAmbiguousRecovery(117, 4, 60);
        Assert.StartsWith("117 ambiguous", ambiguous);
        Assert.Contains("failures 4", ambiguous);
        Assert.Contains("without a complete validated list or recovery", ambiguous);
        Assert.DoesNotContain("consecutive", ambiguous);
        var failure = Slv3GetDevDiagnostics.DescribeFailureRecovery(9, 2, 5);
        Assert.StartsWith("9 GetDev failures accumulated", failure);
        Assert.Contains("ambiguous zero-leading 2", failure);
        Assert.Contains("without a complete validated list or recovery", failure);
    }

    [Fact]
    public void Steady_state_metadata_observation_allocates_no_per_packet_objects()
    {
        var d = new Slv3GetDevDiagnostics();
        var reply = Records(1);
        for (int i = 0; i < 1000; i++) d.Observe(i, 1, true, reply);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 5000; i++) d.Observe(i, 1, true, reply);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    internal static byte[] Records(int count)
    {
        var result = new byte[4 + count * 42];
        result[0] = 0x10;
        result[1] = (byte)count;
        for (int i = 0; i < count; i++) result[4 + (i + 1) * 42 - 1] = 0x1c;
        return result;
    }
}
