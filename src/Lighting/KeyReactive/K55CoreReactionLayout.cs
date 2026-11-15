using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.KeyReactive;

/// <summary>
/// Explicit ten-zone CORE capability. Physical zone indices survive card slicing;
/// a keyboard with ten arbitrary LEDs never acquires this capability by name.
/// Key membership is transcribed from iCUE's k55_core_rgb manifest (SHA-256
/// 0d4c2f392c6ae46f6cd5d2188173c074c4964113a2b92113da65a9a32fb792c2).
/// No vendor implementation or artwork is included.
/// </summary>
public sealed partial class K55CoreReactionLayout
{
    public const int PhysicalZones = 10;
    public static readonly IReadOnlyList<string> KeyboardLayouts = Array.AsReadOnly(new[] { "US", "UK", "BR", "JP" });
    public static readonly IReadOnlyList<string> SupportedEffects = Array.AsReadOnly(new[] { KeyReactionCatalog.Fade, KeyReactionCatalog.Ripple });
    private readonly int[] _physical;
    private readonly string _projection;

    // Centres in the existing full-size nominal key space. These describe shared
    // light regions, not individually addressable keys or measured LED locations.
    private static readonly float[] CentresX = { 0.6f, 2.4f, 4.4f, 6.4f, 8.7f, 10.9f, 13.1f, 16.25f, 19f, 20.9f };
    public int LedCount => _physical.Length;

    private K55CoreReactionLayout(int[] physical)
    {
        _physical = physical;
        _projection = string.Create(physical.Length + 3, physical, static (span, indices) =>
        {
            span[0] = 'c'; span[1] = '1'; span[2] = ':';
            for (var i = 0; i < indices.Length; i++) span[i + 3] = (char)('0' + indices[i]);
        });
    }

    /// <summary>Stable, bounded runtime identity for the card's physical slots and disabled mask; never persisted.</summary>
    public string Revision(bool[]? disabled)
    {
        var mask = 0;
        for (var i = 0; i < LedCount; i++)
            if (disabled is not null && i < disabled.Length && disabled[i]) mask |= 1 << i;
        return _projection + ":" + mask.ToString("X3", CultureInfo.InvariantCulture);
    }

    internal static bool ValidRevision(string? revision)
    {
        if (revision is null) return true;
        if (revision.Length is < 8 or > 17 || !revision.StartsWith("c1:", StringComparison.Ordinal)
            || revision[^4] != ':')
        {
            return false;
        }
        for (var i = 3; i < revision.Length - 4; i++)
            if (revision[i] is < '0' or > '9') return false;
        for (var i = revision.Length - 3; i < revision.Length; i++)
            if (!char.IsAsciiHexDigit(revision[i])) return false;
        return true;
    }

    public static bool IsKnownLayout(string? layout) => layout is "US" or "UK" or "BR" or "JP";

    public static K55CoreReactionLayout? Create(RgbDevice device, int offset, int count, IReadOnlyList<ZoneSlice>? slices = null)
    {
        if (device.Type != 5 || device.LedCount != PhysicalZones || !HasCoreUsbIdentity(device.Location)
            || count is <= 0 or > PhysicalZones)
        {
            return null;
        }
        var physical = new int[count];
        if (slices is null)
        {
            if (offset < 0 || offset > PhysicalZones - count) return null;
            for (var i = 0; i < count; i++) physical[i] = offset + i;
        }
        else
        {
            var pos = 0;
            var used = new HashSet<int>();
            foreach (var slice in slices)
            {
                if (slice.Segment < 0 || slice.Segment >= device.Zones.Count || slice.Start < 0 || slice.Count <= 0
                    || slice.Count > count - pos || slice.Start > device.Zones[slice.Segment].LedCount - slice.Count)
                {
                    return null;
                }
                var zoneStart = 0;
                for (var z = 0; z < slice.Segment; z++) zoneStart += device.Zones[z].LedCount;
                for (var i = 0; i < slice.Count; i++)
                {
                    var index = zoneStart + slice.Start + i;
                    if (index < 0 || index >= PhysicalZones || !used.Add(index)) return null;
                    physical[pos++] = index;
                }
            }
            if (pos != count) return null;
        }
        return new K55CoreReactionLayout(physical);
    }

    private static bool HasCoreUsbIdentity(string location)
    {
        const string token = "vid_1b1c&pid_1bfe";
        var index = location.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var end = index + token.Length;
        return (index == 0 || !char.IsAsciiLetterOrDigit(location[index - 1]))
            && (end == location.Length || !char.IsAsciiLetterOrDigit(location[end]));
    }

    public bool SameProjection(K55CoreReactionLayout other) => _physical.AsSpan().SequenceEqual(other._physical);

    /// <summary>The owned local slot for one immediate physical neighbour, or -1 when that slot is outside this card.</summary>
    internal int NeighbourIndex(int led, bool right)
    {
        if ((uint)led >= (uint)_physical.Length) return -1;
        var physical = _physical[led] + (right ? 1 : -1);
        for (var i = 0; i < _physical.Length; i++)
            if (_physical[i] == physical) return i;
        return -1;
    }

    public float[] NormalizedX()
    {
        var x = new float[LedCount];
        for (var i = 0; i < x.Length; i++) x[i] = CentresX[_physical[i]] / KeyNames.NominalFullWidth;
        return x;
    }

    public float[] NormalizedY()
    {
        var y = new float[LedCount];
        Array.Fill(y, 0.5f);
        return y;
    }

    public KeyboardGeometry? BuildGeometry(string? region, bool[]? disabled)
    {
        if (!IsKnownLayout(region)) return null;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var x = new float[LedCount];
        var y = new float[LedCount];
        var active = new bool[LedCount];
        var physicalKeys = KeyZones[region!];
        for (var i = 0; i < LedCount; i++)
        {
            x[i] = CentresX[_physical[i]];
            y[i] = 2.5f;
            active[i] = disabled is null || i >= disabled.Length || !disabled[i];
            if (!active[i]) continue;
            foreach (var (key, zone) in physicalKeys)
                if (zone == _physical[i]) byName[key] = i;
        }
        return KeyboardGeometry.BuildMapped(x, y, active, byName, KeyNames.NominalFullWidth, KeyNames.NominalBottomRow, this);
    }
}
