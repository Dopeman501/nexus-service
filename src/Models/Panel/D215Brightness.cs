using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record D215BrightnessRequest([property: JsonNumberHandling(JsonNumberHandling.Strict)] int? Value);

public sealed record D215BrightnessResponse(string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Value);
