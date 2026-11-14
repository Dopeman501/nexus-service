using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Validates independent beat timing and preserves the old effective timing on the first main edit.</summary>
internal static class PanelAudioTriggerBeatTransitionPolicy
{
    internal static bool IsValid(PanelAudioTriggerBeatTransitionDto? settings) => settings is null
        || settings.DurationMs is >= 80 and <= 5000;

    internal static PanelAudioTriggerBeatTransitionDto? Clone(PanelAudioTriggerBeatTransitionDto? settings) =>
        settings is null || !IsValid(settings) ? null : new()
        {
            Enabled = settings.Enabled, DurationMs = settings.DurationMs,
        };

    /// <summary>Resolve without changing stored state. Legacy shared migration is custom media first, then audio.</summary>
    internal static PanelAudioTriggerBeatTransitionDto FromMain(IPanelPersonalization source)
    {
        if (source.BackgroundTransitions is { } shared && PanelBackgroundTransitionsPolicy.IsValid(shared))
            return new() { Enabled = shared.Enabled, DurationMs = shared.DurationMs };

        var legacy = IsCustom(source.BackgroundMediaTransition) ? source.BackgroundMediaTransition
            : IsCustom(source.BackgroundAudioTriggerTransition) ? source.BackgroundAudioTriggerTransition : null;
        return new() { Enabled = legacy is not null, DurationMs = legacy?.DurationMs ?? 800 };
    }

    private static bool IsCustom(PanelBackgroundTransitionDto? settings) => settings is not null
        && PanelBackgroundTransitionPolicy.IsValid(settings)
        && PanelBackgroundTransitionsPolicy.IsCustomEffect(settings.Effect);
}
