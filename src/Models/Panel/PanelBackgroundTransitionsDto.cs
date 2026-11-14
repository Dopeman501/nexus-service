using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>One ordered transition selection shared by Slideshow and Audio Trigger.</summary>
public sealed class PanelBackgroundTransitionsDto
{
    public bool Enabled { get; set; }
    public List<string> Effects { get; set; } = [];
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int DurationMs { get; set; } = 800;
}
