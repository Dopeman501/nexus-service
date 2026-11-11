using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Lighting;

public sealed class LightingAudioTriggerSettings
{
    public bool Enabled { get; set; }
    public double Intensity { get; set; } = 50;
    public double SwitchDelay { get; set; } = 1;
    public bool Shuffle { get; set; }
    public List<LightingAudioTriggerLook> Looks { get; set; } = new();
    /// <summary>Per-lighting-card controls; absent fields inherit the master settings.</summary>
    public Dictionary<string, LightingAudioTriggerDeviceOverride>? DeviceOverrides { get; set; }
}

public sealed class LightingAudioTriggerDeviceOverride
{
    public bool? Enabled { get; set; }
    public double? Intensity { get; set; }
}

public sealed class LightingAudioTriggerLook
{
    public string Mode { get; set; } = "static";
    public string? Color { get; set; }
    public string? Effect { get; set; }
    public AnimateEffectState? State { get; set; }
    public string? MediaId { get; set; }
}
