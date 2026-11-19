using System.Reflection;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Tests.Lighting.Rgb;

public sealed partial class RgbBridgeMetadataBusyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Retired_detector_redetection_failure_cannot_rescan_or_bounce_the_current_daemon(bool rescan, bool fault, bool lifecycle)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        _store.Update(settings => settings.Devices.OpenRgbDetectorExclusions["synthetic-removed-exclusion"] = new());
        var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (rescan) _controller.ParkedRescan = parked;
        else _controller.ParkedSettings = parked;
        await Invoke("RefreshDevicesAsync");
        await _controller.RedetectCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (lifecycle) { _bridge.Deactivate(); _bridge.AwaitShutdown(); SetField("_active", true); }
        else _controller.Generation++;
        if (fault) parked.SetException(new IOException("synthetic retired failure"));
        else parked.SetResult(false);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, bounces);
        Assert.Equal(rescan ? 1 : 0, _controller.Rescans);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("rescan")]
    [InlineData("success")]
    public async Task Current_redetection_preserves_rescan_and_refusal_fallback(string outcome)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        _store.Update(settings => settings.Devices.OpenRgbDetectorExclusions["synthetic-removed-exclusion"] = new());
        if (outcome == "settings") _controller.ParkedSettings = Completed(false);
        if (outcome == "rescan") _controller.ParkedRescan = Completed(false);
        await Invoke("RefreshDevicesAsync");
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(outcome == "success" ? 0 : 1, bounces);
        Assert.Equal(1, _controller.SettingsCalls);
        Assert.Equal(outcome == "settings" ? 0 : 1, _controller.Rescans);
    }

    [Fact]
    public async Task Retired_redetection_waiting_for_command_admission_sends_nothing()
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        _controller.ParkAdmission = true;
        _store.Update(settings => settings.Devices.OpenRgbDetectorExclusions["synthetic-removed-exclusion"] = new());
        await Invoke("RefreshDevicesAsync");
        await _controller.AdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _controller.Generation++;
        _controller.AdmissionRelease.SetResult();
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, _controller.SettingsCalls);
        Assert.Equal(0, _controller.Rescans);
        Assert.Equal(0, bounces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redetection_restart_cannot_start_after_its_disconnect_owner_retires(bool retire)
    {
        var stops = 0;
        var starts = 0;
        _bridge.RedetectOverride = null;
        _bridge.RedetectionProcessStopOverride = () => stops++;
        _bridge.RedetectionProcessStartOverride = () => starts++;
        _controller.ParkedSettings = Completed(false);
        _controller.ParkedDisconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.Update(settings => settings.Devices.OpenRgbDetectorExclusions["synthetic-removed-exclusion"] = new());
        await Invoke("RefreshDevicesAsync");
        await _controller.DisconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, stops);
        if (retire) _controller.Generation++;
        _controller.ParkedDisconnect.SetResult(true);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(retire ? 0 : 1, starts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Deferred_redetection_retains_its_owner_on_complete_or_poll(bool poll, bool retire)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        var owner = typeof(RgbBridge).GetMethod("CaptureRefreshOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, null);
        SetField("_detectionStartedSeen", 1);
        SetField("_detectionActivityTicks", DateTime.UtcNow.Ticks);
        typeof(RgbBridge).GetMethod("Redetect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, ["detector-exclusions", true, owner]);
        Assert.Equal("detector-exclusions", Field<string>("_deferredRedetectReason"));
        if (retire) _controller.Generation++;
        SetField("_detectionStartedSeen", 0);
        SetField("_detectionActivityTicks", 0L);
        if (poll) await Invoke("PollAsync");
        else typeof(RgbBridge).GetMethod("OnDetectionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, [false]);
        if (_bridge.RedetectionTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(retire ? 0 : 1, _controller.SettingsCalls);
        Assert.Equal(retire ? 0 : 1, _controller.Rescans);
        Assert.Equal(0, bounces);
    }

    private static TaskCompletionSource<bool> Completed(bool value)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(value);
        return source;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_explicit_rescan_coalesces_only_current_deferred_detector_changes(bool previousRetired)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        var owner = typeof(RgbBridge).GetMethod("CaptureRefreshOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, null);
        var redetect = typeof(RgbBridge).GetMethod("Redetect", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SetField("_detectionStartedSeen", 1);
        SetField("_detectionActivityTicks", DateTime.UtcNow.Ticks);
        redetect.Invoke(_bridge, ["detector-exclusions", true, owner]);
        if (previousRetired) _controller.Generation++;
        redetect.Invoke(_bridge, ["user-rescan", false, null]);
        typeof(RgbBridge).GetMethod("OnDetectionStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, [false]);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(previousRetired ? 0 : 1, _controller.SettingsCalls);
        Assert.Equal(1, _controller.Rescans);
        Assert.Equal(0, bounces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_redetection_fault_preserves_its_restart_fallback(bool rescan)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        var faulted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        faulted.SetException(new IOException("synthetic current refusal"));
        if (rescan) _controller.ParkedRescan = faulted;
        else _controller.ParkedSettings = faulted;
        StartRedetection(detectorsChanged: true, scoped: true);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, bounces);
        Assert.Equal(rescan ? 1 : 0, _controller.Rescans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnected_redetection_uses_the_current_scoped_or_explicit_fallback(bool scoped)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        _controller.Connected = false;
        StartRedetection(detectorsChanged: false, scoped);
        if (_bridge.RedetectionBounceTask is { } bounce) await bounce.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, bounces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redetection_failure_respects_a_spawn_admitted_while_the_command_was_pending(bool stillPending)
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        _controller.ParkedSettings = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartRedetection(detectorsChanged: true, scoped: true);
        await _controller.RedetectCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var spawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!stillPending) spawn.SetResult();
        SetField("_pendingStart", spawn.Task);
        try
        {
            _controller.ParkedSettings.SetResult(false);
            await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(stillPending ? 0 : 1, bounces);
        }
        finally { spawn.TrySetResult(); } // Fixture shutdown owns and awaits this private task, even after a failed assertion.
    }

    [Fact]
    public async Task Shutdown_after_redetection_admission_suppresses_its_fallback()
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        _controller.ParkedSettings = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartRedetection(detectorsChanged: true, scoped: true);
        await _controller.RedetectCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _bridge.OsShuttingDown = () => true;
        _controller.ParkedSettings.SetResult(false);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, bounces);
    }

    [Fact]
    public async Task Shutdown_during_restart_disconnect_prevents_process_start_and_reconnect()
    {
        var starts = 0;
        _bridge.RedetectOverride = null;
        _bridge.RedetectionProcessStopOverride = () => { };
        _bridge.RedetectionProcessStartOverride = () => starts++;
        _controller.AfterDisconnect = () => _bridge.OsShuttingDown = () => true;
        _controller.ParkedSettings = Completed(false);
        StartRedetection(detectorsChanged: true, scoped: true);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, starts);
        Assert.False(_controller.Connected);
        Assert.Equal(0, _controller.Fetches);
    }

    [Fact]
    public async Task Unreadable_private_detector_config_uses_fallback_without_sending_settings()
    {
        var bounces = 0;
        _bridge.RedetectOverride = null;
        _bridge.BounceOverride = _ => bounces++;
        Directory.CreateDirectory(Path.Combine(_directory, "openrgb-config", "OpenRGB.json"));
        StartRedetection(detectorsChanged: true, scoped: true);
        await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, bounces);
        Assert.Equal(0, _controller.SettingsCalls);
        Assert.Equal(0, _controller.Rescans);
    }

    private void StartRedetection(bool detectorsChanged, bool scoped)
    {
        var owner = scoped ? typeof(RgbBridge).GetMethod("CaptureRefreshOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, null) : null;
        typeof(RgbBridge).GetMethod("Redetect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, ["detector-exclusions", detectorsChanged, owner]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_process_recovery_retires_old_redetection_before_the_replacement_sdk_connects(bool rescan)
    {
        var stops = 0;
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacementStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bridge.RedetectOverride = null;
        _bridge.RedetectionProcessStopOverride = () =>
        {
            if (Interlocked.Increment(ref stops) > 1) throw new IOException("synthetic obsolete process stop");
        };
        _bridge.RedetectionProcessStartOverride = () =>
        {
            // A replacement exists, but Start has not returned to EnsureConnected:
            // its SDK connection cannot have advanced the epoch yet.
            replacementStarted.TrySetResult();
            releaseReplacementStart.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        _controller.DropConnectionOnDisconnect = true;
        var parked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (rescan) _controller.ParkedRescan = parked;
        else _controller.ParkedSettings = parked;
        StartRedetection(detectorsChanged: true, scoped: true);
        await _controller.RedetectCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var oldScope = Field<OpenRgbMetadataRetry>("_metadataRetry").Scope;
        var oldGeneration = _controller.Generation;
        typeof(RgbBridge).GetMethod("BounceSubprocess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, ["synthetic-user-recovery"]);
        try
        {
            await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(_controller.Connected);
            Assert.Equal(oldGeneration, _controller.Generation);
            Console.WriteLine($"held process recovery: SDK generation={oldGeneration}, prior scope={oldScope}, current scope={Field<OpenRgbMetadataRetry>("_metadataRetry").Scope}");
            parked.SetResult(false);
            await _bridge.RedetectionTask!.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, stops); // Only the fresh explicit recovery may stop a daemon.
        }
        finally
        {
            parked.TrySetResult(false);
            releaseReplacementStart.TrySetResult();
            await _bridge.LegacyBounceTask!.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_recovery_preserves_best_effort_disconnect_only_for_its_current_owner(bool retire)
    {
        var starts = 0;
        _bridge.RedetectionProcessStopOverride = () => _controller.Connected = false;
        _bridge.RedetectionProcessStartOverride = () => starts++;
        _controller.ParkedDisconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RgbBridge).GetMethod("BounceSubprocess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_bridge, ["synthetic-user-recovery"]);
        await _controller.DisconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (retire) _controller.Generation++;
        _controller.ParkedDisconnect.SetException(new IOException("synthetic disconnected-session failure"));
        await _bridge.LegacyBounceTask!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(retire ? 0 : 1, starts);
    }

}
