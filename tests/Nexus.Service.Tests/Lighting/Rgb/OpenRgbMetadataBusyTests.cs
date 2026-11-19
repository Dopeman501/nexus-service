using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public sealed class OpenRgbMetadataBusyTests
{
    private const uint Capability = 1u << 17;
    private const uint Busy = 6;

    [Theory]
    [InlineData("settings")]
    [InlineData("rescan")]
    [InlineData("disconnect")]
    public async Task Redetection_commands_recheck_generation_after_waiting_for_request_admission(string command)
    {
        await using var daemon = new Daemon(grant: true);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        var transport = (IOpenRgbRefreshTransport)controller;
        var generation = controller.ConnectionGeneration;
        var requestLock = (SemaphoreSlim)typeof(OpenRgbController).GetField("_requestLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;
        await requestLock.WaitAsync();
        Task<bool?> pending;
        try
        {
            pending = command switch
            {
                "settings" => transport.TrySetSettingsAsync("Detectors", "{}", generation, () => true, default),
                "rescan" => transport.TryRescanAsync(generation, () => true, default),
                _ => Disconnect(),
            };
            Assert.False(pending.IsCompleted);
            typeof(OpenRgbController).GetField("_connectionGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(controller, generation + 1);
        }
        finally { requestLock.Release(); }
        Assert.NotEqual(true, await pending);
        Assert.True(controller.IsConnected);
        await controller.GetControllerAddressesAsync();
        Assert.Equal(0, daemon.RefreshCommands);
        async Task<bool?> Disconnect() => await transport.TryDisconnectAsync(generation, () => true, default);
    }

    [Fact]
    public async Task Current_redetection_commands_preserve_acknowledgement_and_local_client_requirements()
    {
        await using var daemon = new Daemon(grant: true);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        var transport = (IOpenRgbRefreshTransport)controller;
        var generation = controller.ConnectionGeneration;
        Assert.Null(await transport.TrySetSettingsAsync("Detectors", "{}", generation, () => false, default));
        Assert.Null(await transport.TryRescanAsync(generation, () => false, default));
        Assert.True(await transport.TrySetSettingsAsync("Detectors", "{}", generation, () => true, default));
        Assert.True(await transport.TryRescanAsync(generation, () => true, default));
        Assert.Equal(2, daemon.RefreshCommands);
        Assert.False(await transport.TryDisconnectAsync(generation, () => false, default));
        Assert.True(controller.IsConnected);
        Assert.True(await transport.TryDisconnectAsync(generation, () => true, default));
        Assert.False(controller.IsConnected);
        Assert.False(await transport.TrySetSettingsAsync("Detectors", "{}", generation, () => true, default));
        Assert.False(await transport.TryRescanAsync(generation, () => true, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_commands_check_expected_connection_after_waiting_for_the_request_lock(bool resize)
    {
        await using var daemon = new Daemon(grant: true);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        var transport = (IOpenRgbRefreshTransport)controller;
        var generation = controller.ConnectionGeneration;
        var requestLock = (SemaphoreSlim)typeof(OpenRgbController).GetField("_requestLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;
        await requestLock.WaitAsync();
        Task<bool> pending;
        try
        {
            pending = resize
                ? transport.TryResizeZoneAsync(3, 0, 8, generation, () => true, default)
                : transport.TrySetDirectModeAsync(new() { Address = 3 }, generation, () => true, default);
            Assert.False(pending.IsCompleted);
            // Deterministically advance the connection epoch while admission is parked.
            typeof(OpenRgbController).GetField("_connectionGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(controller, generation + 1);
        }
        finally { requestLock.Release(); }
        Assert.False(await pending);
        await controller.GetControllerAddressesAsync(); // Drain through an acknowledged request before counting writes.
        Assert.Equal(0, daemon.RefreshCommands);
    }

    [Fact]
    public async Task Refresh_admission_preserves_healthy_commands_and_rejects_retired_lifecycles()
    {
        await using var daemon = new Daemon(grant: true);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        var transport = (IOpenRgbRefreshTransport)controller;
        var generation = controller.ConnectionGeneration;
        Assert.False(await transport.TrySetDirectModeAsync(new() { Address = 3 }, generation, () => false, default));
        Assert.False(await transport.TryResizeZoneAsync(3, 0, 8, generation, () => false, default));
        Assert.True(await transport.TrySetDirectModeAsync(new() { Address = 3 }, generation, () => true, default));
        Assert.True(await transport.TryResizeZoneAsync(3, 0, 8, generation, () => true, default));
        await controller.GetControllerAddressesAsync();
        Assert.Equal(2, daemon.RefreshCommands);
        var published = 0;
        Assert.True(transport.TryPublishConnection(generation, () => published++));
        Assert.False(transport.TryPublishConnection(generation - 1, () => published++));
        Assert.Equal(1, published);
    }

    [Fact]
    public async Task Client_requests_nonblocking_metadata_capability()
    {
        await using var daemon = new Daemon(grant: true);
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        Assert.NotEqual(0u, daemon.ClientFlags & Capability);
    }

    [Fact]
    public async Task Negotiated_busy_rejects_partial_list_without_retiring_stream_or_generation()
    {
        await using var daemon = new Daemon(grant: true) { BusySecondDevice = true };
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        var generation = controller.ConnectionGeneration;

        var error = await Record.ExceptionAsync(() => controller.GetDevicesAsync());
        var busy = Assert.IsType<OpenRgbMetadataBusyException>(error);
        Assert.Equal(4, busy.ControllerAddress);
        Assert.Equal(generation, busy.ConnectionGeneration);
        Assert.True(controller.IsConnected);
        Assert.Equal(generation, controller.ConnectionGeneration);
        await controller.PushFrameAsync(3, new[] { new RgbColor(1, 2, 3) });
        await daemon.FrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        daemon.BusySecondDevice = false;
        Assert.Equal(2, (await controller.GetDevicesAsync()).Count);
        Assert.Equal(new[] { 3, 4 }, await controller.GetControllerAddressesAsync());
        Assert.True(controller.IsConnected);
        Assert.Equal(generation, controller.ConnectionGeneration);
    }

    [Fact]
    public async Task Ungranted_busy_retains_legacy_missing_metadata_behaviour()
    {
        await using var daemon = new Daemon(grant: false) { BusySecondDevice = true };
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());

        Assert.Equal(3, Assert.Single(await controller.GetDevicesAsync()).Address);
        Assert.True(controller.IsConnected);
    }

    [Fact]
    public async Task Late_busy_ack_cannot_complete_a_later_count_request()
    {
        await using var daemon = new Daemon(grant: true) { BusySecondDevice = true };
        await using var controller = new OpenRgbController(port: daemon.Port);
        Assert.True(await controller.TryConnectAsync());
        await Assert.ThrowsAsync<OpenRgbMetadataBusyException>(() => controller.GetDevicesAsync());

        Assert.Equal(new[] { 3, 4 }, await controller.GetControllerAddressesAsync());
        Assert.True(controller.IsConnected);
    }

    private sealed class Daemon : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly bool _grant;
        private bool _lateBusyAck;
        private TcpClient? _peer;
        public volatile bool BusySecondDevice;
        public uint ClientFlags { get; private set; }
        public int RefreshCommands;
        public TaskCompletionSource FrameReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Daemon(bool grant)
        {
            _grant = grant;
            _listener.Start();
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                _peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = _peer.GetStream();
                while (!_stop.IsCancellationRequested)
                {
                    var header = new byte[16];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var (address, id, length) = OpenRgbProtocol.ReadHeader(header);
                    var body = new byte[length];
                    if (length != 0) await stream.ReadExactlyAsync(body, _stop.Token);
                    var status = await RespondAsync(stream, address, id, body);
                    var ack = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(ack, (uint)id);
                    BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4), status);
                    await SendAsync(stream, address, OpenRgbProtocol.PacketId.Ack, ack);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        }

        private async Task<uint> RespondAsync(NetworkStream stream, uint address, OpenRgbProtocol.PacketId id, byte[] body)
        {
            if (id == OpenRgbProtocol.PacketId.RequestProtocolVersion)
                await SendAsync(stream, 0, id, BitConverter.GetBytes(6u));
            else if (id == OpenRgbProtocol.PacketId.SetClientFlags)
            {
                ClientFlags = BinaryPrimitives.ReadUInt32LittleEndian(body);
                await SendAsync(stream, 0, OpenRgbProtocol.PacketId.SetServerFlags,
                    BitConverter.GetBytes(OpenRgbProtocol.ServerFlagLocalClient | (_grant ? Capability : 0)));
            }
            else if (id == OpenRgbProtocol.PacketId.RequestControllerCount)
            {
                if (_lateBusyAck)
                {
                    _lateBusyAck = false;
                    var lateAck = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(lateAck, (uint)OpenRgbProtocol.PacketId.RequestControllerData);
                    BinaryPrimitives.WriteUInt32LittleEndian(lateAck.AsSpan(4), Busy);
                    await SendAsync(stream, 4, OpenRgbProtocol.PacketId.Ack, lateAck);
                }
                await SendAsync(stream, 0, id, new byte[] { 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0 });
            }
            else if (id == OpenRgbProtocol.PacketId.RequestControllerData)
            {
                if (address == 4 && BusySecondDevice)
                {
                    _lateBusyAck = true;
                    return Busy;
                }
                using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "OpenRgb", "controllers-v6.json")));
                await SendAsync(stream, address, id, Convert.FromHexString(fixture.RootElement.GetProperty("controllers")[0].GetString()!));
            }
            else if (id == OpenRgbProtocol.PacketId.RgbControllerUpdateLeds) FrameReceived.TrySetResult();
            else if (id is OpenRgbProtocol.PacketId.SetCustomMode or OpenRgbProtocol.PacketId.RgbControllerResizeZone
                or OpenRgbProtocol.PacketId.RgbControllerUpdateMode or OpenRgbProtocol.PacketId.SettingsManagerSetSettings
                or OpenRgbProtocol.PacketId.RequestRescanDevices)
                Interlocked.Increment(ref RefreshCommands);
            return OpenRgbProtocol.StatusOk;
        }

        private static async Task SendAsync(NetworkStream stream, uint address, OpenRgbProtocol.PacketId id, byte[] body)
        {
            var packet = new byte[16 + body.Length];
            OpenRgbProtocol.WriteHeader(packet, address, id, (uint)body.Length);
            body.CopyTo(packet, 16);
            await stream.WriteAsync(packet);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _peer?.Dispose();
            _listener.Stop();
            await _serving;
            _stop.Dispose();
        }
    }
}
