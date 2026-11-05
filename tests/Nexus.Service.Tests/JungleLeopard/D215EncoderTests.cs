using System.Buffers.Binary;
using Nexus.Service.Peripherals.JungleLeopard;
using SkiaSharp;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215EncoderTests
{
    [Fact]
    public void Real_encoder_turns_landscape_input_counterclockwise_into_portrait_jpeg()
    {
        const int width = 1680;
        const int height = 756;
        using var frames = D215BgraFrames.Create(() => false, rotateLandscape: true);
        var frame = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = y < height / 2
                    ? (x < width / 2 ? SKColors.Red : SKColors.Lime)
                    : (x < width / 2 ? SKColors.Blue : SKColors.Yellow);
                var offset = (y * width + x) * 4;
                frame[offset] = color.Blue;
                frame[offset + 1] = color.Green;
                frame[offset + 2] = color.Red;
                frame[offset + 3] = 255;
            }
        }

        frames.Queue(frame);

        var envelope = frames.TakeLatestEnvelope().Span;
        Assert.False(envelope.IsEmpty);
        Assert.Equal((uint)(envelope.Length - 6), BinaryPrimitives.ReadUInt32LittleEndian(envelope));
        using var image = TestImages.Decode(envelope[4..^2].ToArray());
        Assert.Equal(height, image.Width);
        Assert.Equal(width, image.Height);
        AssertColor(SKColors.Lime, image.GetPixel(height / 4, width / 4));
        AssertColor(SKColors.Yellow, image.GetPixel(height * 3 / 4, width / 4));
        AssertColor(SKColors.Red, image.GetPixel(height / 4, width * 3 / 4));
        AssertColor(SKColors.Blue, image.GetPixel(height * 3 / 4, width * 3 / 4));
    }

    [Fact]
    public void Real_encoder_preserves_portrait_geometry_and_frame_order()
    {
        const int width = 756;
        const int height = 1680;
        using var frames = D215BgraFrames.Create(() => false);
        var frame = new byte[width * height * 4];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            frame[offset + (pixel < width * height / 2 ? 2 : 0)] = 255;
            frame[offset + 3] = 255;
        }

        frames.Queue(frame);

        var envelope = frames.TakeLatestEnvelope().Span;
        Assert.False(envelope.IsEmpty);
        Assert.Equal((uint)(envelope.Length - 6), BinaryPrimitives.ReadUInt32LittleEndian(envelope));
        using var image = TestImages.Decode(envelope[4..^2].ToArray());
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        var top = image.GetPixel(width / 2, height / 4);
        var bottom = image.GetPixel(width / 2, height * 3 / 4);
        Assert.InRange(top.Red, 240, 255);
        Assert.InRange(top.Blue, 0, 15);
        Assert.InRange(bottom.Blue, 240, 255);
        Assert.InRange(bottom.Red, 0, 15);
    }

    private static void AssertColor(SKColor expected, SKColor actual)
    {
        Assert.InRange((int)actual.Red, expected.Red - 15, expected.Red + 15);
        Assert.InRange((int)actual.Green, expected.Green - 15, expected.Green + 15);
        Assert.InRange((int)actual.Blue, expected.Blue - 15, expected.Blue + 15);
    }
}
