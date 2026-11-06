using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Panel;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Panel;

internal static class D215PanelsPolicy
{
    internal const int MaximumPanels = 6;
    private static readonly HashSet<string> BuiltInTypes = new(StringComparer.Ordinal)
        { "clock", "calendar", "monitoring", "media", "weather", "gallery" };
    private static readonly HashSet<string> Surfaces = new(StringComparer.Ordinal)
        { "y70", "q60", "phone", "desktop", "monitor", "lcd-round", "lcd-square", "lcd-wide", "lcd-portrait", "kraken" };

    internal static bool IsD215(PanelDeviceCapabilities? capabilities) =>
        capabilities?.Family == JungleLeopardD215Handler.HandlerId
        && capabilities.Surface is PanelSurfaces.LcdPortrait or PanelSurfaces.LcdWide;

    internal static bool IsValid(PanelLayoutDto layout, PanelDeviceCapabilities? capabilities, AppRegistry apps, PanelLayoutDto? stored)
    {
        if (layout.D215Panels is null && layout.D215Widget is null) return true;
        if (!IsD215(capabilities) || !ValidWidget(layout.D215Widget)
            || (layout.D215Panels is not null && !ValidStructure(layout)))
        {
            return false;
        }
        if (layout.D215Panels is null) return true;
        var widgetTypes = (layout.Pages?.SelectMany(page => page?.Widgets ?? []) ?? [])
            .Where(widget => widget is not null).Select(widget => widget.Type);
        var widgetEnabled = layout.D215Widget?.Enabled ?? !layout.D215Panels.Enabled;
        if (widgetEnabled && layout.WidgetPlaylist?.Enabled == true)
            widgetTypes = widgetTypes.Concat(layout.WidgetPlaylist.Types ?? []);
        var widgetApps = widgetTypes.Select(type => WidgetSettingsService.MarketplaceIdFromType(AppPrefixMigration.Rewrite(type))).OfType<string>();
        var singleInstances = new HashSet<string>(widgetApps, StringComparer.Ordinal);
        foreach (var item in layout.D215Panels.Items)
        {
            var appId = WidgetSettingsService.MarketplaceIdFromType(item.Type);
            if (appId is null) continue;
            if (!apps.TryGet(appId, out var entry))
            {
                // A temporarily missing bundle must not block edits to an existing composition.
                if (stored?.D215Panels?.Items?.Any(old => old is not null && old.Id == item.Id && old.Type == item.Type) != true)
                    return false;
                continue;
            }
            if (!SupportsRound(entry.Manifest)
                || (entry.Manifest.SingleInstance && !singleInstances.Add(appId)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidStructure(PanelLayoutDto layout)
    {
        if (layout.D215Panels?.Items is not { Count: <= MaximumPanels } items) return false;
        var ids = new HashSet<string>(layout.Pages?.SelectMany(page => page?.Widgets ?? [])
            .Where(widget => widget is not null).Select(widget => widget.Id) ?? [], StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 || !ids.Add(item.Id)
                || string.IsNullOrEmpty(item.Type)
                || (!BuiltInTypes.Contains(item.Type) && !AppIds.IsValid(WidgetSettingsService.MarketplaceIdFromType(item.Type)))
                || !double.IsFinite(item.X) || item.X is < 0 or > 1
                || !double.IsFinite(item.Y) || item.Y is < 0 or > 1
                || !double.IsFinite(item.Diameter) || item.Diameter is < 0.12 or > 1
                || !D215PanelBackgroundPolicy.IsValid(item.Background))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SupportsRound(AppManifest manifest) =>
        manifest.Sizes.Contains("2x2round")
        && (!manifest.Surfaces.Any(Surfaces.Contains)
            || manifest.Surfaces.Any(surface => PanelSurfaces.Canonical(surface) == PanelSurfaces.LcdRound));

    private static bool ValidWidget(D215WidgetDto? widget) => widget is null
        || (double.IsFinite(widget.X) && widget.X is >= 0 and <= 1
            && double.IsFinite(widget.Y) && widget.Y is >= 0 and <= 1
            && double.IsFinite(widget.Scale) && widget.Scale is >= 0.12 and <= 1);

    /// <summary>Imported looks remain portable, but malformed or foreign-device compositions never reach a live target.</summary>
    internal static PanelLayoutDto? ForTarget(PanelLayoutDto? layout, PanelDeviceCapabilities? capabilities)
    {
        if (layout is null) return null;
        var targetIsD215 = IsD215(capabilities);
        var keepPanels = layout.D215Panels is null || (targetIsD215 && ValidStructure(layout));
        var keepWidget = layout.D215Widget is null || (targetIsD215 && ValidWidget(layout.D215Widget));
        if (keepPanels && keepWidget) return layout;
        var safe = PanelPresets.CloneLayout(layout)!;
        if (!keepPanels) safe.D215Panels = null;
        if (!keepWidget) safe.D215Widget = null;
        return safe;
    }
}
