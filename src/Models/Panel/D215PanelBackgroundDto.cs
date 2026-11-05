using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>Independent circle background. Media assets remain in the parent device's library.</summary>
public sealed class D215PanelBackgroundDto
{
    public string? Mode { get; set; }
    public string? Color { get; set; }
    public string? ColorLight { get; set; }
    public string? Effect { get; set; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? Template { get; set; }
    /// <summary>Local shader controls; editing these never changes a universal lighting preset.</summary>
    public JsonElement? EffectState { get; set; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public double? Opacity { get; set; }
    public string? MediaId { get; set; }
    public string? MediaType { get; set; }
    public bool? MediaAlpha { get; set; }
    public bool? Slideshow { get; set; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? Interval { get; set; }
    public bool? Shuffle { get; set; }
    public bool? FinishVideos { get; set; }
    public List<string>? MediaOrder { get; set; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public double? Frost { get; set; }
}
