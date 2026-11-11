using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Profiles;

public sealed class AudioTriggerControlEpochTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexus-audio-epoch-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Profile_creation_and_switch_rotate_runtime_identity_and_preserve_device_controls()
    {
        Directory.CreateDirectory(_directory);
        using var store = new JsonConfigStore(Path.Combine(_directory, "settings.json"));
        using var profiles = new ProfileManager(store);
        profiles.Initialize();
        var first = profiles.GetActiveEntry()!.Id;
        store.Update(settings => settings.Lighting.AudioTrigger = new()
        {
            Looks = [new() { Mode = "static", Color = "#ff0000" }],
            DeviceOverrides = new() { ["fan"] = new() { Enabled = false, Intensity = 12 } },
        });
        profiles.SaveActiveProfile();
        var beforeCreation = store.Load().AudioTriggerControlEpoch;
        profiles.CreateProfile("Second");
        Assert.NotEqual(beforeCreation, store.Load().AudioTriggerControlEpoch);
        store.Update(settings => settings.Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity = 90);
        profiles.SaveActiveProfile();
        var beforeSwitch = store.Load().AudioTriggerControlEpoch;
        profiles.SwitchProfile(first);
        Assert.NotEqual(beforeSwitch, store.Load().AudioTriggerControlEpoch);
        Assert.Equal(12, store.Load().Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity);
        Assert.False(store.Load().Lighting.AudioTrigger!.DeviceOverrides!["fan"].Enabled);
        store.FlushNow();
        Assert.DoesNotContain("audioTriggerControlEpoch", File.ReadAllText(store.SettingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
