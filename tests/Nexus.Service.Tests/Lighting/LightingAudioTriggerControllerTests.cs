using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class LightingAudioTriggerControllerTests
{
    [Fact]
    public void Changing_one_device_keeps_other_active_detectors_and_built_resources_without_reentering_delay()
    {
        var builds = 0;
        using var controller = new LightingAudioTriggerController(look => { builds++; return Build(look); });
        var settings = LightingAudioTriggerTests.Settings(1);
        controller.Configure(settings);
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 1000);
        controller.Tick(true, 1000, 1000);
        controller.PublishSample(1, 0, 0, 2000);
        controller.Tick(true, 2000, 2000);
        Assert.NotNull(controller.CanvasFor("ram"));
        settings.DeviceOverrides = new() { ["fan"] = new() { Intensity = 90, Enabled = false } };
        controller.Configure(settings);
        controller.Tick(true, 2001, 2001);
        Assert.NotNull(controller.CanvasFor("ram"));
        Assert.Null(controller.CanvasFor("fan"));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Individual_sensitivities_and_disable_select_independent_trigger_canvases_with_shared_effects()
    {
        var builds = 0;
        using var controller = new LightingAudioTriggerController(look => { builds++; return Build(look); });
        var settings = LightingAudioTriggerTests.Settings(0);
        settings.DeviceOverrides = new()
        {
            ["quiet"] = new() { Intensity = 100 }, ["loud"] = new() { Intensity = 0 },
            ["off"] = new() { Enabled = false },
        };
        controller.Configure(settings);
        controller.SetRunning(true);
        controller.PublishSample(0.1f, 0, 0, 1000);
        controller.Tick(true, 1000, 1000);
        Assert.NotNull(controller.CanvasFor("quiet"));
        Assert.Null(controller.CanvasFor("loud"));
        Assert.Null(controller.CanvasFor("off"));
        controller.PublishSample(1, 0, 0, 1200);
        controller.Tick(true, 1200, 1200);
        Assert.NotNull(controller.CanvasFor("loud"));
        Assert.Equal(1, builds);
        controller.SetRunning(false);
        Assert.Null(controller.CanvasFor("quiet"));
    }

    private static IEffect Build(Nexus.Service.Models.Lighting.LightingAudioTriggerLook look)
    {
        Assert.True(StaticColorHex.TryParse(look.Color, out var r, out var g, out var b));
        return new FlatColorEffect(r, g, b);
    }

    [Fact]
    public async Task Quiet_device_reacts_independently_while_insensitive_and_disabled_devices_keep_pins_when_paused()
    {
        using var controller = new LightingAudioTriggerController(Build);
        var pins = new DevicePinTracker();
        pins.Set("quiet", new() { Kind = DevicePinKinds.Static, Color = "#0000ff" });
        pins.Set("loud", new() { Kind = DevicePinKinds.Static, Color = "#0000ff" });
        using var engine = new LightingEngine { FrameIntervalMs = 5, AudioTrigger = controller, Pins = pins };
        var quiet = new DeviceFrame(0, "quiet", 1, 450, 250, 100, 100);
        var loud = new DeviceFrame(1, "loud", 1, 450, 250, 100, 100);
        engine.UpdateDevices([quiet, loud]);
        var settings = LightingAudioTriggerTests.Settings(0);
        settings.DeviceOverrides = new() { ["quiet"] = new() { Intensity = 100 }, ["loud"] = new() { Intensity = 0 } };
        controller.Configure(settings);
        controller.SetRunning(true);
        engine.FramePublished += () => controller.PublishSample(0.1f, 0, 0, Environment.TickCount64);
        engine.SetEffect(new FlatColorEffect(0, 255, 0));
        await WaitFor(engine, quiet, [255, 0, 0]);
        await WaitFor(engine, loud, [0, 0, 255]);
        Assert.Null(controller.Canvas); // Only a device-specific sensitivity is active.
        Assert.True(controller.AnyCanvas);
        engine.SetPaused(true);
        settings.DeviceOverrides["quiet"].Enabled = false;
        controller.Configure(settings);
        await WaitFor(engine, quiet, [0, 0, 255]);
        Assert.True(engine.Paused);
        engine.SetBlackout(true);
        await WaitFor(engine, loud, [0, 0, 0]);
    }

    [Fact]
    public void Latest_samples_switch_colours_expire_and_disable_requests_paused_repaint()
    {
        using var controller = new LightingAudioTriggerController(Build);
        var settings = LightingAudioTriggerTests.Settings(0);
        controller.Configure(settings);
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 1000);
        Assert.True(controller.Tick(true, 1000, 1000));
        Assert.Equal(((byte)255, (byte)0, (byte)0), controller.Canvas!.GetPixel(80, 40));
        controller.PublishSample(1, 1, 1, 1200);
        controller.Tick(true, 1200, 1200);
        Assert.Equal(((byte)0, (byte)0, (byte)255), controller.Canvas!.GetPixel(80, 40));
        settings.Enabled = false;
        controller.Configure(settings);
        Assert.True(controller.Tick(true, 1201, 1201));
        Assert.Null(controller.Canvas);
        Assert.False(controller.CaptureWanted);
        settings.Enabled = true;
        controller.Configure(settings);
        controller.PublishSample(1, 0, 0, 1300);
        controller.Tick(true, 1300, 1300);
        Assert.True(controller.Tick(true, 1651, 1651));
        Assert.Null(controller.Canvas);
    }

    [Fact]
    public void Equivalent_record_reload_preserves_pending_delay_and_source_change_restarts_it()
    {
        using var controller = new LightingAudioTriggerController(Build);
        controller.Configure(LightingAudioTriggerTests.Settings());
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 1000);
        controller.Tick(true, 1000, 1000);
        controller.Configure(LightingAudioTriggerTests.Settings());
        controller.PublishSample(1, 0, 0, 2000);
        controller.Tick(true, 2000, 2000);
        Assert.NotNull(controller.Canvas);
        controller.ResetDetection();
        controller.PublishSample(1, 0, 0, 2001);
        controller.Tick(true, 2001, 2001);
        Assert.Null(controller.Canvas);
        controller.SetRunning(false);
        Assert.False(controller.CaptureWanted);
    }

    [Fact]
    public void Failed_builds_are_bounded_and_disposed_controllers_reject_new_demand()
    {
        var builds = 0;
        using var controller = new LightingAudioTriggerController(_ => { builds++; throw new InvalidOperationException("test failure"); });
        controller.Configure(LightingAudioTriggerTests.Settings(0));
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 0);
        controller.Tick(true, 0, 0);
        controller.Tick(true, 20, 20);
        Assert.Equal(1, builds);
        Assert.Null(controller.Canvas);
        controller.Dispose();
        controller.Configure(LightingAudioTriggerTests.Settings(0));
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 100);
        Assert.False(controller.Tick(true, 100, 100));
        Assert.False(controller.Requested);
        Assert.False(controller.CaptureWanted);
    }

    [Fact]
    public void Pending_shader_holds_last_finished_colour_then_paints_when_ready()
    {
        using var gpu = new GpuContext(160, 90);
        var ready = false;
        using var controller = new LightingAudioTriggerController(look => look.Effect is null ? Build(look) : new ShaderEffect("plasma", gpu, ShaderLibrary.Plasma)
        {
            TestRender = (canvas, _) => { if (!ready) return false; canvas.Fill(0, 0, 255); return true; },
        });
        var settings = LightingAudioTriggerTests.Settings(0);
        settings.Looks[1] = new() { Mode = "animate", Effect = "plasma" };
        controller.Configure(settings);
        controller.SetRunning(true);
        controller.PublishSample(1, 0, 0, 0);
        controller.Tick(true, 0, 0);
        controller.PublishSample(1, 1, 1, 200);
        controller.Tick(true, 200, 200);
        Assert.Equal(((byte)255, (byte)0, (byte)0), controller.Canvas!.GetPixel(80, 40));
        ready = true;
        controller.Tick(true, 220, 220);
        Assert.Equal(((byte)0, (byte)0, (byte)255), controller.Canvas!.GetPixel(80, 40));
    }

    [Fact]
    public async Task Trigger_outranks_pins_but_preserves_LED_colour_locks()
    {
        using var controller = new LightingAudioTriggerController(Build);
        var pins = new DevicePinTracker();
        pins.Set("audio-led", new() { Kind = DevicePinKinds.Static, Color = "#0000ff" });
        var locks = new LedColorLockTracker();
        locks.Set("audio-led", [0], "#ffff00");
        using var engine = new LightingEngine { FrameIntervalMs = 5, AudioTrigger = controller, Pins = pins, LedColorLocks = locks };
        var device = new DeviceFrame(0, "audio-led", 2, 450, 250, 100, 100);
        engine.UpdateDevices([device]);
        controller.Configure(LightingAudioTriggerTests.Settings(0));
        controller.SetRunning(true);
        engine.FramePublished += () => controller.PublishSample(1, 0, 0, Environment.TickCount64);
        engine.SetEffect(new FlatColorEffect(0, 255, 0));
        await WaitFor(engine, device, [255, 255, 0, 255, 0, 0]);
        Assert.Equal("#0000ff", pins.Snapshot()["audio-led"].Color);
    }

    [Fact]
    public async Task Paused_engine_restores_base_on_disable_and_Off_broadcasts_no_trigger_pixels()
    {
        using var controller = new LightingAudioTriggerController(Build);
        using var engine = new LightingEngine { FrameIntervalMs = 5, AudioTrigger = controller };
        var device = new DeviceFrame(0, "audio-led", 1, 450, 250, 100, 100);
        engine.UpdateDevices([device]);
        var settings = LightingAudioTriggerTests.Settings(0);
        controller.Configure(settings);
        controller.SetRunning(true);
        engine.FramePublished += () => controller.PublishSample(1, 0, 0, Environment.TickCount64);
        engine.SetEffect(new FlatColorEffect(0, 255, 0));
        await WaitFor(engine, device, [255, 0, 0]);
        engine.SetPaused(true);
        settings.Enabled = false;
        controller.Configure(settings);
        await WaitFor(engine, device, [0, 255, 0]);
        Assert.True(engine.Paused);
        Assert.Equal("flat", engine.CurrentEffectName);
        settings.Enabled = true;
        controller.Configure(settings);
        await WaitFor(engine, device, [255, 0, 0]);
        byte[]? final = null;
        engine.OnFrame += bytes => final = bytes.ToArray();
        engine.Stop();
        Assert.False(controller.CaptureWanted);
        Assert.NotNull(final);
        Assert.Equal(new byte[] { 0, 0, 0 }, final![5..8]);
    }

    [Fact]
    public async Task Studio_and_mapper_overrides_outrank_trigger_and_base_returns_after_silence()
    {
        using var controller = new LightingAudioTriggerController(Build);
        using var engine = new LightingEngine { FrameIntervalMs = 5, AudioTrigger = controller };
        var device = new DeviceFrame(0, "audio-led", 1, 450, 250, 100, 100);
        engine.UpdateDevices([device]);
        controller.Configure(LightingAudioTriggerTests.Settings(0));
        controller.SetRunning(true);
        var feed = true;
        engine.FramePublished += () => { if (feed) controller.PublishSample(1, 0, 0, Environment.TickCount64); };
        engine.SetEffect(new FlatColorEffect(0, 255, 0));
        await WaitFor(engine, device, [255, 0, 0]);
        Assert.False(engine.CanRenderAhead([device.Id]));
        using var studio = new FlatColorEffect(0, 0, 255);
        engine.SetStudioOverlay(studio);
        await WaitFor(engine, device, [0, 0, 255]);
        device.HighlightLeds = [0];
        await WaitFor(engine, device, [255, 255, 255]);
        device.HighlightLeds = null;
        engine.SetStudioOverlay(null);
        await WaitFor(engine, device, [255, 0, 0]);
        feed = false;
        await WaitFor(engine, device, [0, 255, 0]);
    }

    private static async Task WaitFor(LightingEngine engine, DeviceFrame device, byte[] expected)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe() { if (device.LedBytes.SequenceEqual(expected)) done.TrySetResult(); }
        engine.FramePublished += Observe;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { engine.FramePublished -= Observe; }
    }
}
