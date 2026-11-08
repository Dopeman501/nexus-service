using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record D215RestartRequest;

public sealed record D215RestartResponse(string Status);
