namespace Nexus.Service.Persistence;

/// <summary>
/// Single source of truth for persisted user settings. Implementation is JsonConfigStore by default.
/// All controllers should call into this rather than reading/writing files themselves.
///
/// Reads are cheap (in-memory cache after first load). Writes are atomic via tmp+rename
/// and schedule an atomic disk write. Call FlushNow() when shutdown needs the
/// latest in-memory state on disk immediately.
/// </summary>
public interface IConfigStore
{
    /// <summary>Load (or return cached) settings document.</summary>
    NexusSettings Load();

    /// <summary>
    /// Atomically read-modify-write the settings document.
    /// The mutator runs against a snapshot under a lock; the resulting document is queued for persistence.
    /// </summary>
    void Update(System.Action<NexusSettings> mutator);

    /// <summary>Atomically commit an already-validated mutation only when it returns true. A false result must leave
    /// the document untouched and causes no persistence or OnChanged notification.</summary>
    bool TryUpdate(System.Func<NexusSettings, bool> mutator) =>
        throw new System.NotSupportedException("This configuration store does not support conditional updates.");

    /// <summary>
    /// Runs <paramref name="reader"/> against the settings document without
    /// marking it dirty or raising OnChanged; the reader must not mutate. This
    /// default takes no lock, so a store with concurrent writers overrides it to
    /// read under the lock <see cref="Update"/> holds.
    /// </summary>
    void Read(System.Action<NexusSettings> reader) => reader(Load());

    /// <summary>Force any pending settings write to run now.</summary>
    void FlushNow();

    /// <summary>Filesystem path of the active settings file (for diagnostics + tests).</summary>
    string SettingsPath { get; }

    /// <summary>Invalidate the in-memory cache so the next Load() re-reads from disk.</summary>
    void Reload();

    /// <summary>Fired after any Update() persists. Subscribers can react to setting changes.</summary>
    event System.Action? OnChanged;
}
