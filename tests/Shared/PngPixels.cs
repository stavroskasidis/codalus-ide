using System.Buffers.Binary;
using System.Drawing;
using System.IO.Compression;

namespace Codalus.Tests;

/// <summary>
/// A PNG's pixels for tests (#78: the Windows Forms designer's frames), with no dependency: 8-bit RGB or RGBA,
/// not interlaced, which is what GDI+ writes for the designer's 32-bit frames. The chunks' CRCs aren't checked.
/// </summary>
internal sealed class PngPixels
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly byte[] _rgba;

    private PngPixels(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public static PngPixels FromBase64(string png) => Decode(Convert.FromBase64String(png));

    public static PngPixels Decode(byte[] png)
    {
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG.");
        }

        int width = 0, height = 0, bytesPerPixel = 0;
        using var data = new MemoryStream();
        for (var at = Signature.Length; at + 8 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var chunk = png.AsSpan(at + 8, length);
            at += 12 + length;
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                    var (depth, color, interlace) = (chunk[8], chunk[9], chunk[12]);
                    if (depth != 8 || color is not (2 or 6) || interlace != 0)
                    {
                        throw new InvalidDataException($"Only 8-bit RGB or RGBA without interlacing is read (depth {depth}, color type {color}, interlace {interlace}).");
                    }

                    bytesPerPixel = color == 6 ? 4 : 3;
                    break;
                case "IDAT":
                    data.Write(chunk);
                    break;
                case "IEND":
                    at = png.Length;
                    break;
            }
        }

        if (bytesPerPixel == 0)
        {
            throw new InvalidDataException("No IHDR.");
        }

        data.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var raw = inflated.ToArray();
        var stride = width * bytesPerPixel;
        if (raw.Length < height * (stride + 1))
        {
            throw new InvalidDataException($"{raw.Length} bytes of pixels for {width}×{height}.");
        }

        var rgba = new byte[width * height * 4];
        var prior = new byte[stride];
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var offset = y * (stride + 1);
            var filter = raw[offset];
            Array.Copy(raw, offset + 1, row, 0, stride);
            Unfilter(filter, row, prior, bytesPerPixel);
            for (var x = 0; x < width; x++)
            {
                var target = ((y * width) + x) * 4;
                rgba[target] = row[x * bytesPerPixel];
                rgba[target + 1] = row[(x * bytesPerPixel) + 1];
                rgba[target + 2] = row[(x * bytesPerPixel) + 2];
                rgba[target + 3] = bytesPerPixel == 4 ? row[(x * bytesPerPixel) + 3] : (byte)255;
            }

            (prior, row) = (row, prior);
        }

        return new PngPixels(width, height, rgba);
    }

    /// <summary>Undoes a row's filter (PNG's types 0 to 4) in place, given the row above it unfiltered.</summary>
    private static void Unfilter(byte filter, byte[] row, byte[] prior, int bytesPerPixel)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0, up = prior[i], upLeft = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + ((left + up) / 2)),
                4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                _ => throw new InvalidDataException($"Filter type {filter}."),
            };
        }
    }

    internal static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var (toLeft, toUp, toUpLeft) = (Math.Abs(estimate - left), Math.Abs(estimate - up), Math.Abs(estimate - upLeft));
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    public Color At(int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return Color.FromArgb(_rgba[i + 3], _rgba[i], _rgba[i + 1], _rgba[i + 2]);
    }

    /// <summary>How many pixels of <paramref name="region"/> (inside both pictures) differ from <paramref name="other"/>'s.</summary>
    public int Differing(PngPixels other, Rectangle region)
    {
        var r = Rectangle.Intersect(region, Rectangle.Intersect(new Rectangle(0, 0, Width, Height), new Rectangle(0, 0, other.Width, other.Height)));
        var count = 0;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            for (var x = r.Left; x < r.Right; x++)
            {
                if (At(x, y) != other.At(x, y))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Whether <paramref name="region"/> is the same in both pictures, pixel for pixel.</summary>
    public bool Same(PngPixels other, Rectangle region) => Differing(other, region) == 0;
}
