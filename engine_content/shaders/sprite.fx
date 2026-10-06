// Billboard sprites (docs/design/07 §3.2): the quads are expanded on the CPU (06 §3.7), so the
// vertex shader only transforms them. Techniques: Lit (alpha-tested, sun with its shadows, hemispheric
// ambient, the nearby lamps, fog), Unlit (alpha-tested, fog: full-bright) and UnlitBlend (transparent,
// no alpha test). ShadowCasterAlphaTest draws an alpha-tested sprite into the sun's shadow map, turned to
// the sun and clipped at AlphaCutoff, so a leaf billboard casts a cut-out shadow (issue 4n-11).
// UnlitBlendSoft is UnlitBlend faded where it meets the scene's depth: soft particles (issue 4n-6).
#include "common.fxh"

texture Albedo;
sampler AlbedoSampler : register(s0) = sampler_state { Texture = <Albedo>; };
float4 AlbedoColor;
float AlphaCutoff;

// Soft particles (issue 4n-6; SoftParticles, tested): the scene's depth (`sage:depth`, z/w per pixel, the
// depth hook of issue #316), where this view draws in it, and the fade. Set by the renderer per run; only
// the Soft techniques read them. s1 is the shadow map's (common.fxh).
texture SceneDepth;
sampler SceneDepthSampler : register(s2) = sampler_state
{
    Texture = <SceneDepth>;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = Point;
    AddressU = Clamp;
    AddressV = Clamp;
};
float4 SoftParams;   // x = 1 / the soft distance (metres), y = near plane, z = far plane, w = 1 orthographic
float4 SoftRect;     // the view's rectangle in sage:depth as uv: x, y, width, height

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
    float4 Clip     : TEXCOORD3;    // Position again, for the Soft techniques' depth lookup
    float4 Color    : COLOR0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, ViewProj);
    output.Normal = input.Normal;
    output.UV = input.UV;
    output.Relative = input.Position.xyz;
    output.Clip = output.Position;
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

// The lamps near a sprite (issue #314: spot lights too), wrapped: `LightRules.Sum(..., wrapped: true)`.
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
        float3 l = toLight / max(distance, 0.001);
        sum += LightColors[i].rgb * (falloff * falloff * Wrap(n, l) * SpotCone(i, -l));
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

// z/w (Direct3D's 0..1, as the engine's projections make it) back to metres along the view:
// PostCurves.LinearDepth, as depth_haze.fx's LinearDepth with this view's planes.
float SoftLinearDepth(float d)
{
    float n = SoftParams.y;
    float f = SoftParams.z;
    float perspective = n * f / (f - d * (f - n));
    float orthographic = n + d * (f - n);
    return lerp(perspective, orthographic, SoftParams.w);
}

// UnlitBlend, faded by the metres between it and the scene behind it (SoftParticles.Fade, DepthUv). The
// whole colour is scaled, so a premultiplied (AlphaBlend) particle fades out and an additive one dims.
float4 PSUnlitBlendSoft(VSOutput input) : COLOR0
{
    float4 color = Shade(input, 0);
    float2 ndc = input.Clip.xy / input.Clip.w;
    float2 uv = SoftRect.xy + (ndc * float2(0.5, -0.5) + 0.5) * SoftRect.zw;
    float scene = SoftLinearDepth(tex2D(SceneDepthSampler, uv).r);
    float particle = SoftLinearDepth(input.Clip.z / input.Clip.w);
    return color * saturate((scene - particle) * SoftParams.x);
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

technique UnlitBlendSoft
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSUnlitBlendSoft(); }
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
    output.Clip = output.Position;
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

technique UnlitBlendSoftInstanced
{
    pass P0 { VertexShader = compile vs_3_0 VSInstanced(); PixelShader = compile ps_3_0 PSUnlitBlendSoft(); }
}

technique ShadowCasterAlphaTest
{
    pass P0 { VertexShader = compile vs_3_0 VSShadow(); PixelShader = compile ps_3_0 PSShadow(); }
}
