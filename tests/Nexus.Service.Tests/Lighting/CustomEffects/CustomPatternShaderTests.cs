using Nexus.Service.Lighting.Engine.Gpu;

namespace Nexus.Service.Tests.Lighting.CustomEffects;

public sealed class CustomPatternShaderTests
{
    [Fact]
    public void Legacy_fragcoord_reads_gain_the_shared_controls_and_remain_led_rewritable()
    {
        var raw = CustomFrags.Frag(extra: "vec2 pos() { return gl_FragCoord.xy / u_resolution; }\n");
        var composed = CustomPatternShader.Compose(raw);
        Assert.StartsWith("#version 330 core\n", composed);
        Assert.Contains("return nexusPatternCoord(gl_FragCoord, u_resolution).xy / u_resolution;", composed);
        Assert.Contains("if (!rotatePattern && !zoomPattern) return coord;", composed);
        Assert.Contains("coord.zw", composed);
        Assert.NotNull(LedWindowShader.Rewrite(composed));
        Assert.Equal(composed, CustomPatternShader.Compose(composed));
    }

    [Fact]
    public void Comments_and_identifier_substrings_are_preserved_and_helper_collisions_are_avoided()
    {
        const string comment = "// gl_FragCoord u_background_rotation\n/* u_background_zoom gl_FragCoord */\n";
        var raw = CustomFrags.Frag(extra: comment + "float nexusPatternCoord = 0.0;\nfloat my_gl_FragCoord = 1.0;\nvec2 pos() { return gl_FragCoord.xy; }\n");
        var composed = CustomPatternShader.Compose(raw);
        Assert.Contains(comment, composed);
        Assert.Contains("float my_gl_FragCoord = 1.0;", composed);
        Assert.Contains("vec4 nexusPatternCoord_(", composed);
        Assert.Contains("return nexusPatternCoord_(gl_FragCoord, u_resolution).xy;", composed);
        Assert.True(ShaderParamSpec.Parse(composed).ContainsKey("u_background_rotation"));
    }

    [Fact]
    public void Authored_reserved_names_retain_their_ranges_and_own_transform()
    {
        var raw = CustomFrags.Frag(extra: "uniform float u_background_rotation; // hint_range(-90, 90, 1) = 45\n"
            + "uniform float u_background_zoom; // hint_range(1, 8, 1) = 2\n");
        Assert.Equal(raw, CustomPatternShader.Compose(raw));
    }

    [Theory]
    [InlineData("u_background_rotation", "u_background_zoom")]
    [InlineData("u_background_zoom", "u_background_rotation")]
    public void One_reserved_symbol_leaves_that_axis_owned_and_adds_the_other(string authored, string added)
    {
        var raw = CustomFrags.Frag(extra: $"float {authored} = 1.0;\n");
        var composed = CustomPatternShader.Compose(raw);
        Assert.DoesNotContain($"uniform float {authored};", composed);
        Assert.Contains($"float {authored} = 1.0;", composed);
        Assert.Contains($"uniform float {added};", composed);
    }

    [Fact]
    public void A_coordinate_shader_without_a_resolution_declaration_receives_the_host_resolution()
    {
        var raw = "#version 330 core\nout vec4 fragColor;\nvoid main(){fragColor=gl_FragCoord;}\n";
        Assert.Contains("uniform vec2 u_resolution;", CustomPatternShader.Compose(raw));
    }

    [Fact]
    public void Missing_version_line_ending_passes_through_for_compile_validation()
    {
        const string raw = "#version 330 core";
        Assert.Equal(raw, CustomPatternShader.Compose(raw));
    }
}
