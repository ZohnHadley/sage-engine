// Shared shader contract (docs/design/07 §3.5). Every engine and game effect includes this, so all
// effects bind the same frame and object parameters and light/fog the same way.
//
// Parameter tiers (07 §3.4), set by the renderer; materials never set these:
//   frame:  ViewProj, SunDir, SunColor, AmbientSky, AmbientGround, FogColor, FogParams, Time
//   object: World, Tint
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
float4 FogParams;       // x = start, y = end (metres), z = 1 if fog is on for this view
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

float3 ApplyFog(float3 color, float distance)
{
    float f = saturate((distance - FogParams.x) / max(FogParams.y - FogParams.x, 0.001));
    return lerp(color, FogColor, f * FogParams.z * FogEnabled);
}

void AlphaTest(float alpha, float cutoff)
{
    clip(alpha - cutoff);
}
