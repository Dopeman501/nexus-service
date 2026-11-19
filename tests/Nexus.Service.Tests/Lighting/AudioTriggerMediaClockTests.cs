using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Tests.Lighting;

public sealed class AudioTriggerMediaClockTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 10)]
    [InlineData(300, 30)]
    public void Clock_matches_existing_media_first_render_loops_inactivity_and_clock_jumps(int count, int fps)
    {
        var data = new byte[count * 3];
        for (var frame = 0; frame < count; frame++) data[frame * 3] = (byte)frame;
        using var reference = new MediaFramesEffect(data, 1, 1, fps);
        var canvas = new CanvasBuffer(1, 1);
        var clock = default(AudioTriggerMediaClock);
        double origin = 1_800_000_000_000;
        double[] ticks = [origin, origin + 10, origin + 33, origin + 100, origin + 900,
            origin + 950, origin + 20_000, origin + 20_033, origin + 20_066, origin + 40_000];
        foreach (var tick in ticks)
        {
            reference.RenderFrame(canvas, tick);
            clock = clock.At(tick, count, fps);
            Assert.Equal(canvas.GetPixel(0, 0).r, (byte)clock.Frame);
        }
    }

    [Fact]
    public void Predicting_a_pending_frame_does_not_anchor_or_advance_the_published_clock()
    {
        var clock = default(AudioTriggerMediaClock);
        Assert.Equal(0, clock.At(10_000, 300, 30).Frame);
        Assert.False(clock.Started);
        var firstPublished = clock.At(20_000, 300, 30);
        Assert.Equal(0, firstPublished.Frame);
        Assert.Equal(1, firstPublished.At(20_034, 300, 30).Frame);
        Assert.Equal(0, firstPublished.Frame);
    }
}
