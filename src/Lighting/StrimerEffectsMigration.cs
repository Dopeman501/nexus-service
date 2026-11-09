using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Strimer speed and colors used to be one shared setting; each effect now keeps its own. The effect the
/// user was on takes the old values, so it plays exactly as before, and every other effect starts from its default.
/// </summary>
public static class StrimerEffectsMigration
{
    public static void Apply(NexusSettings doc)
    {
        if (doc.Devices?.StrimerLighting is not { } ls || ls.ModeSpeeds.Count > 0 || ls.ModeColors.Count > 0)
        {
            return;
        }
        // A document that never set anything keeps starting each effect at its own default.
        if (ls is { Mode: "rainbow", EffectMode: null, Speed: 2, Colors.Count: 0 })
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
