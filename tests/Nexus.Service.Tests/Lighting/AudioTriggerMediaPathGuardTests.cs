using System.Diagnostics;
using Nexus.Service.Lighting;
using Xunit.Sdk;

namespace Nexus.Service.Tests.Lighting;

public sealed class AudioTriggerMediaPathGuardTests
{
    [Fact]
    public void Pure_attribute_guard_rejects_links_and_accepts_regular_entries()
    {
        AudioTriggerMediaPathGuard.CheckAttributes(FileAttributes.Directory);
        Assert.Throws<IOException>(() => AudioTriggerMediaPathGuard.CheckAttributes(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void An_ancestor_junction_cannot_create_a_cache_in_the_redirected_directory()
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(target);
        var link = Path.Combine(fixture.Root, "redirect");
        Link(link, target);
        Assert.Throws<IOException>(() => new AudioTriggerMediaSnapshotStore(Path.Combine(link, "cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Theory]
    [InlineData(".owner")]
    [InlineData(".inventory")]
    [InlineData(".inventory.pending")]
    public void Owned_metadata_children_cannot_redirect_io(string name)
    {
        using var fixture = new Fixture();
        var cache = Path.Combine(fixture.Root, "cache");
        Directory.CreateDirectory(cache);
        var target = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(target);
        Link(Path.Combine(cache, name), target);
        if (name == ".inventory.pending")
        {
            using var source = new LightingAudioTriggerMediaTests.LibraryFixture();
            source.Write(160, 90, 1, 0);
            using var store = new AudioTriggerMediaSnapshotStore(cache);
            Assert.Throws<IOException>(() => store.CreateAsync(source.Library,
                "clip", new byte[65536], CancellationToken.None).GetAwaiter().GetResult());
        }
        else Assert.Throws<IOException>(() => new AudioTriggerMediaSnapshotStore(cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task A_pending_inventory_hardlink_cannot_overwrite_its_other_file_name()
    {
        if (!OperatingSystem.IsWindows()) throw SkipException.ForSkip("The NTFS hardlink fixture is Windows-only.");
        using var fixture = new Fixture();
        using var source = new LightingAudioTriggerMediaTests.LibraryFixture();
        source.Write(160, 90, 1, 0);
        var cache = Path.Combine(fixture.Root, "cache");
        using var store = new AudioTriggerMediaSnapshotStore(cache);
        var outside = Path.Combine(fixture.Root, "outside.txt");
        File.WriteAllText(outside, "preserve");
        Link(Path.Combine(cache, ".inventory.pending"), outside, hard: true);
        await store.CreateAsync(source.Library, "clip", new byte[65536], CancellationToken.None);
        Assert.Equal("preserve", File.ReadAllText(outside));
    }

    internal static void Link(string path, string target, bool hard = false)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", hard ? "/H" : "/J", path, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
            throw SkipException.ForSkip("The private junction fixture is unavailable.");
        if (!hard) Assert.NotEqual((FileAttributes)0, File.GetAttributes(path) & FileAttributes.ReparsePoint);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-cache-path-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(Root).StartsWith("nexus-cache-path-", StringComparison.Ordinal)) throw new InvalidOperationException();
            UnlinkChildren(Root);
            Directory.Delete(Root, recursive: true);
        }

        private static void UnlinkChildren(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry, recursive: false);
                else if ((attributes & FileAttributes.Directory) != 0) UnlinkChildren(entry);
            }
        }
    }
}
