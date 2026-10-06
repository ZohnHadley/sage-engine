// A post effect that reads the scene's depth (issue #316): the sample of the depth hook, a haze that
// thickens with distance. The Sandbox turns it on (its content/data/post.json, `r_post_haze`); a game
// copies this file for its own depth fog, SSAO or outlines.
//
// A post_effect with `"depth": true` is handed, besides Source and SourceSize:
//   SceneDepth   `sage:depth`, the scene's z/w per pixel (0 at the near plane, 1 at the far one and on
//                the sky), drawn by the engine before the chain. Point-sample it.
//   DepthParams  x = the main view's near plane, y = its far plane, z = 1 when it is orthographic.
// `LinearDepth` turns the first into metres from the camera with the second (PostCurves.LinearDepth,
// tested). Every other parameter is the material's (engine_content/data/post.json, sage:post_depth_haze).

texture Source;
sampler SourceSampler : register(s0) = sampler_state { Texture = <Source>; };
float4 SourceSize;

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
float4 DepthParams;

float3 HazeColor;     // what the distance fades toward
float HazeStart;      // metres before any haze
float HazeDensity;    // per metre past the start: 1 - exp(-density * metres)
float HazeSky;        // how much of it the sky gets (0: the sky keeps its own colour)

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

// z/w (Direct3D's 0..1, as MonoGame's projections make it) back to the distance along the view.
float LinearDepth(float d)
{
    float n = DepthParams.x;
    float f = DepthParams.y;
    float perspective = n * f / (f - d * (f - n));
    float orthographic = n + d * (f - n);
    return lerp(perspective, orthographic, DepthParams.z);
}

float4 PSHaze(VSOutput input) : COLOR0
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    float d = tex2D(DepthSampler, input.UV).r;
    float metres = max(LinearDepth(d) - HazeStart, 0);
    float haze = 1 - exp(-HazeDensity * metres);
    haze *= d < 0.99999 ? 1 : HazeSky;
    return float4(lerp(c, HazeColor, haze), 1);
}

technique Haze
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSHaze(); }
}
