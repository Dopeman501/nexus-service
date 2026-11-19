#if OFFLINE_SMOKE
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Lifecycle;

/// <summary>Compile-time-only AOT route/package probe. Never compiled into the shipping host.</summary>
internal static class OfflineSmokeHost
{
    internal static string DataRoot => Path.Combine(AppContext.BaseDirectory, "offline-smoke-data");

    internal static void Prepare(string[] args)
    {
        if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var target)
            || target.Scheme != "http" || !target.IsLoopback || target.Port is < 49152 or > 65535
            || target.AbsolutePath != "/" || target.Query.Length != 0 || target.Fragment.Length != 0 || target.UserInfo.Length != 0)
            throw new InvalidOperationException("Offline smoke requires one private high-port loopback HTTP origin; lifecycle flags are disabled.");
        // Fixed under this separate probe publish, never an installed/user-selected root.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = root; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Offline smoke publish cannot use redirected directories.");
        if (Directory.Exists(DataRoot) && (File.GetAttributes(DataRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Offline smoke data cannot be redirected.");
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable("NEXUS_TEST_HOST", "1");
        Environment.SetEnvironmentVariable("NEXUS_DATA_ROOT", DataRoot);
    }

    internal static void Configure(IServiceCollection services)
    {
        services.RemoveAll<IHostedService>();
        services.RemoveAll<OpenRgbProcessManager>();
        services.AddSingleton<OpenRgbProcessManager>(_ => new(Path.Combine(DataRoot, "disabled-openrgb-do-not-create.exe"))
        { ConfigDirectoryResolver = () => Path.Combine(DataRoot, "openrgb") });
        services.RemoveAll<IRgbController>();
        services.AddSingleton<IRgbController, NoOpRgbController>();
        services.RemoveAll<IBeatsProvider>();
        services.AddSingleton<IBeatsProvider, StubBeatsProvider>();
    }

    // The three policy GETs depend only on the private IConfigStore and AOT JSON context.
    // All mutation, sockets, monitoring/device routes and lifecycle verbs are blocked.
    internal static bool Allows(string method, string? path) => method == "GET" && path is not null
        && !path.Contains('%') && !path.Contains('\\') && !path.Split('/').Any(part => part is "." or "..")
        && (path is "/" or "/index.html" or "/favicon.ico" or "/lighting/audio-trigger"
            or "/lighting/animate/settings" or "/lighting/static/settings"
            || path.StartsWith("/assets/", StringComparison.Ordinal)
            || path.StartsWith("/background-transitions/", StringComparison.Ordinal));
}
#endif
