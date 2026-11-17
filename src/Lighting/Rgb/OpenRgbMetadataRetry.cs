namespace Nexus.Service.Lighting.Rgb;

/// <summary>One deferred metadata refresh, scoped to the bridge lifecycle and daemon generation. Caller holds the bridge lock.</summary>
internal sealed class OpenRgbMetadataRetry
{
    public long Scope { get; private set; }
    private int? _generation;

    public void Reset()
    {
        Scope++;
        _generation = null;
    }

    public void Defer(long scope, int generation, int currentGeneration)
    {
        if (scope == Scope && generation == currentGeneration)
            _generation = generation;
    }

    public bool NeedsRetry(int generation)
    {
        if (_generation != generation)
        {
            _generation = null;
            return false;
        }
        return true;
    }

    public void Complete(long scope)
    {
        if (scope == Scope) _generation = null;
    }
}
