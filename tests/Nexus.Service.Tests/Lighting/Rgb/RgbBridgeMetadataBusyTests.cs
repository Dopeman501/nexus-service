using System.Reflection;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public sealed partial class RgbBridgeMetadataBusyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexus-busy-" + Guid.NewGuid().ToString("N"));
    private readonly FakeController _controller = new();
    private readonly LightingEngine _engine = new();
    private readonly RgbBridge _bridge;
    private readonly TestableConfigStore _store;
    private int _redetections;

    public RgbBridgeMetadataBusyTests()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "openrgb-config"));
        _store = new TestableConfigStore(Path.Combine(_directory, "settings.json"));
        var process = new OpenRgbProcessManager(Path.Combine(_directory, "missing-openrgb"))
        {
            ConfigDirectoryResolver = () => Path.Combine(_directory, "openrgb-config"),
        };
        _bridge = new RgbBridge(process, _controller, _engine, _store, new StubUsbEnumerator())
        {
            RedetectOverride = (_, _) => _redetections++,
        };
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
        _controller.NextDevices = [ResizeBoard()];
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_success_cannot_pin_resize_publish_or_complete_the_new_scope(bool replaceConnection)
    {
        await Invoke("RefreshDevicesAsync");
        var frames = _engine.Devices;
        var physical = Field<IDictionary<int, RgbColor[]>>("_physBuffers")[3];
        var completed = Field<TaskCompletionSource>("_listCommitted");
        var directCalls = _controller.DirectCalls;
        var notifications = 0;
        _bridge.DeviceSetChanged += () => notifications++;
        _controller.ParkedFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRefresh = Invoke("RefreshDevicesAsync");
        if (replaceConnection) _controller.Generation++;
        else
        {
            _bridge.Deactivate();
            _bridge.AwaitShutdown();
            SetField("_active", true); // New lifecycle, without spawning any process/timer.
        }
        var currentDevices = _bridge.Devices;
        var retry = Field<OpenRgbMetadataRetry>("_metadataRetry");
        retry.Defer(retry.Scope, _controller.Generation, _controller.Generation);
        var pending = Field<System.Collections.Concurrent.ConcurrentQueue<RgbBridge.PendingIdResize>>("_pendingIdZoneResizes");
        pending.Enqueue(new("current-unresolved-device", 0, 17, DateTime.UtcNow.Ticks));
        var oldBoard = OldBoard();
        _controller.ParkedFetch.SetResult([oldBoard]);
        await oldRefresh;

        Assert.Same(currentDevices, _bridge.Devices);
        Assert.Same(frames, _engine.Devices);
        Assert.Same(physical, Field<IDictionary<int, RgbColor[]>>("_physBuffers")[3]);
        Assert.Same(completed, Field<TaskCompletionSource>("_listCommitted"));
        Assert.False(completed.Task.IsCompleted);
        Assert.Empty(_store.Load().Devices.OpenRgbPinnedIds);
        Assert.Empty(_store.Load().Devices.ZoneLedCounts);
        Assert.Equal(directCalls, _controller.DirectCalls);
        Assert.Equal(0, _controller.ResizeCalls);
        Assert.Equal(0, _redetections);
        Assert.Equal(0, notifications);
        Assert.Single(pending);
        Assert.True(retry.NeedsRetry(_controller.Generation));
    }

    [Fact]
    public async Task Retired_post_resize_success_cannot_publish_or_pin_the_old_list()
    {
        await Invoke("RefreshDevicesAsync");
        var devices = _bridge.Devices;
        var frames = _engine.Devices;
        _controller.ParkFetchAfterResize = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.NextDevices = [ResizeBoard()];
        Field<System.Collections.Concurrent.ConcurrentQueue<(int, int, int)>>("_pendingZoneResizes").Enqueue((3, 0, 8));
        var refreshing = Invoke("RefreshDevicesAsync");
        await _controller.PostResizeFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _controller.Generation++;
        _controller.ParkFetchAfterResize.SetResult([OldBoard()]);
        await refreshing;
        Assert.Same(devices, _bridge.Devices);
        Assert.Same(frames, _engine.Devices);
        Assert.Empty(_store.Load().Devices.OpenRgbPinnedIds);
        Assert.Empty(_store.Load().Devices.ZoneLedCounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_command_completion_cannot_commit_mode_or_issue_the_next_command(bool resize)
    {
        await Invoke("RefreshDevicesAsync");
        var devices = _bridge.Devices;
        var frames = _engine.Devices;
        _controller.NextDevices = resize ? [OldBoard()] :
            [new() { Address = 20, Serial = "old-a", Type = 1, LedCount = 2 }, new() { Address = 21, Serial = "old-b", Type = 1, LedCount = 2 }];
        _controller.ParkedCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _controller.ParkResize = resize;
        var oldRefresh = Invoke("RefreshDevicesAsync");
        await _controller.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _controller.Generation++;
        var resizes = _controller.ResizeCalls;
        var modes = _controller.DirectCalls;
        _controller.ParkedCommand.SetResult();
        await oldRefresh;
        Assert.Same(devices, _bridge.Devices);
        Assert.Same(frames, _engine.Devices);
        Assert.Equal(modes, _controller.DirectCalls);
        Assert.Equal(resizes, _controller.ResizeCalls);
        Assert.DoesNotContain(20, Field<HashSet<int>>("_directModeApplied"));
        Assert.DoesNotContain(21, Field<HashSet<int>>("_directModeApplied"));
    }

    private static RgbDevice OldBoard() => new()
    {
        Address = 9, Index = 9, Name = "Retired synthetic board", Type = 0, LedCount = 0,
        Zones = [new() { ZoneType = 1, LedsMax = 120, Flags = RgbZone.FlagConfigurableSize },
            new() { ZoneType = 1, LedsMax = 120, Flags = RgbZone.FlagConfigurableSize }],
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_failure_or_cancellation_does_not_change_current_failure_or_retry_state(bool cancel)
    {
        await Invoke("RefreshDevicesAsync");
        _controller.ParkedFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Invoke("RefreshDevicesAsync");
        _controller.Generation++;
        SetField("_daemonFailures", 1);
        var retry = Field<OpenRgbMetadataRetry>("_metadataRetry");
        retry.Defer(retry.Scope, _controller.Generation, _controller.Generation);
        _controller.ParkedFetch.SetException(cancel ? new OperationCanceledException() : new IOException("retired synthetic read"));
        await old;
        Assert.Equal(1, Field<int>("_daemonFailures"));
        Assert.True(retry.NeedsRetry(_controller.Generation));
    }

    [Fact]
    public async Task Synchronous_device_notification_can_deactivate_without_later_buffer_publication()
    {
        await Invoke("RefreshDevicesAsync");
        var buffers = Field<IDictionary<int, RgbColor[]>>("_physBuffers");
        var first = buffers[3];
        _controller.NextDevices = [new() { Address = 20, Serial = "new", Type = 1, LedCount = 8 }];
        _bridge.DeviceSetChanged += _bridge.Deactivate;
        await Invoke("RefreshDevicesAsync").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(_bridge.IsActive);
        Assert.Empty(_bridge.Devices);
        Assert.Same(first, buffers[3]);
        Assert.False(buffers.ContainsKey(20));
        Assert.Empty(Field<HashSet<int>>("_directModeApplied"));
    }

    [Fact]
    public async Task Unadmitted_resize_keeps_stable_target_and_newer_intent_across_a_connection_change()
    {
        await Invoke("RefreshDevicesAsync");
        var board = ResizeBoard();
        _controller.NextDevices = [board];
        _controller.ParkAdmission = true;
        var queue = Field<System.Collections.Concurrent.ConcurrentQueue<RgbBridge.PendingIdResize>>("_pendingIdZoneResizes");
        queue.Enqueue(new(board.StableId, 0, 8, DateTime.UtcNow.Ticks));
        var old = Invoke("RefreshDevicesAsync");
        await _controller.AdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _controller.Generation++;
        queue.Enqueue(new(board.StableId, 0, 9, DateTime.UtcNow.AddSeconds(1).Ticks));
        queue.Enqueue(new(board.StableId, 0, 7, DateTime.UtcNow.AddSeconds(-1).Ticks)); // A delayed older intent cannot replace the latest.
        _controller.AdmissionRelease.SetResult();
        await old;
        Assert.Equal(0, _controller.ResizeCalls);
        _controller.ParkAdmission = false;
        var replacement = ResizeBoard();
        replacement.Address = 30;
        _controller.NextDevices = [replacement];
        await Invoke("RefreshDevicesAsync");
        Assert.Equal(new[] { (30, 9) }, _controller.ResizeTargets);
        Assert.Empty(queue);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("disconnect")]
    public async Task Retired_success_respects_cancellation_disposal_and_connection_loss(string cause)
    {
        await Invoke("RefreshDevicesAsync");
        var devices = _bridge.Devices;
        using var cancellation = new CancellationTokenSource();
        SetField("_refreshCts", cancellation);
        _controller.ParkedFetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Invoke("RefreshDevicesAsync");
        if (cause == "cancel") cancellation.Cancel();
        else if (cause == "dispose") SetField("_disposed", true);
        else _controller.Connected = false;
        _controller.ParkedFetch.SetResult([OldBoard()]);
        await old;
        Assert.Same(devices, _bridge.Devices);
        Assert.Empty(_store.Load().Devices.OpenRgbPinnedIds);
        SetField("_disposed", false); // Let fixture teardown dispose its subscriptions normally.
    }

    [Fact]
    public async Task Queued_resizes_expire_or_wait_by_stable_identity_without_driving_unsizable_devices()
    {
        await Invoke("RefreshDevicesAsync");
        _store.Update(settings => settings.Devices.UncontrolledLightingDevices.Add(_bridge.Devices[0].StableId));
        var queue = Field<System.Collections.Concurrent.ConcurrentQueue<RgbBridge.PendingIdResize>>("_pendingIdZoneResizes");
        queue.Enqueue(new("expired", 0, 9, DateTime.UtcNow.AddMinutes(-2).Ticks));
        queue.Enqueue(new("waiting", 0, 9, DateTime.UtcNow.Ticks));
        queue.Enqueue(new(_bridge.Devices[0].StableId, 0, 9, DateTime.UtcNow.Ticks));
        await Invoke("RefreshDevicesAsync");
        Assert.Equal(0, _controller.ResizeCalls);
        var staged = Field<Dictionary<(string, int), RgbBridge.PendingIdResize>>("_stableZoneResizes");
        Assert.Equal("waiting", Assert.Single(staged.Values).DeviceId);
    }

    [Fact]
    public async Task A_parked_old_connect_success_cannot_clear_new_lifecycle_mode_ownership()
    {
        await Invoke("RefreshDevicesAsync");
        _controller.Connected = false;
        _controller.ParkedConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Invoke("EnsureConnectedAsync");
        Assert.False(old.IsCompleted);
        _bridge.Deactivate();
        _bridge.AwaitShutdown();
        SetField("_active", true);
        var currentModes = new HashSet<int> { 42 };
        SetField("_directModeApplied", currentModes);
        _controller.Generation++;
        _controller.ParkedConnect.SetResult(true);
        await old;
        Assert.Same(currentModes, Field<HashSet<int>>("_directModeApplied"));
        Assert.Equal(1, _controller.Fetches);
    }

    private static RgbDevice ResizeBoard() => new()
    {
        Address = 3, Serial = "synthetic-resizable", Type = 0, LedCount = 4,
        Zones = [new() { LedCount = 2, ZoneType = 1, LedsMax = 120, Flags = RgbZone.FlagConfigurableSize },
            new() { LedCount = 2, ZoneType = 1, LedsMax = 120, Flags = RgbZone.FlagConfigurableSize }],
    };

    private Task Invoke(string method) => (Task)typeof(RgbBridge).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, null)!;
    private T Field<T>(string name) => (T)typeof(RgbBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_bridge)!;
    private void SetField(string name, object value) => typeof(RgbBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_bridge, value);

    public void Dispose()
    {
        _bridge.Dispose();
        _bridge.AwaitShutdown();
        Directory.Delete(_directory, true);
    }

    private sealed class FakeController : IRgbController, IOpenRgbWriteHealthTransport, IOpenRgbRefreshTransport
    {
        public bool Busy;
        public bool BusyOnResize;
        public int Fetches;
        public int DirectCalls;
        public int ResizeCalls;
        public int Generation = 1;
        public TaskCompletionSource<IReadOnlyList<RgbDevice>>? ParkedFetch;
        public TaskCompletionSource<IReadOnlyList<RgbDevice>>? ParkFetchAfterResize;
        public TaskCompletionSource PostResizeFetchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<RgbDevice>? NextDevices;
        public TaskCompletionSource? ParkedCommand;
        public TaskCompletionSource CommandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ParkResize;
        public bool ParkAdmission;
        public bool Connected = true;
        public bool DropConnectionOnDisconnect;
        public TaskCompletionSource<bool>? ParkedConnect;
        public TaskCompletionSource AdmissionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AdmissionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(int Address, int Size)> ResizeTargets = [];
        public TaskCompletionSource<bool>? ParkedSettings;
        public TaskCompletionSource<bool>? ParkedRescan;
        public TaskCompletionSource<bool>? ParkedDisconnect;
        public TaskCompletionSource DisconnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? AfterDisconnect;
        public TaskCompletionSource RedetectCommandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Rescans;
        public int SettingsCalls;
        public bool IsConnected => Connected;
        public int ConnectionGeneration => Generation;
        public event Action? DeviceListChanged { add { } remove { } }
        public event Action<bool>? DetectionStateChanged { add { } remove { } }
        public event Action? DetectionProgress { add { } remove { } }
        public event Action<int, uint, uint>? WriteRejected { add { } remove { } }
        public async Task<bool> TryConnectAsync(CancellationToken ct = default)
        {
            if (ParkedConnect is null) { Connected = true; return true; }
            var connected = await ParkedConnect.Task;
            if (connected) Connected = true;
            return connected;
        }
        public Task DisconnectAsync()
        {
            if (DropConnectionOnDisconnect) Connected = false;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>(new[] { 3, 4 });
        public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default)
        {
            Fetches++;
            if (ResizeCalls > 0 && ParkFetchAfterResize is not null)
            {
                PostResizeFetchStarted.TrySetResult();
                return ParkFetchAfterResize.Task;
            }
            if (ParkedFetch is not null) return ParkedFetch.Task;
            if (Busy) throw new OpenRgbMetadataBusyException(4, Generation);
            if (NextDevices is not null) return Task.FromResult(NextDevices);
            return Task.FromResult<IReadOnlyList<RgbDevice>>(new[]
            {
                new RgbDevice { Index = 0, Address = 3, Name = "DRAM A", Serial = "synthetic-a", Type = 1, LedCount = 2 },
                new RgbDevice { Index = 1, Address = 4, Name = "DRAM B", Serial = "synthetic-b", Type = 1, LedCount = 2 },
            });
        }
        public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default)
        {
            DirectCalls++;
            if (ParkedCommand is not null && !ParkResize) { CommandStarted.TrySetResult(); return ParkedCommand.Task; }
            return Task.CompletedTask;
        }
        public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOffAsync(int address, int count, CancellationToken ct = default) => Task.CompletedTask;
        public Task PushZoneFrameAsync(int address, int zone, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResizeZoneAsync(int address, int zone, int size, CancellationToken ct = default)
        {
            ResizeCalls++;
            ResizeTargets.Add((address, size));
            if (ParkedCommand is not null && ParkResize) { CommandStarted.TrySetResult(); return ParkedCommand.Task; }
            if (BusyOnResize) Busy = true;
            return Task.CompletedTask;
        }
        public bool TryPublishConnection(int generation, Action publication, bool requireConnected = true)
        {
            if (generation != Generation) return false;
            publication();
            return generation == Generation;
        }
        public async Task<bool> TrySetDirectModeAsync(RgbDevice device, int generation, Func<bool> current, CancellationToken ct)
        {
            if (generation != Generation || !current()) return false;
            await SetDirectModeAsync(device, ct);
            return true;
        }
        public async Task<bool> TryResizeZoneAsync(int address, int zone, int size, int generation, Func<bool> current, CancellationToken ct)
        {
            if (ParkAdmission) { AdmissionStarted.TrySetResult(); await AdmissionRelease.Task; }
            if (generation != Generation || !current()) return false;
            await ResizeZoneAsync(address, zone, size, ct);
            return true;
        }
        public Task<bool> RescanAsync(CancellationToken ct = default)
        {
            Rescans++;
            if (ParkedRescan is null) return Task.FromResult(true);
            RedetectCommandStarted.TrySetResult(); return ParkedRescan.Task;
        }
        public Task<bool> SetSettingsAsync(string key, string json, CancellationToken ct = default)
        {
            SettingsCalls++;
            if (ParkedSettings is null) return Task.FromResult(true);
            RedetectCommandStarted.TrySetResult(); return ParkedSettings.Task;
        }
        public Task<bool?> TrySetSettingsAsync(string key, string json, int generation, Func<bool> current, CancellationToken ct) =>
            RunScopedCommand(() => SetSettingsAsync(key, json, ct), generation, current);
        public Task<bool?> TryRescanAsync(int generation, Func<bool> current, CancellationToken ct) =>
            RunScopedCommand(() => RescanAsync(ct), generation, current);
        private async Task<bool?> RunScopedCommand(Func<Task<bool>> command, int generation, Func<bool> current)
        {
            if (ParkAdmission) { AdmissionStarted.TrySetResult(); await AdmissionRelease.Task; }
            if (generation != Generation || !current()) return null;
            return await command();
        }
        public async Task<bool> TryDisconnectAsync(int generation, Func<bool> current, CancellationToken ct)
        {
            DisconnectStarted.TrySetResult();
            if (ParkedDisconnect is not null) await ParkedDisconnect.Task;
            if (generation != Generation || !current()) return false;
            Connected = false;
            AfterDisconnect?.Invoke();
            return true;
        }
        public Task<OpenRgbWriteHealthResult> GetWriteHealthAsync(int address, CancellationToken ct = default) => Task.FromResult(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.Unsupported, default));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
