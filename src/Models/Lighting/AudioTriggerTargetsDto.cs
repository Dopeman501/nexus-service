namespace Nexus.Service.Models.Lighting;

public sealed class AudioTriggerTarget
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public double Intensity { get; set; }
}

public sealed class AudioTriggerTargetsResponse
{
    public string Epoch { get; set; } = "";
    public List<AudioTriggerTarget> Targets { get; set; } = new();
}

public sealed class AudioTriggerTargetsPatch
{
    public string? ExpectedEpoch { get; set; }
    public List<string>? TargetIds { get; set; }
    public bool? Enabled { get; set; }
    public double? Intensity { get; set; }
}
