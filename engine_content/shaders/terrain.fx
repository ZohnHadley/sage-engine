// Splat terrain (issue #307, docs/design/07 §11, 14 §3): technique Splat blends up to four layer textures
// by the weights each vertex carries (COLOR0: layer 0 in red, 1 in green, 2 in blue, 3 in alpha), each tiled
// at its own size, with a fine grey detail texture over the top; then lit as lit.fx's Default is (sun with
// its shadows, hemispheric ambient, up to four point lights, fog). ShadowCaster draws the ground into the
// sun's shadow map. A `terrain_material` record is drawn with it (`TerrainSplat.Params` sets the params);
// the weights are `TerrainSplat.Weights`, worked out on the CPU from each vertex's height and slope. Rain wets it
// and puddles it as lit.fx's surfaces (issue #311).
#include "common.fxh"

// Explicit registers: s1 is the shadow map's (common.fxh). Each layer wraps and filters on its own.
texture Layer0;
texture Layer1;
texture Layer2;
texture Layer3;
texture Detail;
sampler Layer0Sampler : register(s0) = sampler_state
{
    Texture = <Layer0>; MinFilter = Linear; MagFilter = Linear; MipFilter = Linear; AddressU = Wrap; AddressV = Wrap;
};
sampler Layer1Sampler : register(s2) = sampler_state
{
    Texture = <Layer1>; MinFilter = Linear; MagFilter = Linear; MipFilter = Linear; AddressU = Wrap; AddressV = Wrap;
};
sampler Layer2Sampler : register(s3) = sampler_state
{
    Texture = <Layer2>; MinFilter = Linear; MagFilter = Linear; MipFilter = Linear; AddressU = Wrap; AddressV = Wrap;
};
sampler Layer3Sampler : register(s4) = sampler_state
{
    Texture = <Layer3>; MinFilter = Linear; MagFilter = Linear; MipFilter = Linear; AddressU = Wrap; AddressV = Wrap;
};
sampler DetailSampler : register(s5) = sampler_state
{
    Texture = <Detail>; MinFilter = Linear; MagFilter = Linear; MipFilter = Linear; AddressU = Wrap; AddressV = Wrap;
};

float4 LayerTiling;     // repeats of each layer per UV unit (a mesh's UVs run one unit per 8 m cell)
float2 DetailParams;    // x = repeats of the detail texture per UV unit, y = its strength (0: none)

struct VSInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 UV       : TEXCOORD0;
    float4 Weights  : COLOR0;      // the four layers' weights, summing to about 1
};

struct VSOutput
{
    float4 Position : POSITION0;
    float3 Normal   : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float3 Relative : TEXCOORD2;   // camera-relative position
    float4 Weights  : TEXCOORD3;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    float4 relative = mul(input.Position, World);
    output.Position = mul(relative, ViewProj);
    output.Normal = mul(input.Normal, (float3x3)World);
    output.UV = input.UV;
    output.Relative = relative.xyz;
    output.Weights = input.Weights;
    return output;
}

float3 Splat(float2 uv, float4 weights)
{
    float4 w = weights / max(dot(weights, float4(1, 1, 1, 1)), 0.0001);
    float3 albedo = tex2D(Layer0Sampler, uv * LayerTiling.x).rgb * w.x;
    albedo += tex2D(Layer1Sampler, uv * LayerTiling.y).rgb * w.y;
    albedo += tex2D(Layer2Sampler, uv * LayerTiling.z).rgb * w.z;
    albedo += tex2D(Layer3Sampler, uv * LayerTiling.w).rgb * w.w;
    // Mid-grey detail leaves the colour as it is; lighter brightens and darker darkens.
    float detail = tex2D(DetailSampler, uv * DetailParams.x).r * 2;
    return albedo * lerp(1, detail, DetailParams.y);
}

float4 PSSplat(VSOutput input) : COLOR0
{
    float3 albedo = Splat(input.UV, input.Weights) * Tint.rgb;
    float3 n = normalize(input.Normal);
    float3 v = -normalize(input.Relative);   // towards the camera, at the origin

    // Rain (issue #311; WetnessRules, common.fxh `Wetting`): the ground is under the open sky, so it is as wet as
    // the world where it faces up, darker and with a sheen, and puddled where it is flat; a puddle is flat water.
    float2 wet = Wetting(input.Relative, n.y, 1, 1);
    albedo *= WetDarken(wet);
    float shadow = ShadowLit(input.Relative, n);
    n = normalize(lerp(n, float3(0, 1, 0), wet.y));

    float3 sun = SunLight(n) * shadow;
    float3 light = HemiAmbient(n) + sun + PointLights(n, input.Relative);
    float3 h = normalize(v - SunDir);
    float sheen = pow(saturate(dot(n, h)), exp2(1 + 10 * WetGloss(wet))) * saturate(dot(n, -SunDir)) * WetShine(wet);
    float3 color = albedo * light + SunColor * (shadow * sheen) + PuddleReflection(n, v, wet.y);
    return float4(ApplyFog(color, length(input.Relative)), Tint.a);
}

// ---- Shadow caster (issue 4h-4): depth into the sun's map, as lit.fx's ----
struct VSShadowOutput
{
    float4 Position : POSITION0;
    float2 Depth    : TEXCOORD0;   // z and w: divided per pixel (a lamp's perspective map; issue #315)
};

VSShadowOutput VSShadow(VSInput input)
{
    VSShadowOutput output;
    output.Position = mul(mul(input.Position, World), ViewProj);
    output.Depth = output.Position.zw;
    return output;
}

float4 PSShadow(VSShadowOutput input) : COLOR0
{
    return float4(input.Depth.x / input.Depth.y, 0, 0, 1);
}

technique Splat
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSSplat(); }
}

technique ShadowCaster
{
    pass P0 { VertexShader = compile vs_3_0 VSShadow(); PixelShader = compile ps_3_0 PSShadow(); }
}
