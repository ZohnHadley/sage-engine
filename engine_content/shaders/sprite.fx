// Billboard sprites (docs/design/07 §3.2): the quads are expanded on the CPU (06 §3.7), so the
// vertex shader only transforms them. Techniques: Lit (alpha-tested, sun with its shadows, hemispheric
// ambient, the nearby lamps, fog), Unlit (alpha-tested, fog: full-bright) and UnlitBlend (transparent,
// no alpha test).
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

// ---- Instanced quads (issue 4n-5): the same sprites, one unit quad drawn once per sprite ----
//
// With `r_instancing 1` the renderer draws a long run as instances of one quad: the corner comes from the
// quad (0..1 across and down), the rest from the sprite's instance (SpriteInstanceVertex), which carries
// what the CPU path would have expanded into four vertices. Each technique is its twin above with this
// vertex shader (Instancing.TechniqueFor: `<name>Instanced`).
struct VSInstancedInput
{
    float3 Corner   : POSITION0;    // x across, y down, 0..1
    float3 Origin   : TEXCOORD4;    // the quad's top-left corner, camera-relative
    float3 AxisX    : TEXCOORD5;    // its top edge, left to right
    float3 AxisY    : TEXCOORD6;    // its left edge, top to bottom
    float3 Normal   : NORMAL0;
    float4 Frame    : TEXCOORD7;    // u0, v0, u1, v1
    float4 Color    : COLOR0;
};

VSOutput VSInstanced(VSInstancedInput input)
{
    VSOutput output;
    float3 position = input.Origin + input.AxisX * input.Corner.x + input.AxisY * input.Corner.y;
    output.Position = mul(float4(position, 1), ViewProj);
    output.Normal = input.Normal;
    output.UV = lerp(input.Frame.xy, input.Frame.zw, input.Corner.xy);
    output.Relative = position;
    output.Color = input.Color;
    return output;
}

technique UnlitInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSUnlit(); }
}

technique LitInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSLit(); }
}

technique UnlitBlendInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSUnlitBlend(); }
}
