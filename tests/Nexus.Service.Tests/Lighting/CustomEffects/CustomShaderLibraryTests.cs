using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.CustomEffects;

public sealed class CustomShaderLibraryTests : IDisposable
{
    private readonly string _id = CustomFrags.NewId();
    private string Key => "custom:" + _id;

    public void Dispose() => ShaderLibrary.UnregisterCustom(_id);

    [Fact]
    public void Get_adds_shared_spatial_controls_without_the_builtin_prelude()
    {
        var frag = CustomFrags.Frag();
        ShaderLibrary.RegisterCustom(_id, frag);

        Assert.StartsWith("#version 330 core", ShaderLibrary.Get(Key));
        Assert.Contains("void main() { fragColor = vec4(vec3(u_b1_gain), 1.0); }", ShaderLibrary.Get(Key));
        Assert.DoesNotContain("vec3 finalize(", ShaderLibrary.Get(Key));
        var specs = ShaderLibrary.Params(Key);
        Assert.Equal(new ShaderParamSpec("u_background_rotation", 0, 360, 1, 0, true), specs["u_background_rotation"]);
        Assert.Equal(new ShaderParamSpec("u_background_zoom", 0.1f, 3, 0.05f, 1, true), specs["u_background_zoom"]);
    }

    [Fact]
    public void A_missing_custom_key_throws_and_is_not_known()
    {
        Assert.False(ShaderLibrary.IsKnownKey(Key));
        Assert.Throws<FileNotFoundException>(() => ShaderLibrary.Get(Key));
    }

    [Fact]
    public void Re_registering_invalidates_Get_Params_and_SourceTag()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(gainDefault: 0.5f));
        var tag1 = ShaderLibrary.SourceTag(Key);
        Assert.Equal(0.5f, ShaderLibrary.Params(Key)["u_b1_gain"].Default);

        var next = CustomFrags.Frag(gainDefault: 1.5f);
        ShaderLibrary.RegisterCustom(_id, next);

        Assert.Contains("= 1.5", ShaderLibrary.Get(Key));
        Assert.Equal(1.5f, ShaderLibrary.Params(Key)["u_b1_gain"].Default);
        Assert.NotEqual(tag1, ShaderLibrary.SourceTag(Key));
    }

    [Fact]
    public void Unregistering_drops_the_derived_data()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        _ = ShaderLibrary.Params(Key);

        ShaderLibrary.UnregisterCustom(_id);

        Assert.Throws<FileNotFoundException>(() => ShaderLibrary.Params(Key));
        Assert.Throws<FileNotFoundException>(() => ShaderLibrary.SourceTag(Key));
    }

    [Fact]
    public void Built_in_keys_are_unchanged()
    {
        Assert.True(ShaderLibrary.IsKnownKey("plasma"));
        Assert.False(ShaderLibrary.IsKnownKey("nope"));
        Assert.Contains("void main", ShaderLibrary.Get("plasma"));
    }

    [Fact]
    public void IsAudioEffect_follows_the_stored_source_for_custom_keys()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(audio: false));
        Assert.False(ShaderLibrary.IsAudioEffect(Key));

        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(audio: true));
        Assert.True(ShaderLibrary.IsAudioEffect(Key));

        ShaderLibrary.UnregisterCustom(_id);
        Assert.False(ShaderLibrary.IsAudioEffect(Key));
        Assert.True(ShaderLibrary.IsAudioEffect("spectrumbars"));
    }

    [Theory]
    [InlineData("uniform float u_audioLevel;", true)]
    [InlineData("uniform float u_audioBass;", true)]
    [InlineData("uniform float u_audioMid;", true)]
    [InlineData("uniform float u_audioHigh;", true)]
    [InlineData("uniform float u_audioBeat;", true)]
    [InlineData("uniform float u_spectrum[32];", true)]
    [InlineData("uniform float u_spectrum64[64];", true)]
    [InlineData("uniform vec4 u_specHist[8];", true)]
    [InlineData("uniform float u_bassPeak;", true)]
    [InlineData("uniform float u_midPeak;", true)]
    [InlineData("uniform float u_highPeak;", true)]
    [InlineData("uniform float u_audioBoost;", false)]
    [InlineData("uniform float u_audioLevelScale;", false)]
    [InlineData("uniform float my_u_spectrum;", false)]
    [InlineData("// u_audioLevel only in a comment", false)]
    [InlineData("/* u_spectrum */", false)]
    public void ReadsAudio_matches_whole_identifiers_the_effect_binds(string line, bool expected)
    {
        Assert.Equal(expected, ShaderLibrary.ReadsAudio(CustomFrags.Frag(extra: line + "\n")));
    }

    [Fact]
    public void Sanitize_clamps_a_custom_key_against_its_hint_ranges()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag());
        var state = new AnimateEffectState
        {
            Speed = 400,
            Params = new() { ["u_b1_gain"] = 9f, ["u_unknown"] = 7f, ["u_background_rotation"] = 900, ["u_background_zoom"] = 9 },
        };

        ShaderParamSpec.Sanitize(Key, state);

        Assert.Equal(2f, state.Params["u_b1_gain"]);
        Assert.Equal(7f, state.Params["u_unknown"]);
        Assert.Equal(200, state.Speed);
        Assert.Equal(360f, state.Params["u_background_rotation"]);
        Assert.Equal(3f, state.Params["u_background_zoom"]);
    }

    [Fact]
    public void Sanitize_leaves_an_unknown_custom_key_untouched()
    {
        var state = new AnimateEffectState { Params = new() { ["u_b1_gain"] = 9f } };

        ShaderParamSpec.Sanitize("custom:" + CustomFrags.NewId(), state);

        Assert.Equal(9f, state.Params["u_b1_gain"]);
    }
}
