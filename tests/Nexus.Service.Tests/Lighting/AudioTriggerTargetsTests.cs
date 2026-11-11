using Nexus.Service.Lighting;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public sealed class AudioTriggerTargetsTests
{
    [Fact]
    public void Receiving_budget_is_constant_size_shared_and_renews_after_one_monotonic_second()
    {
        long now = 1;
        var budget = new Nexus.Service.Routes.AudioTriggerRequestBudget(() => now);
        for (var index = 0; index < Nexus.Service.Routes.AudioTriggerRequestBudget.MaximumPerSecond; index++) Assert.True(budget.TryAccept());
        Assert.False(budget.TryAccept());
        now = 1000;
        Assert.False(budget.TryAccept());
        now = 1001;
        Assert.True(budget.TryAccept());
    }

    [Fact]
    public void Epoch_guard_rejects_late_controls_without_touching_new_profile_settings()
    {
        var settings = Settings();
        var old = AudioTriggerTargets.Snapshot(settings, Devices).Epoch;
        settings.AudioTriggerControlEpoch = Guid.NewGuid().ToString("N");
        Assert.False(AudioTriggerTargets.Apply(settings, Devices, new() { ExpectedEpoch = old, Enabled = false }));
        Assert.True(settings.PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
    }

    [Fact]
    public void Override_capacity_failure_is_atomic_and_invalid_overrides_are_rejected()
    {
        var settings = Settings();
        settings.Lighting.AudioTrigger!.Enabled = false;
        settings.Lighting.AudioTrigger.DeviceOverrides = Enumerable.Range(0, 512).ToDictionary(index => "old" + index,
            _ => new LightingAudioTriggerDeviceOverride { Enabled = false });
        Assert.False(AudioTriggerTargets.Apply(settings, Devices, new() { TargetIds = ["background:panel", "lighting:fan"], Enabled = true }));
        Assert.True(settings.PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
        Assert.False(settings.Lighting.AudioTrigger.Enabled);
        settings.Lighting.AudioTrigger.DeviceOverrides["extra"] = new() { Intensity = double.NaN };
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings.Lighting.AudioTrigger));
        settings.Lighting.AudioTrigger.DeviceOverrides = new() { [" "] = new() };
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings.Lighting.AudioTrigger));
        settings.Lighting.AudioTrigger.DeviceOverrides = new() { ["fan"] = new() { Intensity = -1 } };
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings.Lighting.AudioTrigger));
    }

    private static NexusSettings Settings() => new()
    {
        PanelDevices = new()
        {
            ["panel"] = new() { Id = "panel", DisplayName = "Y70", BackgroundAudioTrigger = new()
                { Enabled = true, Intensity = 70, Backgrounds = [new() { Mode = "solid", Color = "#ff0000" }] } },
            ["empty"] = new() { Id = "empty", BackgroundAudioTrigger = new() },
        },
        Lighting = new() { AudioTrigger = LightingAudioTriggerTests.Settings(0) },
    };
    private static readonly LightingDevice[] Devices =
    [
        new() { Id = "fan", Name = "Fan", LedCount = 10 },
        new() { Id = "ram", Name = "RAM", LedCount = 8 },
        new() { Id = "firmware", Name = "Firmware", LedCount = 4, DevicePageReason = "firmware" },
    ];

    [Fact]
    public void Lists_configured_targets_even_when_disabled_and_omits_empty_or_uncontrolled_targets()
    {
        var settings = Settings();
        settings.PanelDevices["panel"].BackgroundAudioTrigger!.Enabled = false;
        var targets = AudioTriggerTargets.Snapshot(settings, Devices).Targets;
        Assert.Equal(new[] { "background:panel", "lighting:fan", "lighting:ram" }, targets.Select(t => t.Id));
        Assert.False(targets[0].Enabled);
    }

    [Fact]
    public void Narrow_changes_preserve_selections_and_other_targets_and_global_changes_reach_both_kinds()
    {
        var settings = Settings();
        Assert.True(AudioTriggerTargets.Apply(settings, Devices, new() { TargetIds = ["background:panel"], Intensity = 25 }));
        Assert.Equal(25, settings.PanelDevices["panel"].BackgroundAudioTrigger!.Intensity);
        Assert.Equal(2, settings.Lighting.AudioTrigger!.Looks.Count);
        Assert.Equal(50, settings.Lighting.AudioTrigger.Intensity);
        Assert.True(AudioTriggerTargets.Apply(settings, Devices, new() { Enabled = false, Intensity = 40 }));
        Assert.All(AudioTriggerTargets.Snapshot(settings, Devices).Targets, t => { Assert.False(t.Enabled); Assert.Equal(40, t.Intensity); });
        Assert.Single(settings.PanelDevices["panel"].BackgroundAudioTrigger!.Backgrounds!);
        Assert.Empty(settings.PanelDevices["empty"].BackgroundAudioTrigger!.Backgrounds!);
    }

    [Fact]
    public void Enabling_one_light_while_master_is_off_preserves_other_lights_off()
    {
        var settings = Settings();
        settings.Lighting.AudioTrigger!.Enabled = false;
        Assert.True(AudioTriggerTargets.Apply(settings, Devices, new() { TargetIds = ["lighting:fan"], Enabled = true, Intensity = 91 }));
        var targets = AudioTriggerTargets.Snapshot(settings, Devices).Targets;
        Assert.True(targets.Single(t => t.Id == "lighting:fan").Enabled);
        Assert.False(targets.Single(t => t.Id == "lighting:ram").Enabled);
        Assert.Equal(91, targets.Single(t => t.Id == "lighting:fan").Intensity);
        Assert.Equal(50, targets.Single(t => t.Id == "lighting:ram").Intensity);
        settings.Lighting.AudioTrigger.Enabled = false;
        Assert.All(AudioTriggerTargets.Snapshot(settings, Devices).Targets.Where(t => t.Kind == "lighting"), t => Assert.False(t.Enabled));
    }

    [Fact]
    public void Unknown_target_rejects_entire_batch_and_clones_keep_independent_device_controls()
    {
        var settings = Settings();
        Assert.False(AudioTriggerTargets.Apply(settings, Devices, new() { TargetIds = ["background:panel", "lighting:missing"], Enabled = false }));
        Assert.True(settings.PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
        Assert.True(AudioTriggerTargets.Apply(settings, Devices, new() { TargetIds = ["lighting:fan"], Intensity = 12 }));
        var clone = LightingAudioTriggerPolicy.Clone(settings.Lighting.AudioTrigger)!;
        clone.DeviceOverrides!["fan"].Intensity = 20;
        Assert.Equal(12, settings.Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity);
    }
}
