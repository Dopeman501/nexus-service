using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests.Lighting;

public sealed class HighResolutionTickerTests
{
    [Fact]
    public async Task Relative_wait_is_cancelable_without_waiting_for_its_long_deadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var stop = new CancellationTokenSource();
        using var ticker = HighResolutionTicker.TryCreate(stop.Token);
        Assert.NotNull(ticker);
        Assert.True(ticker.WaitForDelay(TimeSpan.Zero));
        Assert.True(ticker.WaitForDelay(TimeSpan.FromTicks(-1)));
        Assert.True(ticker.WaitForDelay(TimeSpan.FromMilliseconds(.5)));
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new Thread(() =>
        {
            try { result.TrySetResult(ticker.WaitForDelay(TimeSpan.FromSeconds(30))); }
            catch (Exception error) { result.TrySetException(error); }
        }) { IsBackground = true };
        waiting.Start();
        try
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while ((waiting.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(3), "The timer worker did not enter its native wait.");
                Assert.False(result.Task.IsCompleted);
                await Task.Yield();
            }
            stop.Cancel();
            Assert.False(await result.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            stop.Cancel();
            Assert.True(waiting.Join(TimeSpan.FromSeconds(3)), "The canceled timer worker did not exit.");
        }
        Assert.False(ticker.WaitForDelay(TimeSpan.Zero));
        ticker.Dispose();
        ticker.Dispose();
    }

    [Fact]
    public void Existing_fixed_grid_wait_and_disposed_handle_return_their_original_results()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var stop = new CancellationTokenSource();
        using var ticker = HighResolutionTicker.TryCreate(stop.Token);
        Assert.NotNull(ticker);
        Assert.True(ticker.WaitForNextTick(1));
        ticker.Dispose();
        Assert.False(ticker.WaitForDelay(TimeSpan.FromMilliseconds(1)));
        Assert.False(ticker.WaitForNextTick(1));
        stop.Cancel();
        Assert.False(ticker.WaitForNextTick(1));
    }
}
