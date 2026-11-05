using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Receiving-boundary limits for renderer-owned circle backgrounds, including disabled saved settings.</summary>
internal static class D215PanelBackgroundPolicy
{
    private const int MaximumStateBytes = 32 * 1024;
    private const int MaximumMediaOrder = 1000;

    internal static bool IsValid(D215PanelBackgroundDto? background) => background is null
        || ((background.Mode is null or "solid" or "shader" or "media")
            && ValidColor(background.Color) && ValidColor(background.ColorLight)
            && (string.IsNullOrEmpty(background.Effect) || SafeIdentifier(background.Effect))
            && (background.Template is null or >= 0 and <= 3)
            && InRange(background.Opacity, 0, 1) && InRange(background.Frost, 0, 100)
            && (string.IsNullOrEmpty(background.MediaId) || PanelBgLibrary.IsValidId(background.MediaId))
            && (background.MediaType is null or "" or "static" or "animated")
            && (background.Interval is null or 5 or 10 or 15 or 30 or 60 or 180 or 300 or 900 or 1800 or 3600)
            && (background.MediaOrder is null || (background.MediaOrder.Count <= MaximumMediaOrder
                && background.MediaOrder.All(PanelBgLibrary.IsValidId)))
            && (background.EffectState is null || ValidState(background.EffectState.Value)));

    private static bool ValidColor(string? color) => string.IsNullOrEmpty(color)
        || (color.Length == 7 && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit));

    private static bool InRange(double? value, double minimum, double maximum) => !value.HasValue
        || (double.IsFinite(value.Value) && value.Value >= minimum && value.Value <= maximum);

    private static bool SafeIdentifier(string value) => value.Length is > 0 and <= 128
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    private static bool NumberInRange(JsonElement value, double minimum, double maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        && double.IsFinite(number) && number >= minimum && number <= maximum;

    private static bool ValidState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(state.GetRawText()) > MaximumStateBytes)
            return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in state.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            var valid = property.Name switch
            {
                "speed" => NumberInRange(property.Value, -100, 100),
                "intensity" or "hue" or "colorize" => NumberInRange(property.Value, 0, 1),
                "saturation" or "contrast" => NumberInRange(property.Value, 0, 4),
                "params" => ValidMap(property.Value, 128, value => NumberInRange(value, -1e9, 1e9)),
                "sensors" => ValidMap(property.Value, 64, ValidSensor),
                _ => false,
            };
            if (!valid) return false;
        }
        return true;
    }

    private static bool ValidMap(JsonElement map, int maximum, Func<JsonElement, bool> validValue)
    {
        if (map.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
        {
            if (names.Count >= maximum || !names.Add(property.Name) || !SafeIdentifier(property.Name) || !validValue(property.Value))
            {
                return false;
            }
        }
        return true;
    }

    // Matches the public sensor-binding contract in nexus-web/src/lib/sensorBinding.ts.
    private static bool ValidSensor(JsonElement sensor)
    {
        if (sensor.ValueKind != JsonValueKind.Object
            || !sensor.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String
            || category.GetString() is not { Length: > 0 and <= 24 } categoryText || !categoryText.All(char.IsAsciiLetterOrDigit)
            || !sensor.TryGetProperty("sensor", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 and <= 512 }
            || !sensor.TryGetProperty("min", out var minimum) || !NumberInRange(minimum, -1e9, 1e9)
            || !sensor.TryGetProperty("max", out var maximum) || !NumberInRange(maximum, -1e9, 1e9))
        {
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        return minimum.GetDouble() < maximum.GetDouble()
            && sensor.EnumerateObject().All(property => names.Add(property.Name)
                && property.Name is "category" or "sensor" or "min" or "max");
    }
}
