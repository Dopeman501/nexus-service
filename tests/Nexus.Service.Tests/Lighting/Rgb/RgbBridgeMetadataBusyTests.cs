using System.Reflection;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public sealed class RgbBridgeMetadataBusyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexus-busy-" + Guid.NewGuid().ToString("N"));
    private readonly FakeController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;

    public RgbBridgeMetadataBusyTests()
    {
        Directory.CreateDirectory(_directory);
        _bridge = new RgbBridge(new OpenRgbProcessManager(Path.Combine(_directory, "missing-openrgb")),
            _controller, _engine, new TestableConfigStore(Path.Combine(_directory, "settings.json")), new StubUsbEnumerator());
        SetField("_active", true); // Exercise owner methods deterministically, without its background timer/process startup.
    }

    [Fact]
    public async Task Busy_preserves_complete_graph_and_buffers_then_retries_unchanged_ids_once_per_poll()
    {
        await Invoke("RefreshDevicesAsync");
        var devices = _bridge.Devices;
        var frames = _engine.Devices;
        var buffers = Field<IDictionary<int, RgbColor[]>>("_physBuffers");
        var firstBuffer = buffers[3];
        SetField("_daemonFailures", 1);
        _controller.Busy = true;

        await Invoke("RefreshDevicesAsync");
        Assert.Equal(1, Field<int>("_daemonFailures")); // Busy itself does not affect transport health.
        await Invoke("PollAsync");
        await Invoke("PollAsync");

        Assert.Equal(4, _controller.Fetches);
        Assert.Same(devices, _bridge.Devices);
        Assert.Same(frames, _engine.Devices);
        Assert.Same(firstBuffer, buffers[3]);
        Assert.Equal(0, Field<int>("_daemonFailures")); // Successful count liveness checks retain their existing reset.
        Assert.True(_controller.IsConnected);

        _controller.Busy = false;
        await Invoke("PollAsync");
        Assert.Equal(5, _controller.Fetches);
        Assert.Same(frames[0], _engine.Devices[0]);
        Assert.Equal(0, Field<int>("_daemonFailures"));
        await Invoke("PollAsync");
        Assert.Equal(5, _controller.Fetches); // Successful refresh retires the deferred intent.
    }

    [Fact]
    public async Task Replaced_connection_does_not_inherit_deferred_metadata_intent()
    {
        await Invoke("RefreshDevicesAsync");
        _controller.Busy = true;
        await Invoke("RefreshDevicesAsync");
        _controller.Generation++;
        await Invoke("PollAsync");
        Assert.Equal(2, _controller.Fetches);
    }

    [Fact]
    public async Task Deactivation_clears_deferred_metadata_intent()
    {
        await Invoke("RefreshDevicesAsync");
        _controller.Busy = true;
        await Invoke("RefreshDevicesAsync");
        _bridge.Deactivate();
        _bridge.AwaitShutdown();
        SetField("_active", true);
        await Invoke("PollAsync");
        Assert.Equal(2, _controller.Fetches);
    }

    [Fact]
    public async Task Late_busy_after_deactivation_cannot_recreate_a_retry_in_a_new_scope()
    {
        await Invoke("RefreshDevicesAsync");
        _controller.ParkedFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = Invoke("RefreshDevicesAsync");
        _bridge.Deactivate();
        _bridge.AwaitShutdown();
        SetField("_active", true);
        _controller.ParkedFetch.SetException(new OpenRgbMetadataBusyException(4, _controller.Generation));
        await refreshing;
        _controller.ParkedFetch = null;
        await Invoke("PollAsync");
        Assert.Equal(2, _controller.Fetches);
    }

    [Fact]
    public async Task Late_busy_from_an_old_generation_cannot_defer_the_new_connection()
    {
        await Invoke("RefreshDevicesAsync");
        _controller.ParkedFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = Invoke("RefreshDevicesAsync");
        var oldGeneration = _controller.Generation++;
        _controller.ParkedFetch.SetException(new OpenRgbMetadataBusyException(4, oldGeneration));
        await refreshing;
        _controller.ParkedFetch = null;
        await Invoke("PollAsync");
        Assert.Equal(2, _controller.Fetches);
    }

    [Fact]
    public async Task Busy_during_post_resize_refetch_keeps_the_previously_committed_graph()
    {
        await Invoke("RefreshDevicesAsync");
        var devices = _bridge.Devices;
        var frames = _engine.Devices;
        SetField("_daemonFailures", 1);
        Field<System.Collections.Concurrent.ConcurrentQueue<(int, int, int)>>("_pendingZoneResizes").Enqueue((3, 0, 8));
        _controller.BusyOnResize = true;

        await Invoke("RefreshDevicesAsync");

        Assert.Equal(3, _controller.Fetches);
        Assert.Same(devices, _bridge.Devices);
        Assert.Same(frames, _engine.Devices);
        Assert.Equal(1, Field<int>("_daemonFailures"));
        _controller.Busy = false;
        await Invoke("PollAsync");
        Assert.Equal(4, _controller.Fetches);
    }

    private Task Invoke(string method) => (Task)typeof(RgbBridge).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, null)!;
    private T Field<T>(string name) => (T)typeof(RgbBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_bridge)!;
    private void SetField(string name, object value) => typeof(RgbBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_bridge, value);

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        Directory.Delete(_directory, true);
    }

    private sealed class FakeController : IRgbController, IOpenRgbWriteHealthTransport
    {
        public bool Busy;
        public bool BusyOnResize;
        public int Fetches;
        public int Generation = 1;
        public TaskCompletionSource<IReadOnlyList<RgbDevice>>? ParkedFetch;
        public bool IsConnected => true;
        public int ConnectionGeneration => Generation;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>(new[] { 3, 4 });
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            Fetches++;
            if (ParkedFetch is not null) return ParkedFetch.Task;
            if (Busy) throw new OpenRgbMetadataBusyException(4, Generation);
            return Task.FromResult<IReadOnlyList<RgbDevice>>(new[]
            {
                new RgbDevice { Index = 0, Address = 3, Name = "DRAM A", Serial = "synthetic-a", Type = 1, LedCount = 2 },
                new RgbDevice { Index = 1, Address = 4, Name = "DRAM B", Serial = "synthetic-b", Type = 1, LedCount = 2 },
            });
        }
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int count, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zone, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zone, int size, CancellationToken ct = default)
        {
            if (BusyOnResize) Busy = true;
            return Task.CompletedTask;
        }
        public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetSettingsAsync(string key, string json, CancellationToken ct = default) => Task.FromResult(true);
        public Task<OpenRgbWriteHealthResult> GetWriteHealthAsync(int address, CancellationToken ct = default) => Task.FromResult(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.Unsupported, default));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
