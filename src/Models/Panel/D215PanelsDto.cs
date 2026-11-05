using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

public sealed class D215PanelsDto
{
    public bool Enabled { get; set; }
    public List<D215PanelDto> Items { get; set; } = new();
}

public sealed class D215PanelDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>Center coordinates as fractions of the canvas width and height.</summary>
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public double X { get; set; }
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public double Y { get; set; }

    /// <summary>Diameter as a fraction of the canvas's shorter side; the renderer keeps the circle inside the canvas.</summary>
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public double Diameter { get; set; }
    public Dictionary<string, JsonElement>? Config { get; set; }
}
