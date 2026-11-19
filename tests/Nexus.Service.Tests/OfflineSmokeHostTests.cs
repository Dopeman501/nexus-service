#if OFFLINE_SMOKE
using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests;

public sealed class OfflineSmokeHostTests
{
    [Theory]
    [InlineData("GET", "/", true)]
    [InlineData("GET", "/assets/index-hash.js", true)]
    [InlineData("GET", "/background-transitions/demo.jpg", true)]
    [InlineData("GET", "/lighting/audio-trigger", true)]
    [InlineData("GET", "/lighting/animate/settings", true)]
    [InlineData("GET", "/monitoring", false)]
    [InlineData("GET", "/devices", false)]
    [InlineData("GET", "/service/stop", false)]
    [InlineData("POST", "/lighting/audio-trigger", false)]
    [InlineData("POST", "/service/factory-reset", false)]
    [InlineData("GET", "/assets/../monitoring", false)]
    [InlineData("GET", "/assets/%2e%2e/monitoring", false)]
    [InlineData("GET", "/assets/..\\monitoring", false)]
    public void Probe_rejects_any_request_that_could_bind_hardware_dependencies(string method, string path, bool allowed)
        => Assert.Equal(allowed, OfflineSmokeHost.Allows(method, path));

    [Theory]
    [InlineData("--install")]
    [InlineData("--install-pawnio")]
    [InlineData("http://0.0.0.0:59483")]
    [InlineData("http://localhost:9400")]
    [InlineData("https://localhost:59483")]
    [InlineData("http://localhost:59483/devices")]
    public void Probe_rejects_lifecycle_and_non_private_origins_before_startup(string argument)
        => Assert.Throws<InvalidOperationException>(() => OfflineSmokeHost.Prepare([argument]));
}
#endif
