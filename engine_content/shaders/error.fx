// Flat magenta (docs/design/07 §8): what anything with a broken material or a missing mesh draws
// as, so problems are visible instead of silently missing.
#include "common.fxh"

float4 VS(float4 position : POSITION0) : POSITION0
{
    return mul(mul(position, World), ViewProj);
}

float4 PS() : COLOR0
{
    return float4(1, 0, 1, 1);
}

technique Default
{
    pass P0 { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); }
}
