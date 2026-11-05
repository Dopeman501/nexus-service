using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.JungleLeopard;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215FrameRateBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Manual")]
    public async Task Measure_system_timer_pacing_with_a_retained_frame_and_fake_wire()
    {
        var wire = new TimingWire();
        var timing = D215Timing.Default with { ClearSettle = TimeSpan.Zero };
        var process = Process.GetCurrentProcess();
        var cpuAt = process.TotalProcessorTime;
        var stream = new D215LiveStream(new D215Wire(wire, TimeProvider.System, timing),
            new RetainedFrame(), () => true, TimeProvider.System, timing);
        try
        {
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(4));
            await Task.Delay(TimeSpan.FromSeconds(2.2));
            stream.RequestStop();
            await stream.Completion.WaitAsync(TimeSpan.FromSeconds(4));
            var times = wire.Frames.ToArray();
            Assert.True(times.Length > 2);
            var gaps = times.Zip(times.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).Order().ToArray();
            var elapsed = Stopwatch.GetElapsedTime(times[0], times[^1]);
            output.WriteLine($"Real-clock fake-wire requested_fps=60; frames={times.Length}; elapsed_ms={elapsed.TotalMilliseconds:F1}; "
                + $"achieved_fps={(times.Length - 1) / elapsed.TotalSeconds:F2}; gap_mean_ms={gaps.Average():F3}; "
                + $"gap_p95_ms={gaps[(int)(gaps.Length * .95)]:F3}; gap_max_ms={gaps[^1]:F3}; "
                + $"process_cpu_ms={(process.TotalProcessorTime - cpuAt).TotalMilliseconds:F1}");
            Assert.Null(stream.Failure);
        }
        finally
        {
            stream.Abort();
            await stream.Completion.WaitAsync(TimeSpan.FromSeconds(4));
        }
        output.WriteLine("Real TimeProvider.System on this Windows test host; fake instantaneous USB writes, retained tiny JPEG, no browser capture or encoding.");
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void Measure_complete_frame_conversion_without_renderer_or_usb()
    {
        using var nativeProbe = new BgraJpegEncoder(756, 1680);
        output.WriteLine($"TurboJPEG active={nativeProbe.IsNative}; geometry=756x1680; quality=85/70/55; one latest-frame queue");
        Assert.True(nativeProbe.IsNative);
        foreach (var angle in new[] { 0, 90, 180, 270 })
        {
            var landscape = angle is 0 or 180;
            var frame = Pattern(landscape ? 1680 : 756, landscape ? 756 : 1680);
            using var frames = D215BgraFrames.Create(() => false, rotateLandscape: landscape);
            frames.BindFlip180(() => angle >= 180);
            for (var warmup = 0; warmup < 5; warmup++)
            {
                frames.Queue(frame);
                Assert.False(frames.TakeLatestEnvelope().IsEmpty);
            }

            const int count = 60;
            var durations = new double[count];
            long bytes = 0;
            var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();
            for (var index = 0; index < count; index++)
            {
                frame[0] = (byte)index;
                var started = Stopwatch.GetTimestamp();
                frames.Queue(frame);
                var envelope = frames.TakeLatestEnvelope();
                durations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                bytes += envelope.Length;
                Assert.False(envelope.IsEmpty);
            }
            elapsed.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuStart;
            Array.Sort(durations);
            output.WriteLine($"angle={angle}; frames={count}; mean_ms={durations.Average():F3}; p50_ms={durations[count / 2]:F3}; "
                + $"p95_ms={durations[(int)(count * .95)]:F3}; max_ms={durations[^1]:F3}; "
                + $"mean_envelope_bytes={bytes / count}; cpu_ms_per_frame={cpu.TotalMilliseconds / count:F3}; "
                + $"throughput_fps={count / elapsed.Elapsed.TotalSeconds:F1}; dropped={frames.DroppedFrames}");
        }
        output.WriteLine("Offline conversion only: excludes WebView2 capture, shared-memory handoff, serial writes, firmware rendering and display latency.");
    }

    private static byte[] Pattern(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                var tile = ((x / 24) + (y / 24)) % 2;
                frame[at] = (byte)(x * 255 / width);
                frame[at + 1] = (byte)(y * 255 / height);
                frame[at + 2] = (byte)(tile == 0 ? 80 : 200);
                frame[at + 3] = 255;
            }
        }
        return frame;
    }

    private sealed class RetainedFrame : ID215FrameSource
    {
        private byte[]? _frame = Convert.FromHexString("04000000FFD8FFD9B303");
        public bool MayRepeat => true;
        public ReadOnlyMemory<byte> TakeLatestEnvelope() => Interlocked.Exchange(ref _frame, null) ?? ReadOnlyMemory<byte>.Empty;
    }

    private sealed class TimingWire : ID215SerialTransport
    {
        private readonly Channel<byte[]> _reply = Channel.CreateUnbounded<byte[]>();
        internal ConcurrentQueue<long> Frames { get; } = new();
        public ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length == 7 && source.Span[0] == 0x55 && source.Span[4] == 0x11)
                _reply.Writer.TryWrite(Convert.FromHexString("55AA080011001801"));
            else if (source.Length > 7) Frames.Enqueue(Stopwatch.GetTimestamp());
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var bytes = await _reply.Reader.ReadAsync(cancellationToken);
            bytes.AsSpan().CopyTo(destination.Span);
            return bytes.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
