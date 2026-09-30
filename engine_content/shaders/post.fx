// Post-processing (docs/design/06 "As built (post-processing)", issue 4h-6): the full-screen effects of
// the post_effect chain. Each step draws one quad already in clip space and reads the step before it
// as Source. LDR in, LDR out: exposure lives in the grade (decision 6). Techniques: Copy, Grade, Vignette.
//
// The engine sets Source, SourceSize (width, height, 1/width, 1/height) and Night (0 by day, 1 at
// night, from the sky); every other parameter is the material's (engine_content/data/post.json).

texture Source;
sampler SourceSampler = sampler_state { Texture = <Source>; };
float4 SourceSize;
float Night;

float Exposure;           // stops: +1 is twice as bright
float Contrast;           // 1: unchanged, around mid grey
float Saturation;         // 1: unchanged, 0: grey
float3 ColorFilter;       // multiplied in last
float3 NightColor;        // what the night tint pulls the picture toward, times its brightness
float NightStrength;      // how much of it at full night

float VignetteStrength;   // how dark the corners go (0: none)
float VignetteRadius;     // where the darkening starts, from the centre (in screen heights)
float VignetteSoftness;   // how far it takes to reach full strength

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

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float4 PSCopy(VSOutput input) : COLOR0
{
    return float4(tex2D(SourceSampler, input.UV).rgb, 1);
}

float4 PSGrade(VSOutput input) : COLOR0
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    c = c * exp2(Exposure);
    c = lerp(c, Luma(c) * NightColor, saturate(Night * NightStrength));
    float grey = Luma(c);
    c = lerp(float3(grey, grey, grey), c, Saturation);
    c = (c - 0.5) * Contrast + 0.5;
    c = c * ColorFilter;
    return float4(saturate(c), 1);
}

float4 PSVignette(VSOutput input) : COLOR0
{
    float3 c = tex2D(SourceSampler, input.UV).rgb;
    float2 d = (input.UV - 0.5) * float2(SourceSize.x * SourceSize.w, 1);   // in screen heights: round on any aspect
    float v = smoothstep(VignetteRadius, VignetteRadius + VignetteSoftness, length(d));
    return float4(c * (1 - v * VignetteStrength), 1);
}

technique Copy
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSCopy(); }
}

technique Grade
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSGrade(); }
}

technique Vignette
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PSVignette(); }
}
