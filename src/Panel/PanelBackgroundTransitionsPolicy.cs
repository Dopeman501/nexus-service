using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Exact bounded shared selection; invalid imports are omitted independently.</summary>
internal static class PanelBackgroundTransitionsPolicy
{
    internal const int MaximumEffects = 226;

    internal static bool IsCustomEffect(string? effect) => effect is { Length: > 0 and <= 128 }
        && effect is not ("default" or "none") && PanelBackgroundTransitionCatalogue.Effects.Contains(effect);

    internal static bool IsValid(PanelBackgroundTransitionsDto? settings) => settings is null
        || (settings.DurationMs is >= 80 and <= 5000
            && settings.Effects is { Count: <= MaximumEffects } effects
            && (!settings.Enabled || effects.Count > 0)
            && effects.All(IsCustomEffect)
            && effects.Distinct(StringComparer.Ordinal).Count() == effects.Count);

    internal static PanelBackgroundTransitionsDto? Clone(PanelBackgroundTransitionsDto? settings) =>
        settings is null || !IsValid(settings) ? null : new()
        {
            Enabled = settings.Enabled, Effects = new List<string>(settings.Effects), DurationMs = settings.DurationMs,
        };
}
