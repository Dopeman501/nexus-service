using Nexus.Service.Media;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests;

public sealed class PanelBgFrameRateTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(61)]
    public async Task Commit_rejects_out_of_range_rate_before_reading_media(int maxVideoFps)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-bg-invalid-fps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await PanelBgImporter.CommitAsync(new PanelBgLibrary(root), "panel", "missing",
                new CropRect(0, 0, 1, 1), 8, 8, maxVideoFps: maxVideoFps);

            Assert.False(result.Ok);
            Assert.Equal("Video frame rate limit must be 1..60", result.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
