using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>Adds shared spatial sampling to saved, self-contained custom fragments without changing their stored source.</summary>
internal static class CustomPatternShader
{
    // Accepted custom sources have one leading version directive and no further
    // preprocessor directives. Keep comments intact, including range/sensor annotations.
    private static readonly Regex Tokens = new(@"//[^\r\n]*|/\*[\s\S]*?(?:\*/|\z)|[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

    public static string Compose(string source)
    {
        var lineEnd = source.IndexOf('\n');
        if (lineEnd < 0)
        {
            return source;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match token in Tokens.Matches(source))
        {
            if (token.Value[0] != '/')
            {
                names.Add(token.Value);
            }
        }
        // An authored reserved name owns its axis, even if it is not a uniform.
        // Never redeclare it or apply a second spatial transform through it.
        var rotate = !names.Contains("u_background_rotation");
        var zoom = !names.Contains("u_background_zoom");
        if (!rotate && !zoom)
        {
            return source;
        }
        var helper = "nexusPatternCoord";
        while (names.Contains(helper))
        {
            helper += "_";
        }
        var body = Tokens.Replace(source[(lineEnd + 1)..], token => token.Value == "gl_FragCoord"
            ? helper + "(gl_FragCoord, u_resolution)"
            : token.Value);
        return source[..(lineEnd + 1)] + Transform(helper, rotate, zoom, names.Contains("u_resolution")) + body;
    }

    private static string Transform(string helper, bool rotate, bool zoom, bool hasResolution)
    {
        var uniforms = (rotate ? "uniform float u_background_rotation; // hint_range(0.0, 360.0, 1.0) = 0.0\n" : "")
            + (zoom ? "uniform float u_background_zoom; // hint_range(0.1, 3.0, 0.05) = 1.0\n" : "")
            + (hasResolution ? "" : "uniform vec2 u_resolution;\n");
        var transform = uniforms + $$"""
            vec4 {{helper}}(vec4 coord, vec2 resolution) {
                bool rotatePattern = {{(rotate ? "u_background_rotation > 0.0 && u_background_rotation < 360.0" : "false")}};
                bool zoomPattern = {{(zoom ? "u_background_zoom > 0.0 && u_background_zoom != 1.0" : "false")}};
                if (!rotatePattern && !zoomPattern) return coord;
                vec2 p = coord.xy - resolution * 0.5;
            """ + "\n";
        if (rotate)
        {
            transform += "    if (rotatePattern) {\n"
                + "        float a = radians(u_background_rotation);\n"
                + "        float c = cos(a), s = sin(a);\n"
                // gl_FragCoord is bottom-left-origin; this is the prelude's
                // clockwise, top-left-origin inverse sample in pixel space.
                + "        p = vec2(c * p.x - s * p.y, s * p.x + c * p.y);\n    }\n";
        }
        if (zoom)
        {
            transform += "    if (zoomPattern) p /= u_background_zoom;\n";
        }
        return transform + "    return vec4(p + resolution * 0.5, coord.zw);\n}\n";
    }
}
