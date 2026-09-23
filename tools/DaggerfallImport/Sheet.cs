namespace DaggerfallImport;

// An RGBA canvas that frames are pasted into, bottom-centred in their cell the way a sprite stands
// on its own feet (06 §3.8).
internal sealed class Sheet
{
    public readonly int Width, Height;
    private readonly byte[] _pixels;

    public Sheet(int width, int height)
    {
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        _pixels = new byte[Width * Height * 4];
    }

    public void Blit(Arena2Reader.Frame frame, int cellW, int cellH, int column, int row, bool opaque = false)
    {
        int x0 = column * cellW + (cellW - frame.Width) / 2;
        int y0 = row * cellH + (cellH - frame.Height);
        for (int y = 0; y < frame.Height; y++)
        {
            int ty = y0 + y;
            if ((uint)ty >= (uint)Height) continue;
            for (int x = 0; x < frame.Width; x++)
            {
                int tx = x0 + x;
                if ((uint)tx >= (uint)Width) continue;
                int src = (y * frame.Width + x) * 4, dst = (ty * Width + tx) * 4;
                if (!opaque && frame.Rgba[src + 3] == 0) continue;
                _pixels[dst] = frame.Rgba[src];
                _pixels[dst + 1] = frame.Rgba[src + 1];
                _pixels[dst + 2] = frame.Rgba[src + 2];
                _pixels[dst + 3] = opaque ? (byte)255 : frame.Rgba[src + 3];
            }
        }
    }

    public void Save(string path) => Png.Write(path, Width, Height, _pixels);
}
