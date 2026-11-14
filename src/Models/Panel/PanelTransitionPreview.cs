using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>One explicit audition; it never alters panel media or persisted preferences.</summary>
public sealed record PanelTransitionPreviewSelection
{
    [JsonRequired]
    public string? PreviewId { get; init; }
    [JsonRequired]
    public string? Effect { get; init; }
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public int DurationMs { get; init; }
}

public sealed record PanelTransitionPreviewRequest
{
    [JsonRequired]
    public string? SessionId { get; init; }
    [JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)]
    public long Sequence { get; init; }
    [JsonRequired]
    public PanelTransitionPreviewSelection? Preview { get; init; }
}

public sealed record PanelTransitionPreviewFrame
{
    public required string DeviceId { get; init; }
    public required string SessionId { get; init; }
    public long Sequence { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public PanelTransitionPreviewSelection? Preview { get; init; }
    public long StartedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
    public long Revision { get; init; }
}
