// Meshes (docs/design/07 §3.2): techniques Default (sun + hemispheric ambient + up to four point lights
// + fog), AlphaTest (Default + clip at AlphaCutoff), Unlit (albedo + fog) and Skinned (Default, with the
// vertices bent by up to four of the draw's `Bones` each; issue #117). The sun is shadowed by the sun's
// shadow map in Default, AlphaTest and Skinned; ShadowCaster and ShadowCasterSkinned draw a caster into
// that map (issue 4h-4).
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

technique ShadowCaster
{
    pass P0 { VertexShader = compile vs_3_0 VSShadow(); PixelShader = compile ps_3_0 PSShadow(); }
}

technique ShadowCasterSkinned
{
    pass P0 { VertexShader = compile vs_3_0 VSShadowSkinned(); PixelShader = compile ps_3_0 PSShadow(); }
}

// ---- Instanced meshes (issue 4n-5) ----
//
// With `r_instancing 1` a run of items sharing a mesh part, a material, a tint and their lamps is one
// draw: each instance's camera-relative world matrix comes in a second vertex stream (InstanceTransform,
// a row per TEXCOORD4..7) in place of `World`; Tint and the lights are the run's, set once. Each
// technique is its twin above with these vertex shaders (Instancing.TechniqueFor: Default's is
// `Instanced`, any other's `<name>Instanced`).
struct VSInstanceInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 UV       : TEXCOORD0;
    float4 World0   : TEXCOORD4;
    float4 World1   : TEXCOORD5;
    float4 World2   : TEXCOORD6;
    float4 World3   : TEXCOORD7;
};

float4x4 InstanceWorld(VSInstanceInput input)
{
    return float4x4(input.World0, input.World1, input.World2, input.World3);
}

VSOutput VSInstanced(VSInstanceInput input)
{
    VSOutput output;
    float4x4 world = InstanceWorld(input);
    float4 relative = mul(input.Position, world);
    output.Position = mul(relative, ViewProj);
    output.Normal = mul(input.Normal, (float3x3)world);
    output.UV = input.UV;
    output.Relative = relative.xyz;
    return output;
}

VSShadowOutput VSShadowInstanced(VSInstanceInput input)
{
    VSShadowOutput output;
    output.Position = mul(mul(input.Position, InstanceWorld(input)), ViewProj);
    output.Depth = output.Position.z / output.Position.w;
    return output;
}

technique Instanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSDefault(); }
}

technique AlphaTestInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSAlphaTest(); }
}

technique UnlitInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSUnlit(); }
}

technique ShadowCasterInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSShadowInstanced(); PixelShader = compile ps_3_0 PSShadow(); }
}
