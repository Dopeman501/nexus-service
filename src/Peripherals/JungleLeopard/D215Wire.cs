using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal sealed record D215Timing(TimeSpan OpenTimeout, TimeSpan PortSettle, TimeSpan ClearSettle,
    TimeSpan WriteTimeout, TimeSpan ReplyTimeout)
{
    internal const int FramesPerSecond = 60;

    internal static D215Timing Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(2600));

    internal TimeSpan FrameInterval { get; init; } = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
    internal TimeSpan LiveRenewInterval { get; init; } = TimeSpan.FromMilliseconds(1500);
    internal TimeSpan ReleaseTimeout { get; init; } = TimeSpan.FromSeconds(8);
    internal TimeSpan IdentificationTimeout => OpenTimeout + PortSettle + ClearSettle + WriteTimeout * 3 + ReplyTimeout;

    internal D215Timing AtFrameRate(int? framesPerSecond)
    {
        if (framesPerSecond is null) return this;
        if (framesPerSecond is < 1 or > FramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        return this with { FrameInterval = TimeSpan.FromSeconds(1.0 / framesPerSecond.Value) };
    }
}

/// <summary>Bounded wire operations. The session permits only one caller at a time.</summary>
internal sealed class D215Wire(ID215SerialTransport transport, TimeProvider clock, D215Timing timing)
{
    private const int MaximumReplyBytes = 16 * 1024;
    private static readonly byte[] ClearJpeg = [0xFF, 0xD9, 0xFF, 0xD9];
    private static readonly byte[] ClearLength = [0, 0, 0, 0];
    private TransferDeadline? _activeDeadline;

    internal D215ConnectionStatus? TimedOutOperation =>
        Volatile.Read(ref _activeDeadline) is { Token.IsCancellationRequested: true } deadline ? deadline.Status : null;

    internal async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        await WriteAsync(ClearJpeg, cancellationToken).ConfigureAwait(false);
        await WriteAsync(ClearLength, cancellationToken).ConfigureAwait(false);
        await Task.Delay(timing.ClearSettle, clock, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask EnterLiveAsync(CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(D215Protocol.BuildLiveModeRequest(), D215Protocol.LiveModeCommand,
            cancellationToken).ConfigureAwait(false);
        if (reply is not [0])
            throw new D215ConnectionException(D215ConnectionStatus.InvalidReply, "The D215 did not acknowledge live mode.");
    }

    // Firmware 3.4 acknowledges entry and stays silent while already live.
    internal ValueTask RenewLiveAsync(CancellationToken cancellationToken) =>
        WriteAsync(D215Protocol.BuildLiveModeRequest(), cancellationToken);

    internal async ValueTask<D215DeviceInfo> ReadInfoAsync(CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(D215Protocol.BuildInfoRequest(), D215Protocol.InfoCommand,
            cancellationToken).ConfigureAwait(false);
        if (!D215DeviceInfo.TryParse(reply, out var info))
            throw new D215ConnectionException(D215ConnectionStatus.InvalidReply, "The device info reply was malformed.");
        if (!info.IsSupported)
            throw new D215ConnectionException(D215ConnectionStatus.Unsupported, "The device info reply identified an unsupported model or firmware.");
        return info;
    }

    internal async ValueTask<D215DeviceInfo?> SetBrightnessAsync(int value, Func<bool> isAuthorized, Func<bool> mayCommit,
        Action<D215DeviceInfo?> remember, CancellationToken cancellationToken)
    {
        if (!isAuthorized() || !mayCommit()) return null;
        var before = await ReadInfoAsync(cancellationToken).ConfigureAwait(false);
        remember(before);
        if (!isAuthorized() || !mayCommit()) return null;
        if (before.Brightness == value) return before;

        // The old value stops being authoritative as soon as a write can reach the wire.
        remember(null);
        var ack = await ExchangeAsync(D215Protocol.BuildBrightnessRequest(value), D215Protocol.BrightnessCommand,
            cancellationToken).ConfigureAwait(false);
        if (ack is not [0])
            throw new D215ConnectionException(D215ConnectionStatus.InvalidReply, "The D215 did not acknowledge the brightness write.");
        if (!isAuthorized()) return null;
        var after = await ReadInfoAsync(cancellationToken).ConfigureAwait(false);
        remember(after);
        return after;
    }

    internal async ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request, byte command, CancellationToken cancellationToken)
    {
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timing.WriteTimeout, clock);
        using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Volatile.Write(ref _activeDeadline, new TransferDeadline(deadline.Token, D215ConnectionStatus.WriteTimedOut));
        try
        {
            writing.Token.ThrowIfCancellationRequested();
            await transport.WriteAsync(bytes, writing.Token).ConfigureAwait(false);
            writing.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new D215ConnectionException(D215ConnectionStatus.WriteTimedOut, "A serial write timed out.", ex);
        }
        finally { Volatile.Write(ref _activeDeadline, null); }
    }

    private async ValueTask<byte[]> ReadReplyAsync(byte expectedCommand, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timing.ReplyTimeout, clock);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        // Any already buffered trailing frames belong to this transaction, never the next one.
        var framer = new D215ControlReader();
        var buffer = new byte[D215ControlReader.MaximumAppendBytes];
        Volatile.Write(ref _activeDeadline, new TransferDeadline(deadline.Token, D215ConnectionStatus.NoResponse));
        try
        {
            int received = 0;
            while (received < MaximumReplyBytes)
            {
                var count = await transport.ReadAsync(buffer, reading.Token).ConfigureAwait(false);
                reading.Token.ThrowIfCancellationRequested();
                if (count < 0 || count > buffer.Length)
                    throw new IOException("The serial transport returned an invalid byte count.");
                if (count == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), clock, reading.Token).ConfigureAwait(false);
                    continue;
                }
                received += count;
                framer.Append(buffer.AsSpan(0, count));
                while (framer.TryRead(out var command, out var payload))
                    if (command == expectedCommand) return payload;
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new D215ConnectionException(D215ConnectionStatus.NoResponse, "No serial reply arrived before the deadline.", ex);
        }
        finally { Volatile.Write(ref _activeDeadline, null); }
        throw new D215ConnectionException(D215ConnectionStatus.NoResponse, "No serial reply arrived within the receive budget.");
    }

    private sealed record TransferDeadline(CancellationToken Token, D215ConnectionStatus Status);
}
