using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>Owns the exclusive port from a successful info handshake through disposal.</summary>
internal sealed class D215Session : IAsyncDisposable
{
    private readonly ID215SerialTransport _transport;
    private readonly D215Wire _wire;
    private readonly TimeProvider _clock;
    private readonly D215Timing _timing;
    private readonly object _disposeLock = new();
    private D215DeviceInfo? _confirmedInfo;
    private D215LiveStream? _live;
    private Task? _disposeTask;

    private D215Session(D215Port port, D215DeviceInfo info, ID215SerialTransport transport,
        D215Wire wire, TimeProvider clock, D215Timing timing)
    {
        Port = port;
        Info = info;
        _confirmedInfo = info;
        _transport = transport;
        _wire = wire;
        _clock = clock;
        _timing = timing;
    }

    internal D215Port Port { get; }
    internal D215DeviceInfo Info { get; }
    internal int? ConfirmedBrightness => Volatile.Read(ref _confirmedInfo)?.Brightness;

    internal bool CanStream
    {
        get { lock (_disposeLock) return _disposeTask is null && _live?.Failure is null && _wire.TimedOutOperation is null; }
    }

    internal Exception? StreamFailure
    {
        get
        {
            lock (_disposeLock)
            {
                return _live?.Failure ?? (_wire.TimedOutOperation is { } status
                    ? new D215ConnectionException(status, "A D215 serial transfer did not finish within its budget.")
                    : null);
            }
        }
    }

    internal Task? GetRetiringStreamCompletion()
    {
        lock (_disposeLock)
        {
            return _live is { StopRequested: true, Completion.IsCompleted: false } retiring
                ? retiring.Completion
                : null;
        }
    }

    internal static async ValueTask<D215Session> ConnectAsync(D215Port port, ID215PortDiscovery discovery,
        Func<D215Port, CancellationToken, ValueTask<ID215SerialTransport>> open, TimeProvider clock,
        D215Timing timing, CancellationToken cancellationToken)
    {
        ID215SerialTransport? transport = null;
        try
        {
            using (var deadline = new CancellationTokenSource(timing.OpenTimeout, clock))
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            {
                try
                {
                    opening.Token.ThrowIfCancellationRequested();
                    transport = await open(port, opening.Token).ConfigureAwait(false);
                    opening.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    throw new D215ConnectionException(D215ConnectionStatus.OpenTimedOut, "The serial open timed out.", ex);
                }
            }

            // Verify ownership before even sending the raw clear sequence. A COM number can be reused.
            var currentPorts = discovery.Discover();
            if (currentPorts.Count != 1 || !currentPorts.Contains(port))
                throw new D215ConnectionException(D215ConnectionStatus.PortChanged, "The verified device interface changed during open.");

            var wire = new D215Wire(transport, clock, timing);
            await Task.Delay(timing.PortSettle, clock, cancellationToken).ConfigureAwait(false);
            await wire.ClearAsync(cancellationToken).ConfigureAwait(false);
            var info = await wire.ReadInfoAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new D215Session(port, info, transport, wire, clock, timing);
        }
        catch (Exception failure)
        {
            if (transport is not null)
            {
                try { await transport.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    throw new D215ConnectionException(D215ConnectionStatus.CleanupFailed, "The serial connection failed and release could not be confirmed.",
                        new AggregateException(failure, cleanup));
                }
            }
            throw;
        }
    }

    internal D215LiveStream BeginStreaming(ID215FrameSource frames, Func<bool> isAuthorized, int? framesPerSecond = null)
    {
        var streamTiming = _timing.AtFrameRate(framesPerSecond);
        if (!isAuthorized()) throw new InvalidOperationException("The identified D215 session is no longer authorized.");
        lock (_disposeLock)
        {
            if (_disposeTask is not null || _live?.Failure is not null)
                throw new InvalidOperationException("The D215 serial session is closing or faulted.");
            if (_live is { Completion.IsCompleted: false })
                throw new InvalidOperationException("The D215 serial session already has a stream owner.");
            return _live = new D215LiveStream(_wire, frames, isAuthorized, _clock, streamTiming,
                info => Volatile.Write(ref _confirmedInfo, info));
        }
    }

    internal ValueTask<D215BrightnessResponse> SetBrightnessAsync(int value, Func<bool> isAuthorized, CancellationToken cancellationToken)
    {
        D215LiveStream? live;
        lock (_disposeLock) live = _disposeTask is null ? _live : null;
        return live is not null
            ? live.SetBrightnessAsync(value, isAuthorized, cancellationToken)
            : ValueTask.FromResult(new D215BrightnessResponse("unavailable", null));
    }

    internal void RequestStreamStop()
    {
        lock (_disposeLock) _live?.RequestStop();
    }

    public ValueTask DisposeAsync() => CloseAsync(clearIfPresent: false);

    internal ValueTask CloseAsync(bool clearIfPresent)
    {
        lock (_disposeLock)
        {
            if (clearIfPresent) _live?.RequestStop();
            else _live?.Abort();
            if (_disposeTask is null)
            {
                var stream = _live;
                _disposeTask = Task.Run(() => ClosePortAsync(stream));
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task ClosePortAsync(D215LiveStream? stream)
    {
        try
        {
            if (stream is not null) await stream.Completion.ConfigureAwait(false);
        }
        finally { await _transport.DisposeAsync().ConfigureAwait(false); }
    }
}
