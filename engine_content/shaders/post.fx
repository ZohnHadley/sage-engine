// Post-processing (docs/design/06 "As built (post-processing)", issue 4h-6): the full-screen effects of
// the post_effect chain. Each step draws one quad already in clip space and reads the step before it
// as Source. The post_effects are LDR in, LDR out: exposure lives in the grade (decision 6). Techniques:
// Copy, Grade, Vignette; and the engine's own steps (issue #316): BloomPrefilter, BloomDown, BloomUp,
// Tonemap and Fxaa.
//
// The engine sets Source, SourceSize (width, height, 1/width, 1/height) and Night (0 by day, 1 at
// night, from the sky); for the tonemap also Bloom (the bloom chain's top level), BloomOn (0: no bloom)
// and Hdr (1: the scene is HDR, tonemap it; 0: clamp it). Every other parameter is the material's
// (engine_content/data/post.json). The curves are PostCurves (Sage.Simulation), line for line, and tested.

texture Source;
sampler SourceSampler : register(s0) = sampler_state { Texture = <Source>; };
float4 SourceSize;
float Night;

texture Bloom;
// Its own register (s0 keeps the material's sampler); linear, so the small levels upscale smoothly.
sampler BloomSampler : register(s1) = sampler_state
{
    Texture = <Bloom>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Point;
    AddressU = Clamp;
    AddressV = Clamp;
};
float BloomOn;
float Hdr;

float BloomThreshold;     // brightness (the largest channel) where the bloom starts
float BloomKnee;          // how soft that start is, either side of it
float BloomIntensity;     // how much of the bloom is added back
float HdrExposure;        // stops, before the tonemap curve
float FxaaSpan;           // the longest edge FXAA follows, in pixels

float Exposure;           // stops: +1 is twice as bright
float Contrast;           // 1: unchanged, around mid grey
float Saturation;         // 1: unchanged, 0: grey
float3 ColorFilter;       // multiplied in last
float3 NightColor;        // what the night tint pulls the picture toward, times its brightness
float NightStrength;      // how much of it at full night

float VignetteStrength;   // how dark the corners go (0: none)
float VignetteRadius;     // where the darkening starts, from the centre (in screen heights)
float VignetteSoftness;   // how far it takes to reach full strength

struct VSInput
{
    float4 Position : POSITION0;    // clip space
    float2 UV       : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float2 UV       : TEXCOORD0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = input.Position;
    output.UV = input.UV;
    return output;
}

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// ---- HDR, bloom and FXAA (issue #316) ----

// PostCurves.Aces: Narkowicz's fit of the ACES filmic curve.
float3 Aces(float3 x)
{
    x = max(x, 0);
    return saturate(x * (2.51 * x + 0.03) / (x * (2.43 * x + 0.59) + 0.14));
}

// PostCurves.BloomWeight: how much of a pixel the bloom keeps, by its brightness, with a soft knee.
float BloomWeight(float brightness)
{
    float soft = clamp(brightness - BloomThreshold + BloomKnee, 0, 2 * BloomKnee);
    soft = soft * soft / (4 * BloomKnee + 0.00001);
    return max(soft, brightness - BloomThreshold) / max(brightness, 0.00001);
}

// Four bilinear taps half a texel out on the diagonals: a 4x4 box of the source, for halving it.
float3 Box4(float2 uv)
{
    float2 d = SourceSize.zw;
    float3 c = tex2D(SourceSampler, uv + float2(-d.x, -d.y)).rgb;
    c += tex2D(SourceSampler, uv + float2(d.x, -d.y)).rgb;
    c += tex2D(SourceSampler, uv + float2(-d.x, d.y)).rgb;
    c += tex2D(SourceSampler, uv + float2(d.x, d.y)).rgb;
    return c * 0.25;
}

// The scene into bloom0 (half size): its bright parts only, capped so one hot pixel cannot flood it.
float4 PSBloomPrefilter(VSOutput input) : COLOR0
{
    float3 c = min(Box4(input.UV), 64);
    float brightness = max(c.r, max(c.g, c.b));
    return float4(c * BloomWeight(brightness), 1);
}

// One level into the next, half its size.
float4 PSBloomDown(VSOutput input) : COLOR0
{
    return float4(Box4(input.UV), 1);
}

// A level back up into the one above it (added there by the blend): a 3x3 tent over the smaller level.
float4 PSBloomUp(VSOutput input) : COLOR0
{
    float2 d = SourceSize.zw;
    float2 uv = input.UV;
    float3 c = tex2D(SourceSampler, uv).rgb * 4;
    c += (tex2D(SourceSampler, uv + float2(-d.x, 0)).rgb + tex2D(SourceSampler, uv + float2(d.x, 0)).rgb
        + tex2D(SourceSampler, uv + float2(0, -d.y)).rgb + tex2D(SourceSampler, uv + float2(0, d.y)).rgb) * 2;
    c += tex2D(SourceSampler, uv + float2(-d.x, -d.y)).rgb + tex2D(SourceSampler, uv + float2(d.x, -d.y)).rgb
       + tex2D(SourceSampler, uv + float2(-d.x, d.y)).rgb + tex2D(SourceSampler, uv + float2(d.x, d.y)).rgb;
    return float4(c / 16, 1);
}

// The scene plus the bloom into LDR: through the ACES curve after HdrExposure when the scene is HDR,
// clamped when it is not (bloom on an 8-bit scene).
float4 PSTonemap(VSOutput input) : COLOR0
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    c += BloomOn * BloomIntensity * tex2D(BloomSampler, input.UV).rgb;
    float3 mapped = Aces(c * exp2(HdrExposure));
    return float4(lerp(saturate(c), mapped, Hdr), 1);
}

// FXAA (Lottes' compact PC version): the luma of the four diagonal neighbours gives the edge's
// direction; two and four taps along it blend the edge, and the four-tap blend is kept unless it
// overshoots the neighbourhood (a corner), where the two-tap one is.
float4 PSFxaa(VSOutput input) : COLOR0
{
    float2 px = SourceSize.zw;
    float2 uv = input.UV;
    float3 rgbM = tex2D(SourceSampler, uv).rgb;
    float lumaNW = Luma(tex2D(SourceSampler, uv + float2(-px.x, -px.y)).rgb);
    float lumaNE = Luma(tex2D(SourceSampler, uv + float2(px.x, -px.y)).rgb);
    float lumaSW = Luma(tex2D(SourceSampler, uv + float2(-px.x, px.y)).rgb);
    float lumaSE = Luma(tex2D(SourceSampler, uv + float2(px.x, px.y)).rgb);
    float lumaM = Luma(rgbM);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    float2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y = (lumaNW + lumaSW) - (lumaNE + lumaSE);
    float reduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 / 8.0), 1.0 / 128.0);
    float rcpMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);
    dir = clamp(dir * rcpMin, -FxaaSpan, FxaaSpan) * px;

    float3 rgbA = 0.5 * (tex2D(SourceSampler, uv + dir * (1.0 / 3.0 - 0.5)).rgb
                       + tex2D(SourceSampler, uv + dir * (2.0 / 3.0 - 0.5)).rgb);
    float3 rgbB = rgbA * 0.5 + 0.25 * (tex2D(SourceSampler, uv - dir * 0.5).rgb
                                     + tex2D(SourceSampler, uv + dir * 0.5).rgb);
    float lumaB = Luma(rgbB);
    float outside = (lumaB < lumaMin || lumaB > lumaMax) ? 1 : 0;
    return float4(lerp(rgbB, rgbA, outside), 1);
}

float4 PSCopy(VSOutput input) : COLOR0
{
    return float4(tex2D(SourceSampler, input.UV).rgb, 1);
}

float4 PSGrade(VSOutput input) : COLOR0
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    c = c * exp2(Exposure);
    c = lerp(c, Luma(c) * NightColor, saturate(Night * NightStrength));
    float grey = Luma(c);
    c = lerp(float3(grey, grey, grey), c, Saturation);
    c = (c - 0.5) * Contrast + 0.5;
    c = c * ColorFilter;
    return float4(saturate(c), 1);
}

float4 PSVignette(VSOutput input) : COLOR0
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    float2 d = (input.UV - 0.5) * float2(SourceSize.x * SourceSize.w, 1);   // in screen heights: round on any aspect
    float v = smoothstep(VignetteRadius, VignetteRadius + VignetteSoftness, length(d));
    return float4(c * (1 - v * VignetteStrength), 1);
}

technique Copy
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSCopy(); }
}

technique Grade
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSGrade(); }
}

technique Vignette
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSVignette(); }
}

technique BloomPrefilter
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSBloomPrefilter(); }
}

technique BloomDown
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSBloomDown(); }
}

technique BloomUp
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSBloomUp(); }
}

technique Tonemap
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSTonemap(); }
}

technique Fxaa
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSFxaa(); }
}
