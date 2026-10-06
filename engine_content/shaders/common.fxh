// Shared shader contract (docs/design/07 §3.5). Every engine and game effect includes this, so all
// effects bind the same frame and object parameters and light/fog the same way.
//
// Parameter tiers (07 §3.4), set by the renderer; materials never set these:
//   frame:  ViewProj, SunDir, SunColor, AmbientSky, AmbientGround, FogColor, FogParams, Time,
//           ShadowViewProj, ShadowParams, ShadowMap (the sun's shadow map, issue 4h-4),
//           ShadowCascadeRects, ShadowCascadeBias (its cascades, issue 4n-11)
//   object: World, Tint, and for skinned draws Bones
//   material (set from the material record, with FogEnabled from the record's "fog"):
//           FogEnabled + whatever the effect declares (Albedo, AlphaCutoff...)
// OpenGL ignores default values in .fx files: every parameter is set by the engine or a material.
//
// Positions are camera-relative (06 §3.3): World has the camera position subtracted and ViewProj
// has no translation, so length(position) is the distance to the camera.

// ---- Frame ----
float4x4 ViewProj;
float3 SunDir;          // direction the sunlight travels (normalized)
float3 SunColor;
float3 AmbientSky;
float3 AmbientGround;
float3 FogColor;
float4 FogParams;       // x = start, y = end (metres), z = 1 if fog is on for this view,
                        // w = 0 for linear fog, else exp²'s density scaled for exp2() (issue 4h-5)
float Time;

// ---- Object ----
float4x4 World;
float4 Tint;            // premultiplied colour multiplier

// ---- Object: the point lights this draw is lit by (06 §3.9) ----
//
// Four, chosen per object by `LightRules.Nearest` before the draw. Camera-relative like everything else,
// so the distance in here is the distance in the vertex shader's `Relative` without any more maths.
// `LightCount` is how many of the four are real; the rest are not cleared, so nothing may read past it.
#define MAX_LIGHTS 4
float3 LightPositions[MAX_LIGHTS];
float4 LightColors[MAX_LIGHTS];     // rgb = colour x intensity, a = range in metres
float LightCount;

// ---- Object: a skinned draw's joint palette (issue #117) ----
//
// Joint j's bind space → model space, as 4x3 (the last column of a row-vector affine matrix is always
// 0,0,0,1, so it is not sent): 64 of them are 192 of vs_3_0's 256 constant registers. Set per draw by
// the renderer from RenderSnapshot.Bones, only for the `Skinned` technique; `SkinMath` (Sage.Simulation)
// is the same maths on the CPU, and its tests are this function's.
#define MAX_BONES 64
float4x3 Bones[MAX_BONES];

// The vertex's skin matrix: its four joints' palette entries blended by its weights (which the loader
// makes sum to one).
float4x3 SkinMatrix(float4 indices, float4 weights)
{
    float4x3 skin = Bones[(int)indices.x] * weights.x;
    skin += Bones[(int)indices.y] * weights.y;
    skin += Bones[(int)indices.z] * weights.z;
    skin += Bones[(int)indices.w] * weights.w;
    return skin;
}

// A bind-space position into model space; World then places the model as for any mesh.
float4 SkinPosition(float4 position, float4x3 skin)
{
    return float4(mul(position, skin), 1);
}

// A bind-space normal into model space (rotation only; joints are rigid). Normalised by the pixel shader.
float3 SkinNormal(float3 normal, float4x3 skin)
{
    return mul(normal, (float3x3)skin);
}

// ---- Material ----
float FogEnabled;       // 1 or 0, from the material's "fog"

float3 HemiAmbient(float3 n)
{
    return lerp(AmbientGround, AmbientSky, n.y * 0.5 + 0.5);
}

float3 SunLight(float3 n)
{
    return SunColor * saturate(dot(n, -SunDir));
}

// What the nearby lamps add. Falloff is (1 - d/range) squared: nothing outside the range, and a curve
// that looks like light rather than like a cone. Lambert against the surface normal, the same as the
// sun, so a wall facing away from a lamp stays dark and a room reads as a room.
float3 PointLights(float3 n, float3 relative)
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
        float lambert = saturate(dot(n, toLight / max(distance, 0.001)));
        sum += LightColors[i].rgb * (falloff * falloff * lambert);
    }

    return sum;
}

// How much fog covers a point `distance` metres away: `FogMath.Factor` (Sage.Simulation), whose tests are
// this function's. Linear from start to end; exp² past the start when FogParams.w > 0. Pixel shader only,
// so no vertex shader gains a constant.
float FogFactor(float distance)
{
    float x = max(distance - FogParams.x, 0);
    float linearFog = saturate(x / max(FogParams.y - FogParams.x, 0.001));
    float k = FogParams.w * x;
    float exp2Fog = saturate(1 - exp2(-k * k));
    return FogParams.w > 0 ? exp2Fog : linearFog;
}

float3 ApplyFog(float3 color, float distance)
{
    return lerp(color, FogColor, FogFactor(distance) * FogParams.z * FogEnabled);
}

void AlphaTest(float alpha, float cutoff)
{
    clip(alpha - cutoff);
}

// ---- Frame: the sun's shadow map (issues 4h-4, 4n-11; docs/design/06 "As built (sun shadows)") ----
//
// Up to three cascades of light-space depth in [0, 1] (R32F, cleared to 1), side by side in one target,
// drawn by `sage:shadow` with the ShadowCaster techniques. `ShadowViewProj[k]` takes a camera-relative
// position (this view's camera) into cascade k's clip space and `ShadowCascadeRects[k]` says where that
// cascade sits in the target; `ShadowMath` (Sage.Simulation) is where they come from, and its tests are
// this map's (`CascadeOf` is the choice below). Read in the pixel shader from `Relative`, so it costs the
// vertex shader no constants (the skinned one has 64 bones in 192 of its 256). Strength 0 — no map this
// frame, r_shadows 0, the sun down — makes whatever is sampled not matter.
#define MAX_CASCADES 3
float4x4 ShadowViewProj[MAX_CASCADES];
float4 ShadowCascadeRects[MAX_CASCADES];   // uv in the target: x, y, width, height
float4 ShadowCascadeBias;                  // x, y, z: each cascade's depth bias
float4 ShadowParams;    // x = strength (0: none), y = cascades, z = texels a side of each, w = 1 / that
texture ShadowMap;
// Its own register, so an effect's own sampler (Albedo) keeps s0 and the material's sampler state.
// Point sampling: the 2x2 filter below compares depths, then blends the answers; blending depths is wrong.
sampler ShadowSampler : register(s1) = sampler_state
{
    Texture = <ShadowMap>;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = Point;
    AddressU = Clamp;
    AddressV = Clamp;
};

// Cascade k, if no nearer one has taken the point: where the point falls on it (uv, depth), its rectangle
// and bias, kept in `chosen*` when it holds the point with a texel to spare for the filter.
void ShadowCascade(float3 relative, float4x4 viewProj, float4 rect, float bias, float exists,
                   inout float3 chosen, inout float4 chosenRect, inout float chosenBias, inout float found)
{
    float4 clip = mul(float4(relative, 1), viewProj);
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    float2 inside2 = step(ShadowParams.w, uv) * step(uv, 1 - ShadowParams.w);
    float take = inside2.x * inside2.y * step(0, clip.z) * step(clip.z, 1) * exists * (1 - found);
    chosen = lerp(chosen, float3(uv, clip.z), take);
    chosenRect = lerp(chosenRect, rect, take);
    chosenBias = lerp(chosenBias, bias, take);
    found = max(found, take);
}

// How much of the sun reaches a camera-relative point on a surface facing `n`: 1 lit, 0 shadowed.
// The first cascade that holds the point (the nearest are the finest), then a manual 2x2 PCF: the four
// texels around the point each say lit or not, and the answers are blended by where the point falls
// between them, so a shadow edge is a texel-wide ramp rather than a staircase.
float ShadowLit(float3 relative, float3 n)
{
    float3 chosen = float3(0.5, 0.5, 0);
    float4 rect = float4(0, 0, 1, 1);
    float bias = 0;
    float found = 0;
    ShadowCascade(relative, ShadowViewProj[0], ShadowCascadeRects[0], ShadowCascadeBias.x, 1, chosen, rect, bias, found);
    ShadowCascade(relative, ShadowViewProj[1], ShadowCascadeRects[1], ShadowCascadeBias.y, step(1.5, ShadowParams.y), chosen, rect, bias, found);
    ShadowCascade(relative, ShadowViewProj[2], ShadowCascadeRects[2], ShadowCascadeBias.z, step(2.5, ShadowParams.y), chosen, rect, bias, found);

    // More margin as the surface turns from the sun: its depth changes faster across a texel.
    float depth = chosen.z - bias * (2.0 - saturate(dot(n, -SunDir)));

    float2 texel = chosen.xy * ShadowParams.z - 0.5;
    float2 corner = floor(texel);
    float2 f = texel - corner;
    float2 scale = ShadowParams.w * rect.zw;   // one texel of the cascade, in the target's uv
    float2 at = rect.xy + (corner + 0.5) * scale;
    float2 dx = float2(scale.x, 0);
    float2 dy = float2(0, scale.y);
    float lit00 = step(depth, tex2D(ShadowSampler, at).r);
    float lit10 = step(depth, tex2D(ShadowSampler, at + dx).r);
    float lit01 = step(depth, tex2D(ShadowSampler, at + dy).r);
    float lit11 = step(depth, tex2D(ShadowSampler, at + dx + dy).r);
    float lit = lerp(lerp(lit00, lit10, f.x), lerp(lit01, lit11, f.x), f.y);

    // Off every cascade (sideways, or past either end of its depth) is lit: the maps cover the near slice of the view only.
    return lerp(1, lerp(1, lit, found), ShadowParams.x);
}
