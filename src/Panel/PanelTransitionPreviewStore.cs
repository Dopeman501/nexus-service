using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>Bounded one-shot previews, monotonic expiry and retired-editor protection.</summary>
internal sealed class PanelTransitionPreviewStore
{
    internal const int MaximumDevices = 128;
    internal const int MaximumRetiredSessions = 64;
    internal const int MaximumPreviewIds = 256;
    private const int MaximumRequestsPerSecond = 256;
    private const int StartDelayMilliseconds = 150;
    private const int SetupMarginMilliseconds = 6000;
    private const int RetiredMilliseconds = 30000;
    private const long MaximumSequence = 9007199254740991;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _revision;
    private long _requestWindowEnd;
    private int _requests;

    private sealed record Entry(PanelTransitionPreviewFrame Frame, long Expires, long RetainUntil,
        IReadOnlyDictionary<string, long> Retired, IReadOnlyDictionary<string, long> PreviewIds);

    public PanelTransitionPreviewStore() : this(TimeProvider.System) { }
    internal PanelTransitionPreviewStore(TimeProvider clock)
    {
        _clock = clock;
        _requestWindowEnd = After(clock.GetTimestamp(), 1000);
    }

    internal bool TryAcceptRequest()
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (now >= _requestWindowEnd) { _requestWindowEnd = After(now, 1000); _requests = 0; }
            if (_requests >= MaximumRequestsPerSecond) return false;
            _requests++;
            return true;
        }
    }

    internal static bool IsValid(PanelTransitionPreviewRequest? request) => request is not null
        && Guid.TryParse(request.SessionId, out var session) && session != Guid.Empty
        && request.Sequence is >= 0 and <= MaximumSequence
        && (request.Preview is null || (Guid.TryParse(request.Preview.PreviewId, out var preview) && preview != Guid.Empty
            && PanelBackgroundTransitionsPolicy.IsCustomEffect(request.Preview.Effect)
            && request.Preview.DurationMs is >= 80 and <= 5000));

    internal PanelTransitionPreviewFrame? Get(string deviceId)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            return _entries.TryGetValue(deviceId, out var entry) && now < entry.Expires && entry.Frame.Preview is not null
                ? Clone(entry.Frame) : null;
        }
    }

    internal bool TryUpdate(string deviceId, PanelTransitionPreviewRequest request, out PanelTransitionPreviewFrame? frame)
    {
        frame = null;
        if (!IsValid(request)) return false;
        var session = Guid.Parse(request.SessionId!).ToString("D");
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            _entries.TryGetValue(deviceId, out var previous);
            var retired = Retained(previous?.Retired, now);
            if (previous is null)
            {
                if (request.Preview is null || _entries.Count >= MaximumDevices) return false;
            }
            else if (previous.Frame.SessionId == session)
            {
                if (request.Sequence <= previous.Frame.Sequence) return false;
            }
            else
            {
                if (request.Preview is null || retired.ContainsKey(session) || retired.Count >= MaximumRetiredSessions) return false;
                retired.Add(previous.Frame.SessionId, After(now, RetiredMilliseconds));
            }
            var ids = Retained(previous?.PreviewIds, now);
            var selection = request.Preview;
            if (selection is not null)
            {
                var previewId = Guid.Parse(selection.PreviewId!).ToString("D");
                if (ids.ContainsKey(previewId) || ids.Count >= MaximumPreviewIds) return false;
                ids.Add(previewId, After(now, RetiredMilliseconds));
                selection = selection with { PreviewId = previewId };
            }
            var accepted = CreateFrame(deviceId, session, request.Sequence, selection);
            var lease = selection is null ? 0 : StartDelayMilliseconds + selection.DurationMs + SetupMarginMilliseconds;
            _entries[deviceId] = new(accepted, After(now, lease), After(now, lease + RetiredMilliseconds), retired, ids);
            frame = Clone(accepted);
            return true;
        }
    }

    private PanelTransitionPreviewFrame CreateFrame(string deviceId, string session, long sequence, PanelTransitionPreviewSelection? selection)
    {
        var wallNow = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var start = selection is null ? 0 : wallNow + StartDelayMilliseconds;
        return new()
        {
            DeviceId = deviceId, SessionId = session, Sequence = sequence, Preview = selection is null ? null : selection with { },
            StartedAtUnixMs = start, ExpiresAtUnixMs = selection is null ? wallNow : start + selection.DurationMs + SetupMarginMilliseconds,
            Revision = ++_revision,
        };
    }

    private static Dictionary<string, long> Retained(IReadOnlyDictionary<string, long>? values, long now) =>
        values?.Where(pair => pair.Value > now).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        ?? new(StringComparer.Ordinal);

    private long After(long timestamp, int milliseconds) => timestamp + milliseconds * _clock.TimestampFrequency / 1000;
    private void Prune(long now)
    {
        foreach (var id in _entries.Where(pair => now >= pair.Value.RetainUntil).Select(pair => pair.Key).ToArray()) _entries.Remove(id);
    }
    private static PanelTransitionPreviewFrame Clone(PanelTransitionPreviewFrame frame) =>
        frame with { Preview = frame.Preview is null ? null : frame.Preview with { } };
}
