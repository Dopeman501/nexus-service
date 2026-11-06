using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

public sealed class D215WidgetDto
{
    public bool Enabled { get; set; }

    /// <summary>Center coordinates as fractions of the canvas width and height.</summary>
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public double X { get; set; }
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public double Y { get; set; }

    /// <summary>Uniform fraction of both canvas dimensions, preserving the full-canvas aspect ratio.</summary>
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public double Scale { get; set; }
}
