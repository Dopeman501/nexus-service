using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Serialization;

namespace Nexus.Service.Panel;

/// <summary>Bounded leases survive neither a service restart nor a lost editor heartbeat.</summary>
internal sealed class PanelAudioTriggerPreviewStore
{
    internal const int MaximumDevices = 128;
    internal const int MaximumRetiredSessions = 64;
    private const int MaximumRequestsPerSecond = 256;
    private const int LeaseMilliseconds = 6000;
    private const int RetiredMilliseconds = 30000;
    private const long MaximumSequence = 9007199254740991;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _revision;
    private long _requestWindowEnd;
    private int _requests;

    private sealed record Entry(PanelAudioTriggerPreviewFrame Frame, long Expires, long RetainUntil,
        IReadOnlyDictionary<string, long> Retired);

    public PanelAudioTriggerPreviewStore() : this(TimeProvider.System) { }
    internal PanelAudioTriggerPreviewStore(TimeProvider clock)
    {
        _clock = clock;
        _requestWindowEnd = After(clock.GetTimestamp(), 1000);
    }

    // GET and POST share one host-local budget; no caller or device map can grow.
    internal bool TryAcceptRequest()
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            if (now >= _requestWindowEnd)
            {
                _requestWindowEnd = After(now, 1000);
                _requests = 0;
            }
            if (_requests >= MaximumRequestsPerSecond) return false;
            _requests++;
            return true;
        }
    }

    internal static bool IsValid(PanelAudioTriggerPreviewRequest? request) => request is not null
        && Guid.TryParse(request.SessionId, out var session) && session != Guid.Empty
        && request.Sequence is >= 0 and <= MaximumSequence
        && (request.Background is null || PanelAudioTriggerPolicy.IsValid(new PanelAudioTriggerDto
        {
            Backgrounds = new() { request.Background },
        }));

    internal PanelAudioTriggerPreviewFrame? Get(string deviceId)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            return _entries.TryGetValue(deviceId, out var entry) && now < entry.Expires && entry.Frame.Background is not null
                ? Clone(entry.Frame) : null;
        }
    }

    internal bool TryUpdate(string deviceId, PanelAudioTriggerPreviewRequest request, out PanelAudioTriggerPreviewFrame? frame)
    {
        frame = null;
        if (!IsValid(request)) return false;
        var session = Guid.Parse(request.SessionId!).ToString("D");
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            _entries.TryGetValue(deviceId, out var previous);
            var retired = previous?.Retired.Where(pair => pair.Value > now)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
            if (previous is null)
            {
                if (request.Background is null || _entries.Count >= MaximumDevices) return false;
            }
            else if (previous.Frame.SessionId == session)
            {
                if (request.Sequence <= previous.Frame.Sequence) return false;
            }
            else
            {
                if (request.Background is null || retired.ContainsKey(session) || retired.Count >= MaximumRetiredSessions) return false;
                retired.Add(previous.Frame.SessionId, After(now, RetiredMilliseconds));
            }
            var accepted = new PanelAudioTriggerPreviewFrame
            {
                DeviceId = deviceId, SessionId = session, Sequence = request.Sequence, Background = Copy(request.Background),
                ExpiresAtUnixMs = _clock.GetUtcNow().ToUnixTimeMilliseconds() + (request.Background is null ? 0 : LeaseMilliseconds),
                Revision = ++_revision,
            };
            _entries[deviceId] = new(accepted, After(now, request.Background is null ? 0 : LeaseMilliseconds),
                After(now, LeaseMilliseconds + RetiredMilliseconds), retired);
            frame = Clone(accepted);
            return true;
        }
    }

    private long After(long timestamp, int milliseconds) => timestamp + milliseconds * _clock.TimestampFrequency / 1000;

    private void Prune(long now)
    {
        foreach (var id in _entries.Where(pair => now >= pair.Value.RetainUntil).Select(pair => pair.Key).ToArray())
            _entries.Remove(id);
    }

    private static PanelAudioTriggerPreviewFrame Clone(PanelAudioTriggerPreviewFrame frame) => frame with { Background = Copy(frame.Background) };

    private static D215PanelBackgroundDto? Copy(D215PanelBackgroundDto? value) => value is null ? null
        : JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, AppJsonContext.Default.D215PanelBackgroundDto),
            AppJsonContext.Default.D215PanelBackgroundDto);
}
