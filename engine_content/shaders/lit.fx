// Meshes (docs/design/07 §3.2): techniques Default (sun + hemispheric ambient + fog), AlphaTest
// (Default + clip at AlphaCutoff) and Unlit (albedo + fog). Point lights come later (06 §3.9).
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

float4 Shade(VSOutput input, float lit)
{
    float4 albedo = tex2D(AlbedoSampler, input.UV) * AlbedoColor * Tint;
    float3 n = normalize(input.Normal);
    float3 light = lerp(float3(1, 1, 1), HemiAmbient(n) + SunLight(n), lit);
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
