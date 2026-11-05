using System.Buffers.Binary;
using Nexus.Service.Peripherals.JungleLeopard;
using SkiaSharp;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215EncoderTests
{
    [Theory]
    [InlineData(0, 1, 3, 0, 2)]
    [InlineData(90, 0, 1, 2, 3)]
    [InlineData(180, 2, 0, 3, 1)]
    [InlineData(270, 3, 2, 1, 0)]
    public void All_four_rotation_settings_produce_independent_golden_quadrants(
        int rotation, int topLeft, int topRight, int bottomLeft, int bottomRight)
    {
        var portrait = rotation is 90 or 270;
        var width = portrait ? 756 : 1680;
        var height = portrait ? 1680 : 756;
        using var frames = D215BgraFrames.Create(() => true, rotateLandscape: !portrait);
        frames.BindFlip180(() => rotation >= 180);
        SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
        var frame = Quadrants(width, height, colors);

        frames.Queue(frame);
        var envelope = frames.TakeLatestEnvelope().Span;

        using var image = TestImages.Decode(envelope[4..^2].ToArray());
        Assert.Equal((756, 1680), (image.Width, image.Height));
        AssertColor(colors[topLeft], image.GetPixel(189, 420));
        AssertColor(colors[topRight], image.GetPixel(567, 420));
        AssertColor(colors[bottomLeft], image.GetPixel(189, 1260));
        AssertColor(colors[bottomRight], image.GetPixel(567, 1260));
        Assert.Equal(Quadrants(width, height, colors), frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotation_changes_reencode_the_retained_canonical_frame_without_new_writes(bool landscape)
    {
        var mayRepeat = true;
        using var frames = D215BgraFrames.Create(() => mayRepeat, rotateLandscape: landscape);
        var flip180 = false;
        frames.BindFlip180(() => flip180);
        SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
        frames.Queue(Quadrants(landscape ? 1680 : 756, landscape ? 756 : 1680, colors));
        var original = frames.TakeLatestEnvelope();
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);

        flip180 = true;
        mayRepeat = false;
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        mayRepeat = true;
        var rotated = frames.TakeLatestEnvelope();
        Assert.False(rotated.IsEmpty);
        using var image = TestImages.Decode(rotated.Span[4..^2].ToArray());
        AssertColor(landscape ? SKColors.Blue : SKColors.Yellow, image.GetPixel(189, 420));
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);

        flip180 = false;
        Assert.Equal(original.ToArray(), frames.TakeLatestEnvelope().ToArray());
        Assert.Equal(0, frames.DroppedFrames);
    }

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

    private static byte[] Quadrants(int width, int height, SKColor[] colors)
    {
        var frame = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = colors[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
                var offset = (y * width + x) * 4;
                frame[offset] = color.Blue;
                frame[offset + 1] = color.Green;
                frame[offset + 2] = color.Red;
                frame[offset + 3] = 255;
            }
        }
        return frame;
    }
}
