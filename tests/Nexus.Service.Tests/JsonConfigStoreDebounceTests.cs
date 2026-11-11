using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// Validates the debounced write behaviour introduced to stop slider drags
/// from fsync'ing the full settings JSON on every frame. Uses the internal
/// test ctor so we don't touch the user's real settings.json.
/// </summary>
public class JsonConfigStoreDebounceTests : IDisposable
{
    [Fact]
    public void Conditional_update_does_not_flush_or_notify_rejection_but_commits_accepted_mutation()
    {
        using var store = new JsonConfigStore(_path);
        store.Load();
        var before = File.ReadAllBytes(_path);
        var notifications = 0;
        store.OnChanged += () => notifications++;
        Assert.False(store.TryUpdate(_ => false));
        store.FlushNow();
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.Equal(0, notifications);
        Assert.True(store.TryUpdate(settings => { settings.Lighting.Sync = "plasma"; return true; }));
        store.FlushNow();
        Assert.Equal("plasma", store.Load().Lighting.Sync);
        Assert.Equal(1, notifications);
    }

    private readonly string _tempDir;
    private readonly string _path;

    public JsonConfigStoreDebounceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Update_KeepsInMemoryStateImmediatelyConsistent()
    {
        using var store = new JsonConfigStore(_path);
        var loaded1 = store.Load();
        store.Update(s => s.Lighting.Sync = "plasma");
        var loaded2 = store.Load();
        Assert.Equal("plasma", loaded2.Lighting.Sync);
        // Same cached doc reference -- Load() must not pay a disk round-trip
        // after an Update.
        Assert.Same(loaded1, loaded2);
    }

    [Fact]
    public void MultipleUpdates_CoalesceIntoOneDiskWrite()
    {
        using var store = new JsonConfigStore(_path);
        // Pull the file into existence via the first-run Persist path.
        store.Load();
        Assert.True(File.Exists(_path), "initial file was created synchronously");
        var baselineMtime = File.GetLastWriteTimeUtc(_path);

        // Fire 20 rapid-fire updates without waiting for debounce. The disk
        // file should still be at baselineMtime -- no flush has fired yet.
        for (int i = 0; i < 20; i++)
        {
            store.Update(s => s.Lighting.FrameRate = 30 + i);
        }
        Assert.Equal(baselineMtime, File.GetLastWriteTimeUtc(_path));

        // Force the pending write.
        store.FlushNow();
        Assert.NotEqual(baselineMtime, File.GetLastWriteTimeUtc(_path));

        // Persisted content reflects the LAST value, not an intermediate one.
        var json = File.ReadAllText(_path);
        Assert.Contains("\"frameRate\": 49", json);
    }

    [Fact]
    public void Dispose_FlushesPendingWrite()
    {
        using (var store = new JsonConfigStore(_path))
        {
            store.Load();
            store.Update(s => s.Lighting.Sync = "meteor");
            // Intentionally no FlushNow -- Dispose() should flush.
        }

        Assert.True(File.Exists(_path));
        var json = File.ReadAllText(_path);
        Assert.Contains("\"sync\": \"meteor\"", json);
    }

    [Fact]
    public void FlushNow_IsIdempotent()
    {
        using var store = new JsonConfigStore(_path);
        store.Load();
        store.FlushNow();
        // Second call with nothing dirty must be a no-op, not a throw.
        store.FlushNow();
    }

    [Fact]
    public void Reload_ClearsCache()
    {
        // Store A writes, Store B opens the same path and sees the update
        // after Reload() is called.
        using (var a = new JsonConfigStore(_path))
        {
            a.Load();
            a.Update(s => s.Lighting.Sync = "spiral");
            a.FlushNow();
        }
        using var b = new JsonConfigStore(_path);
        var s = b.Load();
        Assert.Equal("spiral", s.Lighting.Sync);
    }
}
