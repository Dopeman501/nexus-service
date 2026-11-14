using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Validates exact supported effects before mutation and drops invalid imported preferences.</summary>
internal static class PanelBackgroundTransitionPolicy
{
    internal static bool IsValid(PanelBackgroundTransitionDto? transition) => transition is null
        || (transition.DurationMs is >= 80 and <= 5000
            && PanelBackgroundTransitionCatalogue.Effects.Contains(transition.Effect));

    internal static PanelBackgroundTransitionDto? Clone(PanelBackgroundTransitionDto? transition) =>
        transition is null || !IsValid(transition) ? null : new()
        {
            Effect = transition.Effect,
            DurationMs = transition.DurationMs,
        };
}
