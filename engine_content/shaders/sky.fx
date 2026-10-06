// The sky (issue 4h-5, docs/design/06 "As built (sky and fog)"): drawn by `sage:sky` in the Sky stage as
// one triangle over the view at the far plane, depth-tested against what the view drew, so it fills only
// what nothing covers. The colour is `SkyRules.ColorAt` (Sage.Simulation), whose tests are this shader's:
// a gradient from the horizon to the zenith, fog's haze along the horizon, the sun disc; plus stars,
// hashed from the view direction, as much as `SkyRules.StarsAt` says. Issue 4n-16 adds a moon, a cloud layer
// and stars that turn with the hour (SkyRules' MoonDisc, CloudAlpha and RotateStars), and haze that can stay above the band.
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

// Issue 4n-16: the moon, the clouds and the turning stars. Each is the SkyRules function of the same name.
float4 SkyHaze;            // x = haze band, y = fog colour left above it (HazeAbove), z = star turn (radians), w = unused
float3 SkyStarAxis;        // the stars turn about it
float4 SkyMoon;            // xyz = direction toward the moon, w = angular radius (radians); w = 0: no moon
float4 SkyMoonLight;       // x = age (0 new, 0.5 full), y = level (0-1), z = gain, w = unused
float4 SkyCloud;           // x = cover (0 clear, 1 overcast; 0: no clouds), y = scale, zw = scroll
float3 SkyCloudColor;      // SkyRules.CloudColor

texture MoonTex;
sampler MoonSampler = sampler_state { Texture = <MoonTex>; AddressU = Clamp; AddressV = Clamp; MinFilter = Linear; MagFilter = Linear; };
texture CloudTex;
sampler CloudSampler = sampler_state { Texture = <CloudTex>; AddressU = Wrap; AddressV = Wrap; MinFilter = Linear; MagFilter = Linear; };

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

// SkyRules.RotateStars: the view direction in the stars' own frame.
float3 RotateStars(float3 d, float3 axis, float turn)
{
    float3 k = normalize(axis);
    float c = cos(-turn), s = sin(-turn);
    return d * c + cross(k, d) * s + k * (dot(k, d) * (1 - c));
}

// SkyRules.MoonDisc and MoonLit over the moon's picture: rgb, and the disc's coverage in a.
float4 MoonLight(float3 d)
{
    float3 m = normalize(SkyMoon.xyz);
    float c = dot(d, m);
    if (SkyMoon.w <= 0 || c <= 0) return 0;
    float3 right = cross(m, float3(0, 1, 0));
    right = dot(right, right) < 1e-6 ? float3(1, 0, 0) : normalize(right);
    float3 up = cross(right, m);
    float3 p = d / c - m;
    float2 local = float2(dot(p, right), dot(p, up)) / tan(SkyMoon.w);
    float r2 = dot(local, local);
    float cover = 1 - Smooth01((sqrt(r2) - 0.92) / 0.08);
    if (cover <= 0) return 0;
    float z = sqrt(max(1 - r2, 0));
    float alpha = SkyMoonLight.x * 6.2831853;
    float lit = Smooth01((local.x * sin(alpha) - z * cos(alpha)) * 5);
    float3 surface = tex2Dlod(MoonSampler, float4(local * float2(0.5, -0.5) + 0.5, 0, 0)).rgb;
    return float4(surface * (0.04 + lit) * SkyMoonLight.z * SkyMoonLight.y, cover);
}

float4 PS(VSOutput input) : COLOR0
{
    float4 far = mul(float4(input.Clip, 1, 1), SkyInvViewProj);
    float3 d = normalize(far.xyz / far.w);

    // SkyRules.Gradient
    float3 sky = lerp(SkyHorizon, SkyZenith, sqrt(saturate(d.y)));

    // SkyRules.CloudAlpha, CloudFade and CloudUv: the layer is a plane overhead, thresholded by coverage.
    float cloud = 0;
    if (SkyCloud.x > 0)
    {
        float2 uv = d.xz / (max(d.y, 0) + 0.2) * SkyCloud.y + SkyCloud.zw;
        float cs = tex2Dlod(CloudSampler, float4(uv, 0, 0)).r;
        cloud = Smooth01((cs - 1 + 1.3 * saturate(SkyCloud.x)) / 0.3) * Smooth01(d.y / 0.25);
        sky = lerp(sky, SkyCloudColor, cloud);
    }

    // SkyRules.Haze: fog's colour along the horizon, and what is left of it above the band.
    float haze = max(1 - Smooth01(d.y / max(SkyHaze.x, 0.001)), saturate(SkyHaze.y)) * SkyParams.y;
    sky = lerp(sky, SkyFog, haze);
    float c = dot(d, -SkySunDir);
    float disc = Smooth01((c - SkyDisc.x) / max(SkyDisc.y - SkyDisc.x, 0.00001));
    sky += SkySunColor * (SkyDisc.z * disc * (1 - 0.9 * cloud));

    // The moon over the sky, behind cloud and haze.
    float4 moon = MoonLight(d);
    sky = lerp(sky, moon.rgb, moon.a * (1 - 0.9 * cloud) * (1 - haze));

    // Stars turn with the hour, above the haze only, and clouds hide them.
    float above = Smooth01(d.y / max(SkyHaze.x, 0.001)) * (1 - saturate(SkyHaze.y));
    sky += StarLight(RotateStars(d, SkyStarAxis, SkyHaze.z)) * SkyParams.x * above * (1 - 0.9 * cloud);
    return float4(sky, 1);
}

technique Sky
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
}
