// Billboard sprites (docs/design/07 §3.2): the quads are expanded on the CPU (06 §3.7), so the
// vertex shader only transforms them. Techniques: Lit (alpha-tested, sun with its shadows, hemispheric
// ambient, the nearby lamps, fog), Unlit (alpha-tested, fog: full-bright) and UnlitBlend (transparent,
// no alpha test). ShadowCasterAlphaTest draws an alpha-tested sprite into the sun's shadow map, turned to
// the sun and clipped at AlphaCutoff, so a leaf billboard casts a cut-out shadow (issue 4n-11).
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

// A billboard has no real normal: it always faces the camera, so plain Lambert would leave it dark
// whenever a light is off to its side (the sun overhead, a lamp beside it). Wrapped (half-Lambert)
// lighting lets a light anywhere but straight behind it reach it, strongest from the front.
float Wrap(float3 n, float3 toLight)
{
    return saturate(dot(n, toLight) * 0.5 + 0.5);
}

float3 SpritePointLights(float3 n, float3 relative)
{
    float3 sum = float3(0, 0, 0);

    for (int i = 0; i < MAX_LIGHTS; i++)
    {
        if (i >= LightCount) break;

        float3 toLight = LightPositions[i] - relative;
        float distance = length(toLight);
        float range = LightColors[i].a;
        if (distance >= range) continue;

        float falloff = 1.0 - distance / max(range, 0.001);
        sum += LightColors[i].rgb * (falloff * falloff * Wrap(n, toLight / max(distance, 0.001)));
    }

    return sum;
}

float4 Shade(VSOutput input, float lit)
{
    float4 albedo = tex2D(AlbedoSampler, input.UV) * AlbedoColor * input.Color * Tint;
    float3 n = normalize(input.Normal);
    float3 sun = SunColor * Wrap(n, -SunDir) * ShadowLit(input.Relative, n);
    float3 light = lerp(float3(1, 1, 1), HemiAmbient(n) + sun + SpritePointLights(n, input.Relative), lit);
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

// ---- Shadow caster (issue 4n-11): ViewProj is the cascade's; the quads were turned to the sun ----
struct VSShadowOutput
{
    float4 Position : POSITION0;
    float Depth     : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float Alpha     : TEXCOORD2;
};

VSShadowOutput VSShadow(VSInput input)
{
    VSShadowOutput output;
    output.Position = mul(input.Position, ViewProj);
    output.Depth = output.Position.z / output.Position.w;
    output.UV = input.UV;
    output.Alpha = input.Color.a;
    return output;
}

float4 PSShadow(VSShadowOutput input) : COLOR0
{
    AlphaTest(tex2D(AlbedoSampler, input.UV).a * AlbedoColor.a * input.Alpha * Tint.a, AlphaCutoff);
    return float4(input.Depth, 0, 0, 1);
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

technique ShadowCasterAlphaTest
{
    pass P0 { VertexShader = compile vs_3_0 VSShadow(); PixelShader = compile ps_3_0 PSShadow(); }
}
