using System;

namespace Nexus.Service.Lighting;

/// <summary>Immutable prediction; the caller commits it only after publishing the complete selected frame.</summary>
internal readonly record struct AudioTriggerMediaClock(int Frame, double LastAdvance, bool Started)
{
    internal AudioTriggerMediaClock At(double tick, int count, int fps)
    {
        if (!Started) return new(0, tick, true);
        if (count <= 1 || fps <= 0) return this;
        var duration = 1000.0 / fps;
        if (tick - LastAdvance < duration) return this;
        var steps = (int)Math.Min(count, Math.Floor((tick - LastAdvance) / duration));
        return new((Frame + steps) % count, LastAdvance + steps * duration, true);
    }
}
