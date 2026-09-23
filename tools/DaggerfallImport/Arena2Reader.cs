using System.Reflection;

namespace DaggerfallImport;

// Reads TEXTURE.### archives through DaggerfallConnect, loaded by path and called by reflection: the
// tool has no compile-time dependency on an assembly that lives outside the repository, and there is
// nothing to restore for someone who only wants to build the engine.
//
// Decoding is done here rather than through the library's own `GetManagedBitmap`, which is 32-bit era
// code (it casts an IntPtr to int) and overflows in a 64-bit process. The indexed data and the
// palette are plain managed arrays, so converting them is a few lines and works anywhere.
internal sealed class Arena2Reader
{
    private readonly Type _textureFile;
    private readonly Type _dfBitmap;
    private readonly Type _dfPalette;
    private readonly object _useDisk;
    private readonly string _arena2;

    public Arena2Reader(string connectDll, string arena2)
    {
        var assembly = Assembly.LoadFrom(connectDll);
        _textureFile = Required(assembly, "DaggerfallConnect.Arena2.TextureFile");
        _dfBitmap = Required(assembly, "DaggerfallConnect.DFBitmap");
        _dfPalette = Required(assembly, "DaggerfallConnect.DFPalette");
        _useDisk = Enum.Parse(Required(assembly, "DaggerfallConnect.FileUsage"), "UseDisk");
        _arena2 = arena2;
    }

    private static Type Required(Assembly assembly, string name) =>
        assembly.GetType(name) ?? throw new InvalidOperationException(
            $"{assembly.GetName().Name} has no {name}: is that really DaggerfallConnect.dll?");

    public sealed record Frame(int Record, int Index, int Width, int Height, byte[] Rgba);

    // Every frame of records `first`..`last` of TEXTURE.<archive>, as RGBA. Palette index 0 is
    // Daggerfall's transparent colour, so those pixels come back with zero alpha.
    public List<Frame> Read(int archive, int first, int last)
    {
        var frames = new List<Frame>();
        string path = Path.Combine(_arena2, $"TEXTURE.{archive:000}");
        if (!File.Exists(path)) { Console.Error.WriteLine($"No {path}"); return frames; }

        object file = Activator.CreateInstance(_textureFile)!;
        var load = _textureFile.GetMethod("Load", new[] { typeof(string), _useDisk.GetType(), typeof(bool) })!;
        if (!(bool)load.Invoke(file, new[] { path, _useDisk, (object)true })!)
        {
            Console.Error.WriteLine($"Could not read {path}");
            return frames;
        }

        // Load the palette the archive asks for (ART_PAL.COL for textures). Without this the reader
        // hands back its unloaded default, which is every entry bright red — the first run of this
        // tool produced a scene that looked like it was on fire.
        string paletteName = (string)_textureFile.GetProperty("PaletteName")!.GetValue(file)!;
        string palettePath = Path.Combine(_arena2, paletteName);
        if (!File.Exists(palettePath))
        {
            Console.Error.WriteLine($"No palette {palettePath}: colours will be wrong");
            return frames;
        }
        var palette = Activator.CreateInstance(_dfPalette, palettePath)!;
        _textureFile.GetProperty("Palette")!.SetValue(file, palette);
        var red = _dfPalette.GetMethod("GetRed")!;
        var green = _dfPalette.GetMethod("GetGreen")!;
        var blue = _dfPalette.GetMethod("GetBlue")!;
        var lookup = new byte[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            lookup[i * 3] = (byte)red.Invoke(palette, new object[] { i })!;
            lookup[i * 3 + 1] = (byte)green.Invoke(palette, new object[] { i })!;
            lookup[i * 3 + 2] = (byte)blue.Invoke(palette, new object[] { i })!;
        }

        int records = (int)_textureFile.GetProperty("RecordCount")!.GetValue(file)!;
        var frameCount = _textureFile.GetMethod("GetFrameCount")!;
        var getBitmap = _textureFile.GetMethod("GetDFBitmap", new[] { typeof(int), typeof(int) })!;
        var widthField = _dfBitmap.GetField("Width")!;
        var heightField = _dfBitmap.GetField("Height")!;
        var strideField = _dfBitmap.GetField("Stride")!;
        var dataField = _dfBitmap.GetField("Data")!;
        var formatField = _dfBitmap.GetField("Format")!;

        for (int r = first; r <= Math.Min(last, records - 1); r++)
            for (int f = 0, n = (int)frameCount.Invoke(file, new object[] { r })!; f < n; f++)
            {
                object bitmap = getBitmap.Invoke(file, new object[] { r, f })!;
                int width = (int)widthField.GetValue(bitmap)!;
                int height = (int)heightField.GetValue(bitmap)!;
                int stride = (int)strideField.GetValue(bitmap)!;
                var data = (byte[])dataField.GetValue(bitmap)!;
                bool indexed = Convert.ToInt32(formatField.GetValue(bitmap)) == 0;
                if (width <= 0 || height <= 0 || data.Length == 0) continue;

                var rgba = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int dst = (y * width + x) * 4;
                        if (indexed)
                        {
                            int index = data[y * stride + x];
                            if (index == 0) continue;                    // the hole in the sprite
                            rgba[dst] = lookup[index * 3];
                            rgba[dst + 1] = lookup[index * 3 + 1];
                            rgba[dst + 2] = lookup[index * 3 + 2];
                            rgba[dst + 3] = 255;
                        }
                        else
                        {
                            int src = y * stride + x * 4;                // A8R8G8B8
                            rgba[dst] = data[src + 2];
                            rgba[dst + 1] = data[src + 1];
                            rgba[dst + 2] = data[src];
                            rgba[dst + 3] = data[src + 3];
                        }
                    }
                frames.Add(new Frame(r, f, width, height, rgba));
            }
        return frames;
    }
}
