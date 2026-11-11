using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>Narrow, atomic controls for existing trigger selections; never creates an empty trigger.</summary>
internal static class AudioTriggerTargets
{
    internal const int MaximumTargets = 1024;
    internal static bool ValidPatch(AudioTriggerTargetsPatch? patch) => patch is not null
        && (patch.Enabled.HasValue || patch.Intensity.HasValue)
        && (patch.ExpectedEpoch is null || (patch.ExpectedEpoch.Length == 32 && patch.ExpectedEpoch.All(char.IsAsciiHexDigit)))
        && (patch.Intensity is not { } intensity || (double.IsFinite(intensity) && intensity is >= 0 and <= 100))
        && (patch.TargetIds is null || (patch.TargetIds.Count is > 0 and <= MaximumTargets
            && patch.TargetIds.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 300)
            && patch.TargetIds.Distinct(StringComparer.Ordinal).Count() == patch.TargetIds.Count));

    internal static AudioTriggerTargetsResponse Snapshot(NexusSettings settings, IReadOnlyList<LightingDevice> devices)
    {
        var targets = new List<AudioTriggerTarget>();
        foreach (var panel in settings.PanelDevices.Values.OrderBy(panel => panel.Id, StringComparer.Ordinal))
        {
            if (panel.BackgroundAudioTrigger is not { Backgrounds.Count: > 0 } trigger || !PanelAudioTriggerPolicy.IsValid(trigger)) continue;
            targets.Add(new() { Id = "background:" + panel.Id, Kind = "background", Name = panel.DisplayName,
                Enabled = trigger.Enabled, Intensity = trigger.Intensity });
        }
        if (settings.Lighting.AudioTrigger is { Looks.Count: > 0 } lighting && LightingAudioTriggerPolicy.IsValid(lighting))
        {
            foreach (var device in devices.Where(EligibleDevice).DistinctBy(device => device.Id).OrderBy(device => device.Id, StringComparer.Ordinal).Take(512))
            {
                var control = lighting.DeviceOverrides?.GetValueOrDefault(device.Id);
                targets.Add(new() { Id = "lighting:" + device.Id, Kind = "lighting",
                    Name = settings.Lighting.DeviceNames?.GetValueOrDefault(device.Id) ?? device.Name,
                    Enabled = lighting.Enabled && control?.Enabled != false, Intensity = control?.Intensity ?? lighting.Intensity });
            }
        }
        return new() { Epoch = settings.AudioTriggerControlEpoch, Targets = targets.Take(MaximumTargets).ToList() };
    }

    private static bool EligibleDevice(LightingDevice device) => device.Controlled && device.DevicePageReason is null
        && device.LedCount > 0 && device.Id.Length is > 0 and <= 256;

    internal static bool Apply(NexusSettings settings, IReadOnlyList<LightingDevice> devices, AudioTriggerTargetsPatch patch)
    {
        if (!ValidPatch(patch)) return false;
        if (patch.ExpectedEpoch is { } epoch && epoch != settings.AudioTriggerControlEpoch) return false;
        var snapshot = Snapshot(settings, devices);
        var requested = patch.TargetIds?.ToHashSet(StringComparer.Ordinal)
            ?? snapshot.Targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        if (!requested.IsSubsetOf(snapshot.Targets.Select(target => target.Id))) return false;
        var selected = snapshot.Targets.Where(target => requested.Contains(target.Id)).ToList();
        var existingControls = settings.Lighting.AudioTrigger?.DeviceOverrides;
        var controlKeys = existingControls?.Keys.ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        foreach (var target in selected.Where(target => target.Kind == "lighting")) controlKeys.Add(target.Id[9..]);
        if (patch.Enabled == true && settings.Lighting.AudioTrigger?.Enabled == false)
            foreach (var target in snapshot.Targets.Where(target => target.Kind == "lighting")) controlKeys.Add(target.Id[9..]);
        if (controlKeys.Count > 512) return false;
        var selectedLights = selected.Where(target => target.Kind == "lighting").ToList();
        if (selectedLights.Count == 0) { ApplyBackgrounds(); return true; }
        var lighting = LightingAudioTriggerPolicy.Clone(settings.Lighting.AudioTrigger)!;
        var controls = lighting.DeviceOverrides ?? new(StringComparer.Ordinal);
        var lightingTargets = snapshot.Targets.Where(target => target.Kind == "lighting").ToList();
        if (patch.TargetIds is null)
        {
            if (patch.Enabled is { } enabled) lighting.Enabled = enabled;
            if (patch.Intensity is { } intensity) lighting.Intensity = intensity;
        }
        else if (patch.Enabled == true && !lighting.Enabled)
        {
            // Turning one card on cannot enable every card that used to follow the disabled master.
            lighting.Enabled = true;
            foreach (var target in lightingTargets)
            {
                var id = target.Id[9..];
                controls[id] = new() { Enabled = false, Intensity = controls.GetValueOrDefault(id)?.Intensity };
            }
        }
        foreach (var target in selectedLights)
        {
            var id = target.Id[9..];
            var previous = controls.GetValueOrDefault(id);
            controls[id] = new()
            {
                Enabled = patch.Enabled.HasValue ? (patch.TargetIds is null ? null : patch.Enabled) : previous?.Enabled,
                Intensity = patch.Intensity.HasValue ? (patch.TargetIds is null ? null : patch.Intensity) : previous?.Intensity,
            };
            if (controls[id].Enabled is null && controls[id].Intensity is null) controls.Remove(id);
        }
        lighting.DeviceOverrides = controls.Count == 0 ? null : controls;
        if (patch.Enabled == false && lightingTargets.All(target => controls.GetValueOrDefault(target.Id[9..])?.Enabled == false))
            lighting.Enabled = false;
        if (!LightingAudioTriggerPolicy.IsValid(lighting)) return false;
        settings.Lighting.AudioTrigger = lighting;
        ApplyBackgrounds();
        LightingPresetLooks.CaptureIntoActive(settings);
        return true;

        void ApplyBackgrounds()
        {
            foreach (var target in selected.Where(target => target.Kind == "background"))
            {
                var panel = settings.PanelDevices[target.Id[11..]];
                var trigger = PanelAudioTriggerPolicy.Clone(panel.BackgroundAudioTrigger)!;
                if (patch.Enabled is { } enabled) trigger.Enabled = enabled;
                if (patch.Intensity is { } intensity) trigger.Intensity = intensity;
                panel.BackgroundAudioTrigger = trigger;
            }
        }
    }
}
