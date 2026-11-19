using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Panel.Streams;

internal readonly record struct SharedFrameReadSnapshot(long Reads, long FirstSequence,
    long LastSequence, long SkippedBetweenReads, long Duplicates, long Invalid);

/// <summary>One reader/diagnostic epoch. The first observed sequence is a baseline, not a loss count.</summary>
internal sealed class SharedFrameReadDiagnostics
{
    private readonly string _tag;
    private readonly long _revision, _frequency, _created;
    private long _reads, _first, _last, _skipped, _duplicates, _invalid, _lastReport;
    private int _reports;

    internal SharedFrameReadDiagnostics(string section, long revision, long frequency, long created)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(section)))[..12];
        _revision = revision; _frequency = frequency; _created = created;
    }

    internal void Observe(long sequence)
    {
        if (sequence <= 0 || (sequence & 1) != 0 || sequence < _last) { _invalid++; return; }
        if (sequence == _last) { _duplicates++; return; }
        if (_reads == 0) _first = sequence;
        else _skipped += (sequence - _last) / 2 - 1;
        _last = sequence; _reads++;
    }

    internal SharedFrameReadSnapshot Snapshot() => new(_reads, _first, _last, _skipped, _duplicates, _invalid);

    internal string? Report(long now)
    {
        if (now < _created || _reports >= 72 || (_reports > 0 && (double)(now - _lastReport) / _frequency < 30)) return null;
        _reports++; _lastReport = now;
        return string.Create(CultureInfo.InvariantCulture,
            $"[efficiency-shared] v=1 role=reader section={_tag} epoch={_revision} sample={_reports} elapsedMs={(long)((double)(now - _created) * 1000 / _frequency)} reads={_reads} firstSequence={_first} consumedSequence={_last} skippedBetweenReads={_skipped} duplicates={_duplicates} invalid={_invalid} firstSequenceIsBaseline=1");
    }
}
