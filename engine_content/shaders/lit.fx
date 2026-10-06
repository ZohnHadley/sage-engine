// Meshes (docs/design/07 §3.2): techniques Default (sun + hemispheric ambient + up to four point lights
// + fog), AlphaTest (Default + clip at AlphaCutoff), Unlit (albedo + fog) and Skinned (Default, with the
// vertices bent by up to four of the draw's `Bones` each; issue #117). The sun is shadowed by the sun's
// shadow map in Default, AlphaTest and Skinned; ShadowCaster and ShadowCasterSkinned draw a caster into
// that map (issue 4h-4). Lightmapped (issue #313) is Default for a brush level with a baked lightmap.
#include "common.fxh"

texture Albedo;
sampler AlbedoSampler = sampler_state { Texture = <Albedo>; };
float4 AlbedoColor;     // multiplies the texture (premultiplied)
float AlphaCutoff;

struct VSInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 UV       : TEXCOORD0;   // meshes without UVs read (0,0)
};

struct VSOutput
{
    float4 Position : POSITION0;
    float3 Normal   : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float3 Relative : TEXCOORD2;   // camera-relative position
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    float4 relative = mul(input.Position, World);
    output.Position = mul(relative, ViewProj);
    output.Normal = mul(input.Normal, (float3x3)World);
    output.UV = input.UV;
    output.Relative = relative.xyz;
    return output;
}

// A skinned vertex (the client's VertexSkinned): the rigid vertex plus four joint indices and weights.
struct VSSkinnedInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 UV       : TEXCOORD0;
    float4 Indices  : BLENDINDICES0;
    float4 Weights  : BLENDWEIGHT0;
};

VSOutput VSSkinned(VSSkinnedInput input)
{
    VSOutput output;
    float4x3 skin = SkinMatrix(input.Indices, input.Weights);
    float4 relative = mul(SkinPosition(input.Position, skin), World);
    output.Position = mul(relative, ViewProj);
    output.Normal = mul(SkinNormal(input.Normal, skin), (float3x3)World);
    output.UV = input.UV;
    output.Relative = relative.xyz;
    return output;
}

float4 Shade(VSOutput input, float lit)
{
    float4 albedo = tex2D(AlbedoSampler, input.UV) * AlbedoColor * Tint;
    float3 n = normalize(input.Normal);
    float3 sun = SunLight(n) * ShadowLit(input.Relative, n);
    float3 light = lerp(float3(1, 1, 1), HemiAmbient(n) + sun + PointLights(n, input.Relative), lit);
    float3 color = ApplyFog(albedo.rgb * light, length(input.Relative));
    return float4(color, albedo.a);
}

float4 PSDefault(VSOutput input) : COLOR0
{
    return Shade(input, 1);
}

float4 PSAlphaTest(VSOutput input) : COLOR0
{
    float4 color = Shade(input, 1);
    AlphaTest(color.a, AlphaCutoff);
    return color;
}

float4 PSUnlit(VSOutput input) : COLOR0
{
    return Shade(input, 0);
}

// ---- Lightmapped (issue #313): a brush level's faces with their baked light ----
//
// The renderer draws a mesh that has a lightmap (VertexLightmapped: TEXCOORD1 is its place in the atlas)
// with this instead of Default, and sets `Lightmap` for the draw. The texture holds the baked lamps' light
// (rgb, times LIGHTMAP_SCALE: LevelLightmap.Scale) and how much sky the texel sees (a), which the ambient is
// multiplied by. The sun stays dynamic and shadowed (it moves), and the lamps that are not baked add on top:
// the renderer leaves the baked ones out of this draw's four.
#define LIGHTMAP_SCALE 4.0
texture Lightmap;
// Its own register, past the shadow map's (s1) and the surface maps' (s2..s5).
sampler LightmapSampler : register(s6) = sampler_state
{
    Texture = <Lightmap>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Clamp;
    AddressV = Clamp;
};

struct VSLightmappedInput
{
    float4 Position   : POSITION0;
    float3 Normal     : NORMAL0;
    float2 UV         : TEXCOORD0;
    float2 LightmapUV : TEXCOORD1;
};

struct VSLightmappedOutput
{
    float4 Position   : POSITION0;
    float3 Normal     : TEXCOORD0;
    float2 UV         : TEXCOORD1;
    float3 Relative   : TEXCOORD2;
    float2 LightmapUV : TEXCOORD3;
};

VSLightmappedOutput VSLightmapped(VSLightmappedInput input)
{
    VSLightmappedOutput output;
    float4 relative = mul(input.Position, World);
    output.Position = mul(relative, ViewProj);
    output.Normal = mul(input.Normal, (float3x3)World);
    output.UV = input.UV;
    output.Relative = relative.xyz;
    output.LightmapUV = input.LightmapUV;
    return output;
}

float4 PSLightmapped(VSLightmappedOutput input) : COLOR0
{
    float4 albedo = tex2D(AlbedoSampler, input.UV) * AlbedoColor * Tint;
    float3 n = normalize(input.Normal);
    float4 baked = tex2D(LightmapSampler, input.LightmapUV);
    float3 sun = SunLight(n) * ShadowLit(input.Relative, n);
    float3 light = HemiAmbient(n) * baked.a + baked.rgb * LIGHTMAP_SCALE + sun + PointLights(n, input.Relative);
    float3 color = ApplyFog(albedo.rgb * light, length(input.Relative));
    return float4(color, albedo.a);
}

// ---- Shadow casters (issue 4h-4): depth into the sun's map ----
//
// ViewProj is the sun's (the renderer sets it for these draws). The depth goes out through a
// TEXCOORD, as computed here: the map compares against the same number ShadowLit computes, whatever
// the platform later does to the position's z.
struct VSShadowOutput
{
    float4 Position : POSITION0;
    float Depth     : TEXCOORD0;
};

VSShadowOutput VSShadow(VSInput input)
{
    VSShadowOutput output;
    output.Position = mul(mul(input.Position, World), ViewProj);
    output.Depth = output.Position.z / output.Position.w;
    return output;
}

VSShadowOutput VSShadowSkinned(VSSkinnedInput input)
{
    VSShadowOutput output;
    float4x3 skin = SkinMatrix(input.Indices, input.Weights);
    output.Position = mul(mul(SkinPosition(input.Position, skin), World), ViewProj);
    output.Depth = output.Position.z / output.Position.w;
    return output;
}

float4 PSShadow(VSShadowOutput input) : COLOR0
{
    return float4(input.Depth, 0, 0, 1);
}

technique Default
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSDefault(); }
}

technique AlphaTest
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSAlphaTest(); }
}

technique Unlit
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSUnlit(); }
}

technique Skinned
{
    pass P0 { VertexShader = compile vs_3_0 VSSkinned(); PixelShader = compile ps_3_0 PSDefault(); }
}

technique Lightmapped
{
    pass P0 { VertexShader = compile vs_3_0 VSLightmapped(); PixelShader = compile ps_3_0 PSLightmapped(); }
}

technique ShadowCaster
{
    pass P0 { VertexShader = compile vs_3_0 VSShadow(); PixelShader = compile ps_3_0 PSShadow(); }
}

technique ShadowCasterSkinned
{
    pass P0 { VertexShader = compile vs_3_0 VSShadowSkinned(); PixelShader = compile ps_3_0 PSShadow(); }
}
