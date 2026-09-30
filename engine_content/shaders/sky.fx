// The sky (issue 4h-5, docs/design/06 "As built (sky and fog)"): drawn by `sage:sky` in the Sky stage as
// one triangle over the view at the far plane, depth-tested against what the view drew, so it fills only
// what nothing covers. The colour is `SkyRules.ColorAt` (Sage.Simulation), whose tests are this shader's:
// a gradient from the horizon to the zenith, fog's haze along the horizon, the sun disc; plus stars,
// hashed from the view direction, as much as `SkyRules.StarsAt` says.
//
// Its own parameters, not common.fxh's: nothing else draws with them, and the lit effects keep their
// constants. All are set by the renderer every draw (OpenGL ignores defaults).

float4x4 SkyInvViewProj;   // clip space → the camera-relative direction (the view has no translation)
float3 SkyHorizon;         // RenderEnvironment.ClearColor
float3 SkyZenith;
float3 SkyFog;             // the fog colour, which the horizon hazes to
float3 SkySunDir;          // the way the light travels
float3 SkySunColor;        // zero at night
float4 SkyParams;          // x = stars (0-1), y = fog on (0/1), z = haze band (sin of elevation), w = time (s)
float4 SkyDisc;            // x = cos of the disc's outer edge, y = its inner edge, z = gain, w = unused

struct VSInput
{
    float4 Position : POSITION0;   // already in clip space, z = w: the far plane
};

struct VSOutput
{
    float4 Position : POSITION0;
    float2 Clip     : TEXCOORD0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = float4(input.Position.xy, 1, 1);
    output.Clip = input.Position.xy;
    return output;
}

float Smooth01(float x)
{
    x = saturate(x);
    return x * x * (3 - 2 * x);
}

// A number in [0, 1) from a cell of the sky.
float Hash(float3 cell)
{
    return frac(sin(dot(cell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
}

// Stars: one in a few hundred cells of a grid on the view direction lights, at its own brightness,
// twinkling a little, faded out near the horizon where the haze is.
float StarLight(float3 d)
{
    float3 cell = floor(d * 180);
    float h = Hash(cell);
    float star = step(0.9965, h);
    float3 centre = (cell + 0.5) / 180;
    float spot = saturate(1 - length(d - normalize(centre)) * 400);
    float twinkle = 0.75 + 0.25 * sin(SkyParams.w * (1.5 + h * 3) + h * 40);
    return star * spot * twinkle * (0.4 + 0.6 * frac(h * 97));
}

float4 PS(VSOutput input) : COLOR0
{
    float4 far = mul(float4(input.Clip, 1, 1), SkyInvViewProj);
    float3 d = normalize(far.xyz / far.w);

    // SkyRules.Gradient, Haze and SunDisc
    float3 sky = lerp(SkyHorizon, SkyZenith, sqrt(saturate(d.y)));
    float haze = (1 - Smooth01(d.y / max(SkyParams.z, 0.001))) * SkyParams.y;
    sky = lerp(sky, SkyFog, haze);
    float c = dot(d, -SkySunDir);
    float disc = Smooth01((c - SkyDisc.x) / max(SkyDisc.y - SkyDisc.x, 0.00001));
    sky += SkySunColor * (SkyDisc.z * disc);

    // Stars, above the haze only.
    float above = Smooth01(d.y / max(SkyParams.z, 0.001));
    sky += StarLight(d) * SkyParams.x * above;
    return float4(sky, 1);
}

technique Sky
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
}
