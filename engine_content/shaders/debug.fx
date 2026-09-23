// Debug geometry (docs/design/06 §3.2, 07 §3.2): vertex-coloured lines in camera-relative world
// space. No texture, no lighting, no fog — the point of a debug line is that you can see it.
#include "common.fxh"

struct VSInput
{
    float4 Position : POSITION0;    // already camera-relative world space
    float4 Color    : COLOR0;
};

struct VSOutput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, ViewProj);
    output.Color = input.Color;
    return output;
}

float4 PS(VSOutput input) : COLOR0
{
    return input.Color;
}

technique Default
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
}
