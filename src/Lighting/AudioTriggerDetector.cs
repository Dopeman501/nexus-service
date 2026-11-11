using System;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Lighting;

internal readonly record struct AudioTriggerSample(float Level, float Bass, float Beat)
{
    internal bool Valid => float.IsFinite(Level) && Level is >= 0 and <= 1
        && float.IsFinite(Bass) && Bass is >= 0 and <= 1 && float.IsFinite(Beat) && Beat is >= 0 and <= 1;
}

internal readonly record struct AudioTriggerState(int? Index, long? PlayingSince, long? QuietSince, long LastAttackAt, float PreviousPeak);

/// <summary>Pure monotonic-time transitions matching the panel Audio Trigger thresholds.</summary>
internal static class AudioTriggerDetector
{
    internal const int StaleMs = 350;
    internal static AudioTriggerState Initial => new(null, null, null, long.MinValue, 0);

    internal static AudioTriggerState Advance(AudioTriggerState state, LightingAudioTriggerSettings settings,
        AudioTriggerSample? sample, long now, Func<double>? random = null, double? intensity = null)
    {
        var count = settings.Looks.Count;
        if (!settings.Enabled || count == 0) return Initial;
        var sensitivity = (intensity ?? settings.Intensity) / 100;
        var playing = sample is { Valid: true } valid && valid.Level >= 0.55 - sensitivity * 0.535;
        var peak = playing ? Math.Max(sample!.Value.Bass, sample.Value.Beat) : 0;
        var delay = settings.SwitchDelay * 1000;
        if (!playing)
        {
            var quietSince = state.QuietSince ?? now;
            return state with { PlayingSince = null, QuietSince = quietSince, PreviousPeak = 0,
                Index = now - quietSince >= delay ? null : state.Index };
        }
        var playingSince = state.PlayingSince ?? now;
        if (state.Index is null)
        {
            var entered = now - playingSince >= delay;
            return state with { PlayingSince = playingSince, QuietSince = null, PreviousPeak = peak,
                Index = entered ? Next(null, count, settings.Shuffle, random) : null,
                LastAttackAt = entered ? now : state.LastAttackAt };
        }
        var threshold = 0.65 - sensitivity * 0.6;
        var attack = peak >= threshold && (state.PreviousPeak < threshold * 0.55 || peak - state.PreviousPeak >= 0.22 - sensitivity * 0.19);
        var advance = count > 1 && attack && now - state.LastAttackAt >= 120;
        return state with { PlayingSince = playingSince, QuietSince = null, PreviousPeak = peak,
            Index = advance ? Next(state.Index, count, settings.Shuffle, random) : state.Index,
            LastAttackAt = advance ? now : state.LastAttackAt };
    }

    private static int Next(int? current, int count, bool shuffle, Func<double>? random)
    {
        if (!shuffle) return current is null ? 0 : (current.Value + 1) % count;
        var pick = Math.Clamp((random ?? Random.Shared.NextDouble)(), 0, 0.999999);
        return current is null ? (int)(pick * count) : (current.Value + 1 + (int)(pick * (count - 1))) % count;
    }
}
