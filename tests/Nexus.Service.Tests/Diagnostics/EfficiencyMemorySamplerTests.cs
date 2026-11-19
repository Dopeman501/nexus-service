using Nexus.Service.Diagnostics;

namespace Nexus.Service.Tests.Diagnostics;

public sealed class EfficiencyMemorySamplerTests
{
    private static EfficiencyMemorySnapshot Reading => new(123, 456, 7, 890, 901, 12, 3, 4, 5, 678, 789, 10);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    public void Only_exact_process_opt_in_enables_diagnostics(string? value, bool expected) =>
        Assert.Equal(expected, EfficiencyDiagnosticOptions.IsEnabled(value));

    [Fact]
    public void Emits_numeric_metrics_with_explicit_last_collection_semantics()
    {
        var lines = new List<string>();
        using var owner = new EfficiencyMemorySampler(() => Reading, lines.Add);
        owner.Tick();
        var line = Assert.Single(lines);
        Assert.Contains("allocatedBytes=123", line);
        Assert.Contains("managedApproxBytes=456", line);
        Assert.Contains("lastGcIndex=7", line);
        Assert.Contains("lastGcCommittedBytes=901", line);
        Assert.Contains("privateBytes=678", line);
        Assert.Contains("workingSetBytes=789", line);
        Assert.DoesNotContain("liveHeap", line);
    }

    [Fact]
    public void Sample_limit_stops_collection_and_repeated_disposal_is_safe()
    {
        var reads = 0;
        var lines = new List<string>();
        using var owner = new EfficiencyMemorySampler(() => { reads++; return Reading; }, lines.Add, 2);
        for (var i = 0; i < 5; i++) owner.Tick();
        owner.Dispose();
        owner.Dispose();
        Assert.Equal(2, reads);
        Assert.Equal(2, lines.Count);
        Assert.True(owner.IsRetired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Rejects_unbounded_or_empty_sample_budget(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EfficiencyMemorySampler(() => Reading, _ => { }, limit));

    [Fact]
    public void Retired_owner_does_not_read_or_log()
    {
        var reads = 0;
        using var owner = new EfficiencyMemorySampler(() => { reads++; return Reading; }, _ => throw new Exception());
        owner.Dispose();
        owner.Tick();
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Capture_failure_is_bounded_and_does_not_log_exception_contents()
    {
        var lines = new List<string>();
        using var owner = new EfficiencyMemorySampler(() => throw new InvalidOperationException("private-fixture-secret"), lines.Add, 2);
        owner.Tick();
        owner.Tick();
        owner.Tick();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => { Assert.Contains("unavailable=1", line); Assert.DoesNotContain("private-fixture-secret", line); });
        Assert.True(owner.IsRetired);
    }

    [Fact]
    public void Broken_log_sink_cannot_escape_timer_or_extend_the_budget()
    {
        using var owner = new EfficiencyMemorySampler(() => Reading, _ => throw new IOException("log unavailable"));
        owner.Tick();
        Assert.True(owner.IsRetired);
    }

    [Fact]
    public void No_natural_collection_yet_is_explicitly_unknown()
    {
        var lines = new List<string>();
        using var owner = new EfficiencyMemorySampler(() => Reading with { LastGcIndex = 0, LastGcCommittedBytes = 0 }, lines.Add);
        owner.Tick();
        Assert.Contains("lastGcKnown=0", Assert.Single(lines));
    }

    [Fact]
    public void Invalid_callbacks_fail_before_a_timer_can_be_created()
    {
        Assert.Throws<ArgumentNullException>(() => new EfficiencyMemorySampler(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new EfficiencyMemorySampler(() => Reading, null!));
    }

    [Fact]
    public async Task Busy_sample_is_not_duplicated_and_retirement_does_not_wait_for_reader()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var lines = new List<string>();
        using var owner = new EfficiencyMemorySampler(() =>
        {
            Interlocked.Increment(ref reads);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Reading;
        }, lines.Add);
        var active = Task.Run(owner.Tick);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            owner.Tick();
            owner.Dispose();
        }
        finally { release.Set(); }
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, reads);
        Assert.Empty(lines);
    }

    [Fact]
    public void Process_snapshot_reads_numeric_counters_without_a_host_or_device()
    {
        var snapshot = EfficiencyMemorySampler.ReadCurrentProcess();
        Assert.True(snapshot.AllocatedBytes >= 0);
        Assert.True(snapshot.PrivateBytes > 0);
        Assert.True(snapshot.WorkingSetBytes > 0);
    }

    [Fact]
    public void Factory_follows_the_single_process_opt_in()
    {
        using var owner = EfficiencyMemorySampler.StartIfEnabled(_ => { });
        Assert.Equal(EfficiencyDiagnosticOptions.Enabled, owner is not null);
    }
}
