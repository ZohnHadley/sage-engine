// Meshes (docs/design/07 §3.2): techniques Default (sun + hemispheric ambient + up to four point lights
// + fog), AlphaTest (Default + clip at AlphaCutoff), Unlit (albedo + fog) and Skinned (Default, with the
// vertices bent by up to four of the draw's `Bones` each; issue #117). The sun is shadowed by the sun's
// shadow map in Default, AlphaTest and Skinned; ShadowCaster and ShadowCasterSkinned draw a caster into
// that map (issue 4h-4).
//
// The surface (issue #410), in every technique that shades: a tangent-space normal map, Blinn-Phong
// highlights from the sun and the lamps (strength and gloss, times a specular map's red and green), an
// emissive map times an emissive colour, the mesh's vertex colours, and a reflected panorama. The material
// record's surface fields set them (`MaterialSurface`); a map it does not name is a 1x1 texture that
// changes nothing, so there is one technique for all of it and no permutations.
#include "common.fxh"

#define PI 3.14159265

// Explicit registers, as terrain.fx: s1 is the shadow map's (common.fxh). Albedo's sampler state is the
// material's (slot 0); the surface maps' are too (the renderer sets slots 2 to 5 to it).
texture Albedo;
sampler AlbedoSampler : register(s0) = sampler_state { Texture = <Albedo>; };
texture NormalMap;
sampler NormalSampler : register(s2) = sampler_state { Texture = <NormalMap>; };
texture SpecularMap;
sampler SpecularSampler : register(s3) = sampler_state { Texture = <SpecularMap>; };
texture EmissiveMap;
sampler EmissiveSampler : register(s4) = sampler_state { Texture = <EmissiveMap>; };
texture EnvironmentMap;
sampler EnvironmentSampler : register(s5) = sampler_state { Texture = <EnvironmentMap>; };

float4 AlbedoColor;     // multiplies the texture (premultiplied)
float AlphaCutoff;
float4 SurfaceParams;   // x = highlight strength, y = gloss (0..1), z = reflectivity, w = 1 to use vertex colours
float3 EmissiveColor;   // linear rgb, times the emissive map

struct VSInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 UV       : TEXCOORD0;   // meshes without UVs read (0,0)
    float4 Tangent  : TANGENT0;    // w = the bitangent's sign; a mesh without tangents reads (0,0,0,1)
    float4 Color    : COLOR0;      // a mesh without colours reads (0,0,0,1): used only when SurfaceParams.w is 1
};

struct VSOutput
{
    float4 Position : POSITION0;
    float3 Normal   : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float3 Relative : TEXCOORD2;   // camera-relative position
    float4 Tangent  : TEXCOORD3;
    float4 Color    : TEXCOORD4;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    float4 relative = mul(input.Position, World);
    output.Position = mul(relative, ViewProj);
    output.Normal = mul(input.Normal, (float3x3)World);
    output.UV = input.UV;
    output.Relative = relative.xyz;
    output.Tangent = float4(mul(input.Tangent.xyz, (float3x3)World), input.Tangent.w);
    output.Color = input.Color;
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
    float4 Tangent  : TANGENT0;
    float4 Color    : COLOR0;
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
    output.Tangent = float4(mul(SkinNormal(input.Tangent.xyz, skin), (float3x3)World), input.Tangent.w);
    output.Color = input.Color;
    return output;
}

// The normal at a pixel: the normal map's, turned out of the tangent frame. A mesh without tangents has a
// zero tangent, so only the map's z survives and the mesh's own normal comes back; so does a flat map.
float3 SurfaceNormal(VSOutput input)
{
    float3 n = normalize(input.Normal);
    float3 t = input.Tangent.xyz - n * dot(n, input.Tangent.xyz);
    t *= rsqrt(max(dot(t, t), 1e-8));
    float3 b = cross(n, t) * (input.Tangent.w < 0 ? -1 : 1);
    float3 m = tex2D(NormalSampler, input.UV).xyz * 2 - 1;
    return normalize(t * m.x + b * m.y + n * max(m.z, 0.001));
}

// Blinn-Phong: how much of a light arriving along `toLight` (unit) is reflected towards the eye `v`.
float Highlight(float3 n, float3 v, float3 toLight, float power)
{
    float3 h = normalize(toLight + v);
    return pow(saturate(dot(n, h)), power) * saturate(dot(n, toLight));
}

// PointLights (common.fxh) with highlights: diffuse out as the return value, highlights in `specular`.
float3 PointLightsLit(float3 n, float3 v, float3 relative, float power, out float3 specular)
{
    float3 sum = float3(0, 0, 0);
    specular = float3(0, 0, 0);

    for (int i = 0; i < MAX_LIGHTS; i++)
    {
        if (i >= LightCount) break;

        float3 toLight = LightPositions[i] - relative;
        float distance = length(toLight);
        float range = LightColors[i].a;
        if (distance >= range) continue;

        float3 l = toLight / max(distance, 0.001);
        float falloff = 1.0 - distance / max(range, 0.001);
        float3 c = LightColors[i].rgb * (falloff * falloff * SpotCone(i, -l));   // a spot's cone (issue #314)
        sum += c * saturate(dot(n, l));
        specular += c * Highlight(n, v, l, power);
    }

    return sum;
}

// The panorama's texel a direction sees: longitude across (-Z in the middle), the sky at the top.
float3 Environment(float3 r)
{
    float2 uv = float2(atan2(r.x, -r.z) * (0.5 / PI) + 0.5, acos(clamp(r.y, -1, 1)) * (1 / PI));
    // tex2D, not tex2Dlod: GLSL 1.10 has no lod lookup in a pixel shader. Longitude wraps at the back, where
    // a mipmapped panorama may show a one-pixel seam.
    return tex2D(EnvironmentSampler, uv).rgb;
}

float4 Shade(VSOutput input, float lit)
{
    float4 albedo = tex2D(AlbedoSampler, input.UV) * AlbedoColor * Tint;
    albedo *= lerp(float4(1, 1, 1, 1), input.Color, SurfaceParams.w);
    float3 n = SurfaceNormal(input);
    float3 v = -normalize(input.Relative);   // towards the camera, which is at the origin

    // Highlights: strength times the map's red, tightness from gloss times its green (2 to 2048).
    float4 spec = tex2D(SpecularSampler, input.UV);
    float strength = SurfaceParams.x * spec.r;
    float power = exp2(1 + 10 * saturate(SurfaceParams.y * spec.g));
    float3 emissive = tex2D(EmissiveSampler, input.UV).rgb * EmissiveColor;
    float3 reflection = Environment(reflect(-v, n)) * (SurfaceParams.z * spec.r);   // the samples before the lamps' loop

    float shadow = ShadowLit(input.Relative, n);
    float3 sun = SunLight(n) * shadow;
    float3 lampSpecular;
    float3 lamps = PointLightsLit(n, v, input.Relative, power, lampSpecular);
    float3 specular = (SunColor * shadow * Highlight(n, v, -SunDir, power) + lampSpecular) * strength;

    float3 light = lerp(float3(1, 1, 1), HemiAmbient(n) + sun + lamps, lit);
    float3 color = albedo.rgb * light + (specular + reflection) * (lit * albedo.a) + emissive * albedo.a;
    return float4(ApplyFog(color, length(input.Relative)), albedo.a);
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
    float4 Tangent  : TANGENT0;    // as VSInput (issue #410)
    float4 Color    : COLOR0;
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
    output.Tangent = float4(mul(input.Tangent.xyz, (float3x3)world), input.Tangent.w);
    output.Color = input.Color;
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
