using System.Diagnostics;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests;

public class OverlayHostShutdownTests
{
    [Fact]
    public void StopReapsUntrackedOverlayFromItsOwnPackageOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("nexus-overlay-stop-");
        try
        {
            var ownPath = CopyOverlayStandIn(root.FullName, "own");
            var otherPath = CopyOverlayStandIn(root.FullName, "other");
            using var own = StartStandIn(ownPath);
            try
            {
                using var other = StartStandIn(otherPath);
                try { AssertPackageShutdown(root.FullName, ownPath, own, other); }
                finally { ExitStandIn(other); }
            }
            finally { ExitStandIn(own); }
        }
        finally { root.Delete(recursive: true); }
    }

    private static void AssertPackageShutdown(string root, string ownPath, Process own, Process other)
    {
        Assert.False(own.HasExited);
        Assert.False(other.HasExited);
        // The tray started both outside the launcher. A stale PID file
        // also points at the other installation's identically named exe.
        var pidFile = Path.Combine(root, "overlay.pid");
        File.WriteAllText(pidFile, other.Id.ToString());
        var launcher = new PanelOverlayHostLauncher(ownPath, pidFile);

        launcher.Stop();

        Assert.True(own.WaitForExit(2000), "The untracked overlay did not exit.");
        Assert.False(other.HasExited);
    }

    private static string CopyOverlayStandIn(string root, string package)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, package, "overlay"));
        var path = Path.Combine(dir.FullName, "nexus-overlay.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        return path;
    }

    private static Process StartStandIn(string path) => Process.Start(new ProcessStartInfo(path)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        ArgumentList = { "/d", "/q", "/k" },
    })!;

    private static void ExitStandIn(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(2000), "Could not clean up the test process.");
    }
}
