namespace Nexus.Service.Lighting.Rgb;

/// <summary>A negotiated cached-metadata snapshot is temporarily unavailable; the SDK stream remains usable.</summary>
public sealed class OpenRgbMetadataBusyException(int controllerAddress, int connectionGeneration)
    : Exception("OpenRGB controller metadata is temporarily busy")
{
    public int ControllerAddress { get; } = controllerAddress;
    public int ConnectionGeneration { get; } = connectionGeneration;
}
