using System;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Panel;

/// <summary>Bounds incoming audio selections and sanitizes imported profile/preset copies.</summary>
internal static class PanelAudioTriggerPolicy
{
    internal const int MaximumBackgrounds = 32;
    private const int MaximumBytes = 128 * 1024;

    internal static bool IsValid(PanelAudioTriggerDto? settings)
    {
        if (settings is null) return true;
        if (!double.IsFinite(settings.Intensity) || settings.Intensity is < 0 or > 100
            || !double.IsFinite(settings.SwitchDelay) || settings.SwitchDelay is < 0 or > 10
            || settings.Backgrounds is not { Count: <= MaximumBackgrounds } backgrounds
            || backgrounds.Any(background => background is null
                || background.Mode is not ("solid" or "shader" or "media")
                || background.Slideshow == true || background.MediaOrder is { Count: > 0 }
                || (background.Mode == "media" && (string.IsNullOrEmpty(background.MediaId)
                    || background.MediaType is not ("static" or "animated")))
                || !D215PanelBackgroundPolicy.IsValid(background)))
        {
            return false;
        }
        return JsonSerializer.SerializeToUtf8Bytes(settings, AppJsonContext.Default.PanelAudioTriggerDto).Length <= MaximumBytes;
    }

    /// <summary>Invalid imported settings are omitted without changing the standard background.</summary>
    internal static PanelAudioTriggerDto? Clone(PanelAudioTriggerDto? settings) => settings is null || !IsValid(settings)
        ? null
        : JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(settings, AppJsonContext.Default.PanelAudioTriggerDto),
            AppJsonContext.Default.PanelAudioTriggerDto);
}
