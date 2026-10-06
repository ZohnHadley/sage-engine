#nullable enable
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// The sampler states textures are drawn with (issue #317), now that every texture has its mip chain
// (TextureMips: loose files at load, cooked ones from the `.sgtex`):
//
//   - a material's "Linear" (and "Anisotropic") filter samples between mip levels, trilinear, and with
//     `r_anisotropy` above 1 anisotropically at up to that many taps: distant floors and walls stop shimmering;
//   - "Point" keeps the exact texels of the full-size level, as before mips existed: pixel art, billboard
//     sprites (sage:sprite_default) and the UI are not blurred and do not change look with distance. MonoGame
//     has no "no mip" filter for a mipmapped texture, so this is point filtering with the level-of-detail bias
//     at its floor (-16), which keeps the sampler on level 0 for anything short of a 65536:1 minification.
//
// The states are shared and made once each (a state is frozen once a device has used it), one per
// anisotropy level and address mode; `Anisotropy` is the renderer's copy of `r_anisotropy`.
internal static class TextureSampling
{
    public const int MaxAnisotropy = 16;

    private const float TopLevelOnly = -16f;

    public static readonly SamplerState PixelClamp = Pixel(TextureAddressMode.Clamp, "sage.pixel.clamp");
    public static readonly SamplerState PixelWrap = Pixel(TextureAddressMode.Wrap, "sage.pixel.wrap");

    private static readonly SamplerState?[] Smooth = new SamplerState?[(MaxAnisotropy + 1) * 2];

    private static int _anisotropy = 1;

    public static int Anisotropy
    {
        get => _anisotropy;
        set => _anisotropy = value < 1 ? 1 : value > MaxAnisotropy ? MaxAnisotropy : value;
    }

    public static SamplerState For(SamplerFilter filter, SamplerAddress address)
    {
        bool clamp = address == SamplerAddress.Clamp;
        if (filter == SamplerFilter.Point) return clamp ? PixelClamp : PixelWrap;
        int slot = _anisotropy * 2 + (clamp ? 1 : 0);
        return Smooth[slot] ??= SmoothState(_anisotropy, clamp);
    }

    private static SamplerState Pixel(TextureAddressMode mode, string name) => new()
    {
        Name = name,
        Filter = TextureFilter.Point,
        AddressU = mode, AddressV = mode, AddressW = mode,
        MipMapLevelOfDetailBias = TopLevelOnly,
    };

    private static SamplerState SmoothState(int anisotropy, bool clamp)
    {
        var mode = clamp ? TextureAddressMode.Clamp : TextureAddressMode.Wrap;
        return new SamplerState
        {
            Name = $"sage.smooth.x{anisotropy}.{(clamp ? "clamp" : "wrap")}",
            Filter = anisotropy > 1 ? TextureFilter.Anisotropic : TextureFilter.Linear,   // Linear: min, mag and mip
            MaxAnisotropy = anisotropy,
            AddressU = mode, AddressV = mode, AddressW = mode,
        };
    }
}
