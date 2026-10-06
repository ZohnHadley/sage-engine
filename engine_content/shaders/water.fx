// Water surfaces and the underwater view (issue #411): the post chain's water step, drawn after the views
// over `sage:scene` with this frame's `sage:depth` (the depth hook, issue #316). See Renderer.Water.cs for
// why it is a post step, and WaterShading (Sage.Simulation) for the curves: `WaveNormal`, `Fresnel`,
// `DepthFog`, `ShoreFade` and the underwater fog are its functions of the same names, line for line, and
// tested there.
//
// Per pixel: the main view's ray meets the nearest surface's plane in front of what the scene drew.
// From above, the surface shows the bottom (bent by the ripples, fogged by the water the ray crosses),
// reflects the shore by a short march through the depth buffer and the sky where that finds nothing,
// glints in the sun, and fades out where the bottom comes up to the waterline. From below, it shows what
// is above, bent. With the camera under water (WaterUnderTint.w) everything is tinted and fogged by the
// metres of water the ray crosses.
//
// Every Water* parameter is set by the engine (Renderer.SetWaterParams); the three Reflection ones are the
// material's (engine_content/data/water.json, sage:post_water). Positions are relative to the camera.

texture Source;
sampler SourceSampler : register(s0) = sampler_state { Texture = <Source>; };

texture SceneDepth;
sampler DepthSampler : register(s1) = sampler_state
{
    Texture = <SceneDepth>;
    MinFilter = Point;
    MagFilter = Point;
    MipFilter = Point;
    AddressU = Clamp;
    AddressV = Clamp;
};

float4x4 WaterInvViewProj;   // the main view's clip space -> camera-relative
float4x4 WaterViewProj;
float4 WaterCamera;          // xyz: the camera (origin space), for the ripples' place; w: time (s)
float4 WaterViewport;        // the main view's rectangle in the picture: u0, v0, u1, v1
float WaterCount;            // surfaces in the arrays below, nearest first (at most 4)
float4 WaterRect[4];         // footprint, camera-relative: min x, min z, max x, max z
float4 WaterLevel[4];        // x: surface height, y: floor (camera-relative); z: fog density; w: shore fade
float4 WaterColour[4];       // rgb: the water's colour; w: reflectivity
float4 WaterWaves[4];        // xy: the ripples' direction (x, z); z: wave length; w: strength
float4 WaterMore[4];         // x: wave speed; y: refraction; z: specular; w: unused
float3 WaterHorizon;         // the sky the surface reflects (sky.fx's gradient and haze)
float3 WaterZenith;
float3 WaterFog;
float3 WaterSunDir;          // the way the light travels
float3 WaterSunColour;
float4 WaterSky;             // x: the horizon hazes to the fog colour (0/1); y: the haze band
float3 WaterLight;           // WaterShading.Light: what the water's own colour is lit by
float4 WaterUnder;           // rgb: the underwater colour, lit; w: its density
float4 WaterUnderTint;       // rgb: the underwater tint; w: 1 with the camera under water

float ReflectionDistance;    // the material's: how far the reflected ray is marched on screen (m)
float ReflectionThickness;   // how far behind what the screen shows the ray may pass and still hit it (m)
float ScreenReflections;     // 1: march the screen for the shore; 0: the sky only

struct VSInput
{
    float4 Position : POSITION0;    // clip space
    float2 UV       : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float2 UV       : TEXCOORD0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = input.Position;
    output.UV = input.UV;
    return output;
}

static const float Far = 1e9;   // "nothing there": the sky, a miss

float Smooth01(float x)
{
    x = saturate(x);
    return x * x * (3 - 2 * x);
}

// A point of the main view (its own 0..1 across, top-left first) at depth z/w, camera-relative.
float3 Unproject(float2 viewUv, float d)
{
    float2 clip = float2(viewUv.x * 2 - 1, 1 - viewUv.y * 2);
    float4 p = mul(float4(clip, d, 1), WaterInvViewProj);
    return p.xyz / p.w;
}

float2 ToScreen(float2 viewUv) { return WaterViewport.xy + viewUv * (WaterViewport.zw - WaterViewport.xy); }
float2 ToView(float2 uv) { return (uv - WaterViewport.xy) / max(WaterViewport.zw - WaterViewport.xy, 0.00001); }

float3 Picture(float2 uv) { return tex2Dlod(SourceSampler, float4(uv, 0, 0)).rgb; }
float DepthAt(float2 uv) { return tex2Dlod(DepthSampler, float4(uv, 0, 0)).r; }

// WaterShading.Normal: four travelling sine waves, turned from the look's direction, each a share of its
// length and strength, moving at its speed times the square root of that share.
float2 Ripple(float2 dir, float turn, float scale, float share, float2 p, float4 waves, float speed, float t)
{
    float c = cos(turn);
    float s = sin(turn);
    float2 d = float2(dir.x * c - dir.y * s, dir.x * s + dir.y * c);
    float k = 6.2831853 / (waves.z * scale);
    float phase = k * (dot(d, p) - speed * sqrt(scale) * t);
    return d * (waves.w * share * cos(phase));
}

float3 WaveNormal(float4 waves, float speed, float2 p, float t)
{
    float2 g = Ripple(waves.xy, 0.0, 1.0, 0.45, p, waves, speed, t)
             + Ripple(waves.xy, 0.55, 0.57, 0.3, p, waves, speed, t)
             + Ripple(waves.xy, -0.8, 0.33, 0.15, p, waves, speed, t)
             + Ripple(waves.xy, 1.9, 0.21, 0.1, p, waves, speed, t);
    return normalize(float3(-g.x, 1, -g.y));
}

// WaterShading.Fresnel: Schlick's, for water (0.02 head-on), scaled by the reflectivity.
float Fresnel(float cosine, float reflectivity)
{
    float m = 1 - saturate(cosine);
    float m2 = m * m;
    return reflectivity * (0.02 + 0.98 * m2 * m2 * m);
}

// WaterShading.DepthFog: how much of what is behind `metres` of water its colour hides.
float DepthFog(float density, float metres)
{
    return 1 - exp(-max(density, 0) * max(metres, 0));
}

// WaterShading.ShoreFade: none of the surface at the waterline, all of it from `shore` metres deep.
float ShoreFade(float shore, float depth)
{
    float hard = depth > 0 ? 1 : 0;
    return shore > 0 ? Smooth01(depth / shore) : hard;
}

// The sky in direction `d`: sky.fx's gradient, hazed to the fog colour along the horizon.
float3 SkyColour(float3 d)
{
    float3 sky = lerp(WaterHorizon, WaterZenith, sqrt(saturate(d.y)));
    float haze = (1 - Smooth01(d.y / max(WaterSky.y, 0.001))) * WaterSky.x;
    return lerp(sky, WaterFog, haze);
}

// WaterShading.Hit: how far along the ray from `o` (unit `dir`) it meets a surface's top inside its
// footprint, from above or below; Far when it misses.
float HitPlane(float4 rect, float height, float3 o, float3 dir)
{
    float dy = abs(dir.y) < 0.000001 ? 0.000001 : dir.y;
    float t = (height - o.y) / dy;
    float3 p = o + dir * t;
    bool inside = t > 0 && p.x >= rect.x && p.x <= rect.z && p.z >= rect.y && p.z <= rect.w;
    return inside ? t : Far;
}

// What the reflected ray from `from` along `r` sees: something on screen (the shore) where a march
// through the depth buffer finds it, faded into the sky toward the picture's edges; else the sky.
float3 Reflection(float3 from, float3 r, float3 sky)
{
    if (ScreenReflections < 0.5) return sky;
    float3 result = sky;
    [loop] for (int i = 1; i <= 16; i++)
    {
        float along = ReflectionDistance * (i * i) / 256.0;     // denser near the surface
        float3 p = from + r * along;
        float4 c = mul(float4(p, 1), WaterViewProj);
        if (c.w <= 0.0001) break;
        float2 viewUv = float2(c.x / c.w * 0.5 + 0.5, 0.5 - c.y / c.w * 0.5);
        if (viewUv.x < 0 || viewUv.x > 1 || viewUv.y < 0 || viewUv.y > 1) break;
        float2 uv = ToScreen(viewUv);
        float d = DepthAt(uv);
        // Past what the screen shows there by less than the thickness (and the step): a hit. The sky
        // there (d = 1) never is: the ray goes on.
        float behind = length(p) - length(Unproject(viewUv, min(d, 0.99999)));
        float stride = ReflectionDistance * (2 * i - 1) / 256.0;
        if (d < 0.99999 && behind > 0 && behind < ReflectionThickness + stride)
        {
            float2 edge = min(viewUv, 1 - viewUv);
            float fade = Smooth01(min(edge.x, edge.y) * 10);
            result = lerp(sky, Picture(uv), fade);
            break;
        }
    }
    return result;
}

float4 PSWater(VSOutput input) : COLOR0
{
    float2 uv = input.UV;
    float3 scene = tex2D(SourceSampler, uv).rgb;
    float2 viewUv = ToView(uv);
    if (viewUv.x < 0 || viewUv.x > 1 || viewUv.y < 0 || viewUv.y > 1) return float4(scene, 1);

    float d = DepthAt(uv);
    float3 origin = Unproject(viewUv, 0);
    float3 dir = normalize(Unproject(viewUv, 1) - origin);
    bool sky = d >= 0.99999;
    float3 scenePoint = Unproject(viewUv, d);
    float sceneDist = sky ? Far : dot(scenePoint - origin, dir);

    // The nearest surface in front of what the scene drew.
    float best = Far;
    float4 level = 0;
    float4 colour = 0;
    float4 waves = 0;
    float4 more = 0;
    [unroll] for (int i = 0; i < 4; i++)
    {
        float t = HitPlane(WaterRect[i], WaterLevel[i].x, origin, dir);
        bool nearer = i < WaterCount && t < best && t < sceneDist;
        best = nearer ? t : best;
        level = nearer ? WaterLevel[i] : level;
        colour = nearer ? WaterColour[i] : colour;
        waves = nearer ? WaterWaves[i] : waves;
        more = nearer ? WaterMore[i] : more;
    }

    float3 result = scene;
    bool surface = best < Far;
    if (surface)
    {
        float3 hit = origin + dir * best;
        float3 n = WaveNormal(waves, more.x, hit.xz + WaterCamera.xz, WaterCamera.w);

        // What is behind the surface, bent by the ripples: unless what the bent ray finds is in front of
        // the water (a post standing in it), which would smear it into the lake.
        float2 bentUv = clamp(uv + n.xz * more.y, WaterViewport.xy, WaterViewport.zw);
        float bentDepth = DepthAt(bentUv);
        float3 bentPoint = Unproject(ToView(bentUv), bentDepth);
        float bentDist = bentDepth >= 0.99999 ? Far : dot(bentPoint - origin, dir);
        bool bent = bentDist > best;
        float2 throughUv = bent ? bentUv : uv;
        float3 through = Picture(throughUv);

        if (dir.y < 0)
        {
            // From above: the bottom through the water, the reflection by Fresnel, the glint, the shore.
            float behind = (bent ? bentDist : sceneDist) - best;
            float3 body = colour.rgb * WaterLight;
            float3 water = lerp(through, body, DepthFog(level.z, behind));

            float3 r = reflect(dir, n);
            r.y = abs(r.y);
            float3 reflected = Reflection(hit, r, SkyColour(r));
            float f = Fresnel(dot(-dir, n), colour.w);
            float glint = pow(saturate(dot(r, -WaterSunDir)), 180) * more.z;

            float depth = sky ? Far : level.x - scenePoint.y;
            float3 shaded = lerp(water, reflected, f) + WaterSunColour * glint;
            result = lerp(scene, shaded, ShoreFade(level.w, depth));
        }
        else
        {
            // From below: what is above the surface, bent.
            result = through;
        }
    }

    if (WaterUnderTint.w > 0.5)
    {
        // Under water: tinted, and fogged by the metres to what the ray reaches, or to the surface.
        float metres = surface && dir.y > 0 ? best : sceneDist;
        result = lerp(result * WaterUnderTint.rgb, WaterUnder.rgb, DepthFog(WaterUnder.w, metres));
    }
    return float4(result, 1);
}

technique Water
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSWater(); }
}
