using System;
using System.Diagnostics;

namespace Nexus.Service.Peripherals.LianLiWireless;

internal enum Slv3GetDevReplyKind
{
    SendFailure,
    EmptyReply,
    AmbiguousZeroLeading,
    InvalidHeader,
    AcceptedHeader,
}

/// <summary>
/// Opt-in, fixed-storage observations owned by the hub lock. These never decide
/// packet acceptance, request sizing, topology, recovery or device commands.
/// An accepted header/record is not proof of freshness without a protocol token.
/// </summary>
internal sealed class Slv3GetDevDiagnostics
{
    internal const long ReportIntervalMs = 30_000;
    private bool _started;
    private long _nextReportMs;
    private long _lastNativeExchange;
    private long? _openedAtMs, _lastHeaderMs, _lastCompleteListMs, _lastRecordReplyMs;
    private Slv3GetDevStatistics _statistics;
    internal Slv3GetDevStatistics Statistics => _statistics;

    internal static Slv3GetDevReplyKind Classify(bool sent, ReadOnlySpan<byte> reply)
    {
        if (!sent) return Slv3GetDevReplyKind.SendFailure;
        if (reply.IsEmpty) return Slv3GetDevReplyKind.EmptyReply;
        if (reply[0] == 0) return Slv3GetDevReplyKind.AmbiguousZeroLeading;
        return reply.Length < Slv3Protocol.RecordHeaderLength || reply[0] != Slv3Protocol.UsbSendRf
            ? Slv3GetDevReplyKind.InvalidHeader : Slv3GetDevReplyKind.AcceptedHeader;
    }

    internal static string DescribeAmbiguousRecovery(int count, int failures, int threshold) =>
        $"{count} ambiguous zero-leading GetDev replies accumulated without a complete validated list or recovery (threshold {threshold}, failures {failures}; vendor status unverified)";

    internal static string DescribeFailureRecovery(int count, int ambiguous, int threshold) =>
        $"{count} GetDev failures accumulated without a complete validated list or recovery (threshold {threshold}, ambiguous zero-leading {ambiguous})";

    internal void HandleOpened(long nowMs)
    {
        Start(nowMs);
        _openedAtMs = nowMs;
        _lastNativeExchange = 0;
        _statistics = _statistics with
        {
            HandleGeneration = _statistics.HandleGeneration + 1,
            HeadersSinceOpen = 0, CompleteListsSinceOpen = 0, RecordRepliesSinceOpen = 0,
            LastNativeExchange = 0, LastNative = default,
        };
    }

    internal void Observe(long nowMs, int requestedPages, bool sent, ReadOnlySpan<byte> reply,
        Slv3IoObservation native = default)
    {
        Start(nowMs);
        var kind = Classify(sent, reply);
        var s = _statistics with
        {
            Exchanges = _statistics.Exchanges + 1, LastPages = requestedPages,
            LastReplyBytes = reply.Length, LastKind = kind,
        };
        switch (kind)
        {
            case Slv3GetDevReplyKind.SendFailure: s = s with { SendFailures = s.SendFailures + 1 }; break;
            case Slv3GetDevReplyKind.EmptyReply: s = s with { EmptyReplies = s.EmptyReplies + 1 }; break;
            case Slv3GetDevReplyKind.AmbiguousZeroLeading: s = s with { AmbiguousReplies = s.AmbiguousReplies + 1 }; break;
            case Slv3GetDevReplyKind.InvalidHeader: s = s with { InvalidHeaders = s.InvalidHeaders + 1 }; break;
            case Slv3GetDevReplyKind.AcceptedHeader:
                _lastHeaderMs = nowMs;
                s = ObserveRecords(nowMs, reply, s);
                break;
        }
        _statistics = ObserveNative(native, s);
    }

    private Slv3GetDevStatistics ObserveRecords(long nowMs, ReadOnlySpan<byte> reply, Slv3GetDevStatistics s)
    {
        var declared = Slv3Protocol.RecordCount(reply);
        var available = Math.Min(declared, (reply.Length - Slv3Protocol.RecordHeaderLength) / Slv3Protocol.RecordLength);
        var valid = 0;
        for (var i = 0; i < available; i++)
        {
            // Same structural validator as TryParseRecord, without allocating its arrays.
            var tail = Slv3Protocol.RecordHeaderLength + (i + 1) * Slv3Protocol.RecordLength - 1;
            if (reply[tail] == Slv3Protocol.RecordValidator) valid++;
        }
        var complete = available == declared && valid == declared;
        if (complete) _lastCompleteListMs = nowMs;
        if (valid > 0) _lastRecordReplyMs = nowMs;
        return s with
        {
            AcceptedHeaders = s.AcceptedHeaders + 1, HeadersSinceOpen = s.HeadersSinceOpen + 1,
            CompleteLists = s.CompleteLists + (complete ? 1 : 0),
            CompleteListsSinceOpen = s.CompleteListsSinceOpen + (complete ? 1 : 0),
            RecordReplies = s.RecordReplies + (valid > 0 ? 1 : 0),
            RecordRepliesSinceOpen = s.RecordRepliesSinceOpen + (valid > 0 ? 1 : 0),
            PartialLists = s.PartialLists + (available < declared ? 1 : 0),
            InvalidRecords = s.InvalidRecords + available - valid,
            LastDeclaredRecords = declared, LastAvailableRecords = available, LastValidRecords = valid,
        };
    }

    private Slv3GetDevStatistics ObserveNative(Slv3IoObservation native, Slv3GetDevStatistics s)
    {
        if (!native.Available) return s with { NativeUnavailable = s.NativeUnavailable + 1 };
        if (!native.IsGetDev || native.Exchange <= _lastNativeExchange)
            return s with { NativeUncorrelated = s.NativeUncorrelated + 1 };
        _lastNativeExchange = native.Exchange;
        return s with
        {
            NativeObserved = s.NativeObserved + 1, LastNativeExchange = native.Exchange,
            FlushFailures = s.FlushFailures + (native.FlushAttempted && !native.FlushSucceeded ? 1 : 0),
            LastFlushError = native.FlushError != 0 ? native.FlushError : s.LastFlushError,
            NativeWriteFailures = s.NativeWriteFailures + (native.WriteAttempted && !native.WriteSucceeded ? 1 : 0),
            ShortWrites = s.ShortWrites + (native.WriteAttempted && native.WriteSucceeded && native.WriteTransferred != native.WriteRequested ? 1 : 0),
            LastWriteError = native.WriteError != 0 ? native.WriteError : s.LastWriteError,
            NativeReadErrors = s.NativeReadErrors + (native.ReadStop == Slv3ReadStop.NativeError ? 1 : 0),
            NativeEmptyReads = s.NativeEmptyReads + (native.ReadStop == Slv3ReadStop.EmptyRead ? 1 : 0),
            NativeShortReads = s.NativeShortReads + native.ShortReads,
            LastReadError = native.ReadError != 0 ? native.ReadError : s.LastReadError,
            NativeReadBytes = s.NativeReadBytes + native.NativeReadBytes,
            LastNative = native,
        };
    }

    private void Start(long nowMs)
    {
        if (_started) return;
        _started = true;
        _nextReportMs = nowMs + ReportIntervalMs;
    }

    internal string? TryReport(long nowMs)
    {
        if (!_started || nowMs < _nextReportMs) return null;
        _nextReportMs = nowMs + ReportIntervalMs;
        var s = _statistics;
        var n = s.LastNative;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[lianli-wireless-diag] handleGeneration={s.HandleGeneration} exchanges={s.Exchanges} " +
            $"sendFailures={s.SendFailures} emptyReplies={s.EmptyReplies} ambiguousZeroLeading={s.AmbiguousReplies} " +
            $"invalidHeaders={s.InvalidHeaders} acceptedHeaders={s.AcceptedHeaders} completeLists={s.CompleteLists} " +
            $"recordReplies={s.RecordReplies} partialLists={s.PartialLists} invalidRecords={s.InvalidRecords} " +
            $"headersSinceOpen={s.HeadersSinceOpen} completeListsSinceOpen={s.CompleteListsSinceOpen} recordRepliesSinceOpen={s.RecordRepliesSinceOpen} " +
            $"handleAgeMs={Age(nowMs, _openedAtMs)} acceptedHeaderAgeMs={Age(nowMs, _lastHeaderMs)} " +
            $"completeListAgeMs={Age(nowMs, _lastCompleteListMs)} recordReplyAgeMs={Age(nowMs, _lastRecordReplyMs)} " +
            $"nativeObserved={s.NativeObserved} nativeUnavailable={s.NativeUnavailable} nativeUncorrelated={s.NativeUncorrelated} nativeExchange={s.LastNativeExchange} " +
            $"flushFailures={s.FlushFailures} lastFlushError={s.LastFlushError} nativeWriteFailures={s.NativeWriteFailures} " +
            $"shortWrites={s.ShortWrites} lastWriteError={s.LastWriteError} nativeReadErrors={s.NativeReadErrors} " +
            $"nativeEmptyReads={s.NativeEmptyReads} nativeShortReads={s.NativeShortReads} lastReadError={s.LastReadError} nativeReadBytes={s.NativeReadBytes} " +
            $"pages={s.LastPages} replyBytes={s.LastReplyBytes} lastReplyKind={(int)s.LastKind} " +
            $"declaredRecords={s.LastDeclaredRecords} availableRecords={s.LastAvailableRecords} validRecords={s.LastValidRecords} " +
            $"nativeExpected={n.ExpectedRead} nativeReceived={n.ReceivedRead} nativeReadCalls={n.ReadCalls} nativeLastRead={n.LastReadTransferred} " +
            $"nativeFlushAttempted={(n.FlushAttempted ? 1 : 0)} nativeFlushOk={(n.FlushSucceeded ? 1 : 0)} nativeFlushError={n.FlushError} " +
            $"nativeWriteAttempted={(n.WriteAttempted ? 1 : 0)} nativeWriteOk={(n.WriteSucceeded ? 1 : 0)} nativeWriteError={n.WriteError} nativeReadError={n.ReadError} " +
            $"writeRequested={n.WriteRequested} writeTransferred={n.WriteTransferred} readStop={(int)n.ReadStop} " +
            $"sendStartTicks={n.SendStartedTicks} sendUs={Microseconds(n.SendElapsedTicks)} readUs={Microseconds(n.ReadElapsedTicks)} " +
            $"sendLockWaitUs={Microseconds(n.SendLockWaitTicks)} readLockWaitUs={Microseconds(n.ReadLockWaitTicks)} freshnessVerified=0");
    }

    private static long Age(long nowMs, long? thenMs) => thenMs is { } value ? Math.Max(0, nowMs - value) : -1;
    private static long Microseconds(long ticks) => (long)(ticks * (1_000_000d / Stopwatch.Frequency));
}

internal readonly record struct Slv3GetDevStatistics
{
    public long HandleGeneration { get; init; }
    public long Exchanges { get; init; }
    public long SendFailures { get; init; }
    public long EmptyReplies { get; init; }
    public long AmbiguousReplies { get; init; }
    public long InvalidHeaders { get; init; }
    public long AcceptedHeaders { get; init; }
    public long CompleteLists { get; init; }
    public long RecordReplies { get; init; }
    public long PartialLists { get; init; }
    public long InvalidRecords { get; init; }
    public long HeadersSinceOpen { get; init; }
    public long CompleteListsSinceOpen { get; init; }
    public long RecordRepliesSinceOpen { get; init; }
    public long NativeObserved { get; init; }
    public long NativeUnavailable { get; init; }
    public long NativeUncorrelated { get; init; }
    public long LastNativeExchange { get; init; }
    public long FlushFailures { get; init; }
    public int LastFlushError { get; init; }
    public long NativeWriteFailures { get; init; }
    public long ShortWrites { get; init; }
    public int LastWriteError { get; init; }
    public long NativeReadErrors { get; init; }
    public long NativeEmptyReads { get; init; }
    public long NativeShortReads { get; init; }
    public int LastReadError { get; init; }
    public long NativeReadBytes { get; init; }
    public int LastPages { get; init; }
    public int LastReplyBytes { get; init; }
    public int LastDeclaredRecords { get; init; }
    public int LastAvailableRecords { get; init; }
    public int LastValidRecords { get; init; }
    public Slv3GetDevReplyKind LastKind { get; init; }
    public Slv3IoObservation LastNative { get; init; }
}
