using System.Text.Json;
using System.Reflection;
using Nexus.Service.Lighting;
using Nexus.Service.Media;
using Nexus.Service.Models.Media;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public sealed class AudioTriggerMediaSnapshotTests
{
    [Fact]
    public async Task A_validated_snapshot_survives_original_deletion_and_restart_cleans_only_owned_files()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("clip", 2);
        var cache = Path.Combine(fixture.Root, "cache");
        using (var store = new AudioTriggerMediaSnapshotStore(cache))
        {
            var clip = await store.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None);
            File.Delete(source);
            Assert.True(fixture.Library.DeleteItem("clip"));
            var frame = new byte[AudioTriggerMediaSnapshot.FrameBytes];
            await clip.ReadFrameAsync(1, frame, CancellationToken.None);
            Assert.Equal(17, frame[0]);
            Assert.Equal(2 * frame.Length, store.ReservedBytes);
            Assert.Throws<IOException>(() => new AudioTriggerMediaSnapshotStore(cache));
        }
        var unrelated = Path.Combine(cache, "unrelated.txt");
        File.WriteAllText(unrelated, "preserve");
        using var reopened = new AudioTriggerMediaSnapshotStore(cache);
        Assert.Equal(0, reopened.ReservedBytes);
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task Reservation_rejects_low_budget_before_creating_a_snapshot_and_truncation_releases_capacity()
    {
        using var fixture = new Fixture();
        fixture.Write("clip", 2);
        using var limited = new AudioTriggerMediaSnapshotStore(Path.Combine(fixture.Root, "small"), 43_200);
        await Assert.ThrowsAsync<IOException>(() => limited.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None));
        Assert.Equal(0, limited.ReservedBytes);
        var source = fixture.Library.GetFramesBinPath("clip");
        File.WriteAllBytes(source, new byte[43_200]);
        using var store = new AudioTriggerMediaSnapshotStore(Path.Combine(fixture.Root, "cache"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None));
        Assert.Equal(0, store.ReservedBytes);
    }

    [Fact]
    public async Task Read_boundaries_ownership_and_failed_inventory_reservations_are_checked()
    {
        using var fixture = new Fixture();
        fixture.Write("clip", 1);
        var cache = Path.Combine(fixture.Root, "cache");
        using var store = new AudioTriggerMediaSnapshotStore(cache);
        Directory.CreateDirectory(Path.Combine(cache, ".inventory.pending"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None));
        Assert.Equal(0, store.ReservedBytes);
        Directory.Delete(Path.Combine(cache, ".inventory.pending"));
        var clip = await store.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => clip.ReadFrameAsync(-1, new byte[43_200], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => clip.ReadFrameAsync(0, new byte[1], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => clip.ReadAllAsync(new byte[1], CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => store.Delete(new(Path.Combine(fixture.Root, "unowned.rgb"), 1, 0)));
        using (var held = new FileStream(clip.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Delete(clip);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(43_200, store.ReservedBytes);
                store.Delete(clip); // Repeated cleanup stays charged and reports once for this owned file.
                Assert.Equal(43_200, store.ReservedBytes);
            }
        }
        if (OperatingSystem.IsWindows()) store.Delete(clip);
        Assert.Equal(0, store.ReservedBytes);
    }

    [Fact]
    public async Task The_bound_snapshot_keeps_its_generation_when_the_owned_filename_is_replaced()
    {
        using var fixture = new Fixture();
        fixture.Write("clip", 1);
        using var store = new AudioTriggerMediaSnapshotStore(Path.Combine(fixture.Root, "cache"));
        var clip = await store.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None);
        File.Move(clip.Path, clip.Path + ".retired");
        File.WriteAllBytes(clip.Path, Enumerable.Repeat((byte)99, 43_200).ToArray());
        var frame = new byte[43_200];
        await clip.ReadFrameAsync(0, frame, CancellationToken.None);
        Assert.All(frame, value => Assert.Equal(0, value));
        store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => clip.ReadFrameAsync(0, frame, CancellationToken.None));
    }

    [Fact]
    public async Task Successful_file_deletion_never_retains_warning_keys_when_inventory_refresh_fails()
    {
        using var fixture = new Fixture();
        fixture.Write("clip", 1);
        var cache = Path.Combine(fixture.Root, "cache");
        using var store = new AudioTriggerMediaSnapshotStore(cache);
        var pending = Path.Combine(cache, ".inventory.pending");
        for (var generation = 0; generation < 16; generation++)
        {
            var clip = await store.CreateAsync(fixture.Library, "clip", new byte[65536], CancellationToken.None);
            Directory.CreateDirectory(pending);
            store.Delete(clip);
            Assert.False(File.Exists(clip.Path));
            Assert.Equal(0, store.ReservedBytes);
            Directory.Delete(pending);
        }
        var warnings = Assert.IsType<HashSet<string>>(typeof(AudioTriggerMediaSnapshotStore)
            .GetField("_cleanupWarnings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("../escape.rgb|43200")]
    [InlineData("invalid")]
    [InlineData("00000000000000000000000000000000.rgb|0")]
    [InlineData("00000000000000000000000000000000.rgb|12960001")]
    public void Invalid_inventory_never_reaches_an_unowned_file(string inventory)
    {
        using var fixture = new Fixture();
        var cache = Path.Combine(fixture.Root, "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, ".inventory"), inventory);
        Assert.Throws<InvalidDataException>(() => new AudioTriggerMediaSnapshotStore(cache));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-media-snapshot-" + Guid.NewGuid().ToString("N"));
        internal MediaLibrary Library { get; }
        internal Fixture() => Library = new(Path.Combine(Root, "library"));
        internal string Write(string id, int frames)
        {
            Directory.CreateDirectory(Library.GetItemDir(id));
            var metadata = new MediaItem { Id = id, Width = 160, Height = 90, Frames = frames, Fps = frames > 1 ? 30 : 0 };
            File.WriteAllText(Path.Combine(Library.GetItemDir(id), "meta.json"), JsonSerializer.Serialize(metadata, AppJsonContext.Default.MediaItem));
            var bytes = new byte[frames * 43_200];
            for (var frame = 0; frame < frames; frame++) Array.Fill(bytes, (byte)(frame * 17), frame * 43_200, 43_200);
            var path = Library.GetFramesBinPath(id);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
