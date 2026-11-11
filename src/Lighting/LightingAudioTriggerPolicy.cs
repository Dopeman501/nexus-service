using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting;

internal static class LightingAudioTriggerPolicy
{
    internal const int MaximumLooks = 32;

    internal static bool IsValid(LightingAudioTriggerSettings? value)
    {
        if (value is null)
        {
            return true;
        }
        if (!double.IsFinite(value.Intensity) || value.Intensity is < 0 or > 100
            || !double.IsFinite(value.SwitchDelay) || value.SwitchDelay is < 0 or > 10
            || value.Looks is not { Count: <= MaximumLooks } || value.Looks.Any(look => !ValidLook(look))
            || value.DeviceOverrides is { Count: > 512 }
            || value.DeviceOverrides?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256
                || pair.Value is null || (pair.Value.Intensity is { } intensity && (!double.IsFinite(intensity) || intensity is < 0 or > 100))) == true)
        {
            return false;
        }
        return JsonSerializer.SerializeToUtf8Bytes(value, AppJsonContext.Default.LightingAudioTriggerSettings).Length <= 128 * 1024;
    }

    private static bool ValidLook(LightingAudioTriggerLook? look)
    {
        if (look is null)
        {
            return false;
        }
        if (look.Mode == "media")
        {
            return MediaLibrary.IsValidId(look.MediaId) && look.Color is null && look.Effect is null && look.State is null;
        }
        if (look.Mode is not ("static" or "animate") || look.MediaId is not null) return false;
        if (look.Color is not null)
        {
            return look.Mode == "static" && look.Effect is null && look.State is null
                && look.Color.Length == 7 && StaticColorHex.TryParse(look.Color, out _, out _, out _);
        }
        if (look.Effect is not { Length: > 0 and <= 128 } effect || ShaderLibrary.IsCustomKey(effect)
            || !ShaderLibrary.IsKnownKey(effect))
        {
            return false;
        }
        var state = look.State;
        return state is null || (state.Speed is >= -100 and <= 100
            && InRange(state.Intensity, 0, 1) && InRange(state.Hue, 0, 1) && InRange(state.Colorize, 0, 1)
            && InRange(state.Saturation, 0, 4) && InRange(state.Contrast, 0, 4)
            && state.Sensors is null && state.Params is { Count: <= 128 }
            && state.Params.All(pair => ValidName(pair.Key) && float.IsFinite(pair.Value) && Math.Abs(pair.Value) <= 1e9));
    }

    private static bool InRange(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
    private static bool ValidName(string name) => name.Length is > 0 and <= 128
        && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    internal static LightingAudioTriggerSettings? Clone(LightingAudioTriggerSettings? value)
    {
        if (value is null || !IsValid(value))
        {
            return null;
        }
        return new()
        {
            Enabled = value.Enabled, Intensity = value.Intensity, SwitchDelay = value.SwitchDelay, Shuffle = value.Shuffle,
            DeviceOverrides = value.DeviceOverrides?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
                pair => new LightingAudioTriggerDeviceOverride { Enabled = pair.Value.Enabled, Intensity = pair.Value.Intensity }, StringComparer.Ordinal),
            Looks = value.Looks.Select(look => new LightingAudioTriggerLook
            {
                Mode = look.Mode, Color = look.Color?.ToLowerInvariant(), Effect = look.Effect, MediaId = look.MediaId,
                State = look.Effect is { } effect ? CloneState(effect, look.State) : null,
            }).ToList(),
        };
    }

    private static AnimateEffectState CloneState(string effect, AnimateEffectState? value)
    {
        value ??= new();
        var copy = new AnimateEffectState
        {
            Speed = value.Speed, Intensity = value.Intensity, Hue = value.Hue, Colorize = value.Colorize,
            Saturation = value.Saturation, Contrast = value.Contrast,
            Params = value.Params.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
        };
        ShaderParamSpec.Sanitize(effect, copy);
        return copy;
    }
}
