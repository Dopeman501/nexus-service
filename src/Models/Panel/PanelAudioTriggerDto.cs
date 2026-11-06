using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>Independent audio-triggered background selection; the ordinary background stays unchanged.</summary>
public sealed class PanelAudioTriggerDto
{
    public bool Enabled { get; set; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public double Intensity { get; set; } = 50;
    /// <summary>Seconds of sustained audio or silence before entering or leaving the selection.</summary>
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public double SwitchDelay { get; set; } = 1;
    public bool Shuffle { get; set; }
    public List<D215PanelBackgroundDto>? Backgrounds { get; set; } = new();
}
