// Billboard sprites (docs/design/07 §3.2): the quads are expanded on the CPU (06 §3.7), so the
// vertex shader only transforms them. Techniques: Unlit (alpha-tested, fog — the Daggerfall look),
// Lit (alpha-tested, sun + hemispheric ambient, fog) and UnlitBlend (transparent, no alpha test).
#include "common.fxh"

texture Albedo;
sampler AlbedoSampler = sampler_state { Texture = <Albedo>; };
float4 AlbedoColor;
float AlphaCutoff;

struct VSInput
{
    float4 Position : POSITION0;    // already camera-relative world space
    float3 Normal   : NORMAL0;      // faces the camera (per sprite)
    float2 UV       : TEXCOORD0;
    float4 Color    : COLOR0;       // per-sprite tint (premultiplied)
};

struct VSOutput
{
    float4 Position : POSITION0;
    float3 Normal   : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float3 Relative : TEXCOORD2;
    float4 Color    : COLOR0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, ViewProj);
    output.Normal = input.Normal;
    output.UV = input.UV;
    output.Relative = input.Position.xyz;
    output.Color = input.Color;
    return output;
}

float4 Shade(VSOutput input, float lit)
{
    float4 albedo = tex2D(AlbedoSampler, input.UV) * AlbedoColor * input.Color * Tint;
    float3 n = normalize(input.Normal);
    float3 light = lerp(float3(1, 1, 1), HemiAmbient(n) + SunLight(n), lit);
    float3 color = ApplyFog(albedo.rgb * light, length(input.Relative));
    return float4(color, albedo.a);
}

float4 PSUnlit(VSOutput input) : COLOR0
{
    float4 color = Shade(input, 0);
    AlphaTest(color.a, AlphaCutoff);
    return color;
}

float4 PSLit(VSOutput input) : COLOR0
{
    float4 color = Shade(input, 1);
    AlphaTest(color.a, AlphaCutoff);
    return color;
}

float4 PSUnlitBlend(VSOutput input) : COLOR0
{
    return Shade(input, 0);
}

technique Unlit
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSUnlit(); }
}

technique Lit
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSLit(); }
}

technique UnlitBlend
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSUnlitBlend(); }
}
