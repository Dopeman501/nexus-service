using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Strimer speed and colors used to be one shared setting; each effect now keeps its own. The effect the
/// user was on takes the old values, so it plays exactly as before, and every other effect starts from its default.
/// </summary>
public static class StrimerEffectsMigration
{
    /// <summary>True when the stored document has a Strimer settings block at all.</summary>
    public static bool HasSettings(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (var devices in doc.RootElement.EnumerateObject())
            {
                if (!devices.NameEquals("devices") && !string.Equals(devices.Name, "devices", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (devices.Value.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                foreach (var block in devices.Value.EnumerateObject())
                {
                    if (string.Equals(block.Name, "strimerLighting", System.StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return false;
    }

    public static void Apply(NexusSettings doc)
    {
        if (doc.Devices?.StrimerLighting is not { } ls || ls.ModeSpeeds.Count > 0 || ls.ModeColors.Count > 0)
        {
            return;
        }
        var key = ls.Mode != "custom" ? ls.Mode : ls.EffectMode ?? "rainbow";
        if (StrimerLightingModes.Find(key) is { } mode)
        {
            // The two fastest levels swapped bytes, so the level that sent the same byte as before is the other one.
            var level = ls.Speed switch { 3 => 4, 4 => 3, _ => ls.Speed };
            ls.Speed = level;
            if (mode.HasSpeed)
            {
                ls.ModeSpeeds[key] = level;
            }
            if (mode.DefaultColors.Length > 0 && ls.Colors.Count > 0)
            {
                ls.ModeColors[key] = new List<string>(ls.Colors);
            }
        }
        ls.Colors.Clear();
    }
}
