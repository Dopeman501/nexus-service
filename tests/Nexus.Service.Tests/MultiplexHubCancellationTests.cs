using System.Net.WebSockets;
using System.Text;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public sealed class MultiplexHubCancellationTests
{
    private const string Topic = "panel/audio-trigger-preview";
    private static readonly ReadOnlyMemory<byte> Envelope = Encoding.UTF8.GetBytes("preview");

    [Fact]
    public async Task Cancellation_retires_a_stalled_send_without_delaying_a_healthy_peer()
    {
        var hub = new MultiplexHub();
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        using var stalled = new ControlledSocket(holdSend: true);
        using var healthy = new ControlledSocket();
        var firstLoop = hub.HandleClientAsync(stalled, lifetime.Token);
        var secondLoop = hub.HandleClientAsync(healthy, lifetime.Token);
        Task? broadcast = null;
        try
        {
            Assert.Equal(2, hub.TopicSubscriberCount(Topic));
            broadcast = Broadcast(hub, deadline.Token);
            await stalled.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Every peer starts independently; the blocked peer does not own fan-out order.
            await healthy.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            deadline.Cancel();
            await broadcast.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, stalled.ActiveSends);
            Assert.True(stalled.Aborted);
            Assert.Equal(1, healthy.CompletedSends);
        }
        finally
        {
            stalled.ReleaseSend();
            lifetime.Cancel();
            if (broadcast is not null) await broadcast.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(firstLoop, secondLoop).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_write_lock_does_not_leave_queued_work_or_abort_its_owner()
    {
        var hub = new MultiplexHub();
        using var lifetime = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource();
        using var socket = new ControlledSocket(holdSend: true);
        var loop = hub.HandleClientAsync(socket, lifetime.Token);
        var first = hub.BroadcastTopicAsync(Topic, Envelope);
        Task? waiting = null;
        try
        {
            await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            waiting = Broadcast(hub, deadline.Token);
            deadline.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, socket.StartedSends);
            Assert.Equal(1, socket.ActiveSends);
            Assert.False(socket.Aborted);
            socket.ReleaseSend();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await Broadcast(hub, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, socket.CompletedSends);
            Assert.Equal(2, socket.StartedSends);
        }
        finally
        {
            socket.ReleaseSend();
            lifetime.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            if (waiting is not null) await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            await loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Disposing_peer_cancels_an_active_cancellable_send()
    {
        var hub = new MultiplexHub();
        using var lifetime = new CancellationTokenSource();
        using var socket = new ControlledSocket(holdSend: true);
        var loop = hub.HandleClientAsync(socket, lifetime.Token);
        var broadcast = Broadcast(hub, CancellationToken.None);
        try
        {
            await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lifetime.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(2));
            await broadcast.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, socket.ActiveSends);
            Assert.Equal(0, hub.ClientCount);
        }
        finally
        {
            socket.ReleaseSend();
            lifetime.Cancel();
            await Task.WhenAll(loop, broadcast).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static Task Broadcast(MultiplexHub hub, CancellationToken token) => hub.BroadcastTopicAsync(Topic, Envelope, token);

    private sealed class ControlledSocket(bool holdSend = false) : WebSocket
    {
        private readonly TaskCompletionSource _releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _subscribed;
        private WebSocketState _state = WebSocketState.Open;
        internal TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ActiveSends;
        internal int StartedSends;
        internal int CompletedSends;
        internal bool Aborted;
        internal void ReleaseSend() => _releaseSend.TrySetResult();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                var command = Encoding.UTF8.GetBytes($$"""{"sub":["{{Topic}}"]}""");
                command.CopyTo(buffer.Array!, buffer.Offset);
                return new WebSocketReceiveResult(command.Length, WebSocketMessageType.Text, true);
            }
            using var registration = cancellationToken.Register(() => _releaseReceive.TrySetResult());
            await _releaseReceive.Task;
            if (_state == WebSocketState.Open) _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartedSends);
            Interlocked.Increment(ref ActiveSends);
            SendStarted.TrySetResult();
            try
            {
                if (holdSend) await _releaseSend.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref CompletedSends);
            }
            finally { Interlocked.Decrement(ref ActiveSends); }
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { Aborted = true; _state = WebSocketState.Aborted; _releaseReceive.TrySetResult(); }
        public override void Dispose() { _releaseReceive.TrySetResult(); _releaseSend.TrySetResult(); }
    }
}
