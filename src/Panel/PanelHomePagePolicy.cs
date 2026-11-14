using System;
using System.Linq;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Home follows a real page identity, never a transient rendered slot.</summary>
internal static class PanelHomePagePolicy
{
    internal static string? Resolve(PanelLayoutDto layout)
    {
        var id = layout.HomePageId;
        return !string.IsNullOrWhiteSpace(id) && id != "blank-page"
            && layout.Pages?.Any(page => page is not null && string.Equals(page.Id, id, StringComparison.Ordinal)) == true
                ? id : null;
    }

    /// <summary>Clear stale or invalid references without editing the received object.</summary>
    internal static PanelLayoutDto Normalize(PanelLayoutDto layout) =>
        string.Equals(layout.HomePageId, Resolve(layout), StringComparison.Ordinal)
            ? layout : PanelPresets.CloneLayout(layout)!;
}
