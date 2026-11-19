#if SLV3_FAKE_NATIVE
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3DiagnosticPreservationTests
{
    [Fact]
    public void Diagnostic_modes_preserve_dynamic_pages_recovery_commands_and_last_known_devices()
    {
        var disabled = RunScenario(false);
        var enabled = RunScenario(true);
        Assert.Equal(disabled, enabled);
        Assert.Contains("pages=1,2,2", enabled);
        Assert.Contains("fans=12", enabled);
        Assert.Contains("opens=3", enabled);
        Assert.Contains("resets=1", enabled);
    }

    private static string RunScenario(bool enabled)
    {
        ServiceLog.Messages.Clear();
        using var f = new Fixture(enabled);
        for (int i = 0; i < 12; i++)
            f.Net.Fans.Add(new Slv3TestHub.SimulatedFan
            {
                Mac = new byte[] { 0xA0, 0, 0, 0, 0, (byte)(i + 1) },
                MasterMac = f.Net.MasterMac, RxType = (byte)(i + 1),
            });
        Assert.True(f.Hub.PollTick());
        Assert.True(f.Hub.PollTick());
        Assert.Equal(12, f.Hub.State.Fans.Length);
        f.Rx.Reply = _ => Array.Empty<byte>();
        Assert.True(f.Hub.PollTick());
        Assert.Equal(12, f.Hub.State.Fans.Length);
        f.Rx.Reply = null;
        Assert.True(f.Hub.PollTick());
        var learnedPages = string.Join(',', f.Rx.Sent.Where(x => x[0] == 0x10).Take(3).Select(x => x[1]));
        f.Rx.Reply = _ => new byte[64];
        for (int i = 0; i < 60; i++) Assert.True(f.Hub.PollTick());
        Assert.Equal(2, f.Opens);
        // A constant clock holds the recovery backoff even past the threshold.
        for (int i = 0; i < 240; i++) Assert.True(f.Hub.PollTick());
        Assert.Equal(2, f.Opens);
        f.Now = 5000;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(3, f.Opens);
        Assert.DoesNotContain(f.Tx.SentFrames, x => x[0] == Slv3Protocol.UsbResetAnother);
        // An incomplete header cannot attest health or reset recovery backoff.
        // Diagnostics remain observational in both modes under the new policy.
        f.Rx.Reply = _ => new byte[] { 0x10, 12, 0, 0 };
        Assert.True(f.Hub.PollTick());
        Assert.Equal(Slv3LinkStatus.Recovering, f.Hub.State.LinkStatus);
        f.Rx.Reply = _ => Array.Empty<byte>();
        for (int i = 0; i < 4; i++) Assert.True(f.Hub.PollTick());
        Assert.DoesNotContain(f.Tx.SentFrames, x => x[0] == Slv3Protocol.UsbResetAnother);
        Assert.True(f.Hub.PollTick());
        Assert.DoesNotContain(f.Tx.SentFrames, x => x[0] == Slv3Protocol.UsbResetAnother);
        // Recovery #2 starts at5s and retains its10s backoff until15s.
        f.Now = 15_000;
        Assert.True(f.Hub.PollTick());
        var resets = f.Tx.SentFrames.Count(x => x[0] == Slv3Protocol.UsbResetAnother);
        Assert.Equal(1, resets);
        Assert.Equal(12, f.Hub.State.Fans.Length);
        if (enabled)
        {
            Assert.Equal(2, f.Diagnostics.Statistics.HandleGeneration - 1);
            Assert.True(f.Diagnostics.Statistics.PartialLists > 0);
            Assert.Equal(0, f.Diagnostics.Statistics.CompleteListsSinceOpen);
        }
        var rxBytes = string.Join('/', f.Rx.Sent.Select(Convert.ToHexString));
        var txBytes = string.Join('/', f.Tx.SentFrames.Select(Convert.ToHexString));
        return $"pages={learnedPages};fans={f.Hub.State.Fans.Length};opens={f.Opens};resets={resets};rx={rxBytes};tx={txBytes}";
    }

    [Fact]
    public void Accumulated_warning_counts_include_intervening_failures_and_backoff_overrun()
    {
        using var f = new Fixture(false);
        ServiceLog.Messages.Clear();
        f.Rx.Reply = _ => new byte[64];
        for (int i = 0; i < 59; i++) Assert.True(f.Hub.PollTick());
        f.Rx.Reply = _ => Array.Empty<byte>();
        Assert.True(f.Hub.PollTick());
        f.Rx.Reply = _ => new byte[64];
        Assert.True(f.Hub.PollTick());
        var first = Assert.Single(ServiceLog.Messages, x => x.Contains("reopening RX", StringComparison.Ordinal));
        Assert.Contains("60 ambiguous", first);
        Assert.Contains("failures 1", first);
        for (int i = 0; i < 240; i++) Assert.True(f.Hub.PollTick());
        f.Now = 5000;
        Assert.True(f.Hub.PollTick());
        Assert.Contains(ServiceLog.Messages, x => x.Contains("241 ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_require_opt_in_and_are_bounded_even_across_reopen()
    {
        using var disabled = new Fixture(false);
        ServiceLog.Messages.Clear();
        disabled.Now = 30_000;
        Assert.True(disabled.Hub.PollTick());
        Assert.DoesNotContain(ServiceLog.Messages, IsReport);
        using var enabled = new Fixture(true);
        ServiceLog.Messages.Clear();
        enabled.Now = 29_999;
        Assert.True(enabled.Hub.PollTick());
        Assert.DoesNotContain(ServiceLog.Messages, IsReport);
        enabled.Now = 30_000;
        Assert.True(enabled.Hub.PollTick());
        Assert.Single(ServiceLog.Messages, IsReport);
        for (int i = 0; i < 30; i++) Assert.True(enabled.Hub.PollTick());
        Assert.Single(ServiceLog.Messages, IsReport);
    }

    private static bool IsReport(string text) => text.StartsWith("[lianli-wireless-diag]", StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        public readonly Slv3TestHub.FakeSlv3Network Net = new();
        public readonly Slv3TestHub.FakeTxTransport Tx;
        public readonly ScriptedRx Rx;
        public readonly Slv3GetDevDiagnostics Diagnostics = new();
        public readonly Slv3Hub Hub;
        public long Now;
        public int Opens;
        public Fixture(bool enabled)
        {
            Tx = new(Net);
            Rx = new(Net);
            Hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), p =>
            {
                if (p.Role == Slv3DongleRole.Tx) return Tx;
                Opens++;
                return Rx;
            }, () => Now)
            {
                ConfirmNewChains = false, SleepMs = _ => { },
                GetDevDiagnostics = enabled ? Diagnostics : null,
            };
            Assert.True(Hub.EnsureConnected());
            Tx.SentFrames.Clear();
        }
        public void Dispose() => Hub.Dispose();
    }

    private sealed class ScriptedRx(Slv3TestHub.FakeSlv3Network net) : ISlv3Transport
    {
        private readonly Slv3TestHub.FakeRxTransport _inner = new(net);
        public readonly List<byte[]> Sent = new();
        public Func<int, byte[]>? Reply;
        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";
        public bool RfSend(ReadOnlySpan<byte> frame) { Sent.Add(frame.ToArray()); return _inner.RfSend(frame); }
        public byte[] RfRead(int expectedLen) => Reply?.Invoke(expectedLen) ?? _inner.RfRead(expectedLen);
        public void Dispose() { }
    }
}
#endif
