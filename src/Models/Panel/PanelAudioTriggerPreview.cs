using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>Ordered, temporary editor override; never part of a panel record or profile.</summary>
public sealed record PanelAudioTriggerPreviewRequest
{
    [JsonRequired]
    public string? SessionId { get; init; }
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public long Sequence { get; init; }
    [JsonRequired]
    public D215PanelBackgroundDto? Background { get; init; }
}

public sealed record PanelAudioTriggerPreviewFrame
{
    public required string DeviceId { get; init; }
    public required string SessionId { get; init; }
    public long Sequence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public D215PanelBackgroundDto? Background { get; init; }
    public long ExpiresAtUnixMs { get; init; }
    public long Revision { get; init; }
}
