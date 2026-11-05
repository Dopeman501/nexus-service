using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal interface ID215FrameSource
{
    // The pump may retain this complete envelope until another frame replaces it.
    ReadOnlyMemory<byte> TakeLatestEnvelope();
    bool MayRepeat { get; }
}

/// <summary>One wire pump from the first live-mode request through the final clear.</summary>
internal sealed class D215LiveStream
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BrightnessResultTimeout = TimeSpan.FromSeconds(22);
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationTokenSource _wake = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<D215DeviceInfo?> _rememberInfo;
    private BrightnessRequest? _brightness;
    private int _stopRequested;
    private Exception? _failure;

    internal D215LiveStream(D215Wire wire, ID215FrameSource frames, Func<bool> isAuthorized, TimeProvider clock,
        D215Timing timing, Action<D215DeviceInfo?>? rememberInfo = null)
    {
        _rememberInfo = rememberInfo ?? (static _ => { });
        Completion = Task.Run(() => RunAsync(wire, frames, isAuthorized, clock, timing));
    }

    internal Task Started => _started.Task;
    internal Task Completion { get; }
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal bool StopRequested => Volatile.Read(ref _stopRequested) != 0;

    internal ValueTask<D215BrightnessResponse> SetBrightnessAsync(int value, Func<bool> isAuthorized, CancellationToken cancellationToken)
    {
        if (value is not (80 or 100)) throw new ArgumentOutOfRangeException(nameof(value));
        if (cancellationToken.IsCancellationRequested || StopRequested || Completion.IsCompleted
            || Failure is not null || !Started.IsCompletedSuccessfully || !isAuthorized())
        {
            return ValueTask.FromResult(new D215BrightnessResponse("unavailable", null));
        }

        var request = new BrightnessRequest(value, isAuthorized, cancellationToken);
        if (Interlocked.CompareExchange(ref _brightness, request, null) is not null)
            return ValueTask.FromResult(new D215BrightnessResponse("busy", null));

        if (StopRequested && ReferenceEquals(Interlocked.CompareExchange(ref _brightness, null, request), request))
            request.Complete("unavailable", null);
        return WaitForBrightnessAsync(request, cancellationToken);
    }

    private async ValueTask<D215BrightnessResponse> WaitForBrightnessAsync(BrightnessRequest request, CancellationToken cancellationToken)
    {
        try { return await request.Result.WaitAsync(BrightnessResultTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException ex)
        {
            // The native lease stays quarantined if Windows has yet to complete the request.
            Fail(ex);
            request.Complete("failed", null);
            return await request.Result.ConfigureAwait(false);
        }
    }

    internal void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            _ = SignalAsync(_wake);
    }

    internal void Abort()
    {
        _ = SignalAsync(_abort);
        RequestStop();
    }

    internal void Fail(Exception failure)
    {
        Interlocked.CompareExchange(ref _failure, failure, null);
        if (_started.TrySetException(failure)) _ = _started.Task.Exception;
        Abort();
    }

    private async Task RunAsync(D215Wire wire, ID215FrameSource frames, Func<bool> isAuthorized, TimeProvider clock, D215Timing timing)
    {
        var abort = _abort.Token;
        try
        {
            if (StopRequested || !isAuthorized()) return;

            var liveRequestedAt = clock.GetTimestamp();
            await wire.EnterLiveAsync(abort).ConfigureAwait(false);
            var liveActive = true;
            if (!StopRequested && isAuthorized())
            {
                _started.TrySetResult();
                liveActive = await PumpAsync(wire, frames, isAuthorized, clock, timing, liveRequestedAt, abort).ConfigureAwait(false);
            }

            // A normal stop lets the in-flight packet finish. A lost device or an
            // uncertain transaction leaves the session for its owner to close.
            if (liveActive && StopRequested && !abort.IsCancellationRequested)
                await wire.ClearAsync(abort).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _failure, ex, null);
            _started.TrySetException(ex);
            ServiceLog.Warn($"[jungle-leopard-d215] live stream ended with {ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            Interlocked.Exchange(ref _brightness, null)?.Complete("unavailable", null);
            _started.TrySetCanceled();
            _wake.Dispose();
            _abort.Dispose();
        }
    }

    private async Task<bool> PumpAsync(D215Wire wire, ID215FrameSource frames, Func<bool> isAuthorized,
        TimeProvider clock, D215Timing timing, long origin, CancellationToken abort)
    {
        var nextRenewal = ScheduleRenewal(TimeSpan.Zero);
        var nextFrame = TimeSpan.Zero;
        var nextReport = ReportInterval;
        ReadOnlyMemory<byte> retained = default;
        int sent = 0, liveEntries = 1, renewals = 0;
        long bytes = 0;
        var slowestWrite = TimeSpan.Zero;
        var liveActive = true;

        while (!StopRequested && isAuthorized())
        {
            abort.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _brightness) is { } brightness)
            {
                if (await ApplyBrightnessAsync(wire, brightness, isAuthorized, abort).ConfigureAwait(false))
                {
                    liveActive = false;
                    if (StopRequested || !isAuthorized() || !brightness.IsAuthorized()) break;
                    var requestedAt = clock.GetElapsedTime(origin);
                    await wire.EnterLiveAsync(abort).ConfigureAwait(false);
                    liveEntries++;
                    nextRenewal = ScheduleRenewal(requestedAt);
                    liveActive = true;
                }
                continue;
            }

            var now = clock.GetElapsedTime(origin);
            if (now >= nextRenewal)
            {
                await RenewAsync().ConfigureAwait(false);
                continue;
            }

            if (now >= nextFrame)
            {
                var latest = frames.TakeLatestEnvelope();
                if (!latest.IsEmpty) retained = latest;
                var frame = !latest.IsEmpty || frames.MayRepeat ? retained : default;
                if (StopRequested || !isAuthorized()) break;

                if (!frame.IsEmpty)
                {
                    // Encoding may cross the renewal deadline. Renew before starting an envelope.
                    var encodedAt = clock.GetElapsedTime(origin);
                    if (encodedAt >= nextRenewal
                        || (slowestWrite >= timing.FrameInterval && encodedAt + slowestWrite >= nextRenewal))
                    {
                        await RenewAsync().ConfigureAwait(false);
                        if (StopRequested || !isAuthorized()) break;
                    }
                    var writeStart = clock.GetTimestamp();
                    await wire.WriteAsync(frame, abort).ConfigureAwait(false);
                    var duration = clock.GetElapsedTime(writeStart);
                    if (duration > slowestWrite) slowestWrite = duration;
                    sent++;
                    bytes += frame.Length;
                    nextFrame = clock.GetElapsedTime(origin, writeStart) + timing.FrameInterval;
                    // Leave room for control when a full envelope already took a frame slot.
                    if (duration >= timing.FrameInterval)
                        nextFrame = clock.GetElapsedTime(origin) + timing.FrameInterval;
                }
                else
                {
                    nextFrame = clock.GetElapsedTime(origin) + timing.FrameInterval;
                }
            }

            now = clock.GetElapsedTime(origin);
            if (now >= nextReport)
            {
                Report(sent, liveEntries, renewals, bytes, slowestWrite);
                nextReport = now + ReportInterval;
            }

            var wakeAt = nextRenewal < nextFrame ? nextRenewal : nextFrame;
            if (wakeAt > now)
            {
                try { await Task.Delay(wakeAt - now, clock, _wake.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (StopRequested) { }
            }
        }

        Report(sent, liveEntries, renewals, bytes, slowestWrite);
        return liveActive;

        async ValueTask RenewAsync()
        {
            var requestedAt = clock.GetElapsedTime(origin);
            await wire.RenewLiveAsync(abort).ConfigureAwait(false);
            renewals++;
            nextRenewal = ScheduleRenewal(requestedAt);
        }

        TimeSpan ScheduleRenewal(TimeSpan requestedAt)
        {
            var scheduled = requestedAt + timing.LiveRenewInterval;
            var earliest = clock.GetElapsedTime(origin) + timing.FrameInterval;
            // Leave one frame slot after a slow command instead of immediately sending another 0x11.
            return scheduled >= earliest ? scheduled : earliest;
        }
    }

    private async ValueTask<bool> ApplyBrightnessAsync(D215Wire wire, BrightnessRequest request,
        Func<bool> isAuthorized, CancellationToken abort)
    {
        bool CanApply() => !StopRequested && isAuthorized() && request.IsAuthorized();
        try
        {
            if (request.Caller.IsCancellationRequested || !CanApply())
            {
                request.Complete("unavailable", null);
                return false;
            }

            await wire.ClearAsync(abort).ConfigureAwait(false);
            var info = await wire.SetBrightnessAsync(request.Value, CanApply,
                () => !request.Caller.IsCancellationRequested, _rememberInfo, abort).ConfigureAwait(false);
            if (info is null || !request.IsAuthorized()) request.Complete("unavailable", null);
            else request.Complete(info.Brightness == request.Value ? "confirmed" : "mismatch", info.Brightness);
            return true;
        }
        catch
        {
            request.Complete("failed", null);
            throw;
        }
        finally { Interlocked.CompareExchange(ref _brightness, null, request); }
    }

    private static void Report(int frames, int liveEntries, int renewals, long bytes, TimeSpan slowestWrite) =>
        ServiceLog.Info($"[jungle-leopard-d215] sent {frames} envelopes ({bytes} bytes), acknowledged {liveEntries} live entries, wrote {renewals} renewals, slowest serial write {slowestWrite.TotalMilliseconds:F0} ms");

    private static async Task SignalAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* Pump already ended. */ }
        catch (Exception ex) { ServiceLog.Warn($"[jungle-leopard-d215] stream cancellation failed ({ex.GetType().Name})"); }
    }

    private sealed class BrightnessRequest(int value, Func<bool> isAuthorized, CancellationToken caller)
    {
        private readonly TaskCompletionSource<D215BrightnessResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Value { get; } = value;
        internal Func<bool> IsAuthorized { get; } = isAuthorized;
        internal CancellationToken Caller { get; } = caller;
        internal Task<D215BrightnessResponse> Result => _result.Task;
        internal void Complete(string status, int? value) => _result.TrySetResult(new D215BrightnessResponse(status, value));
    }
}
