using System.Buffers.Binary;
using System.Drawing;
using System.IO.Compression;
using Codalus.Tests;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The tests' PNG reader (#78), on every OS: PNGs made here, RGBA and RGB, each row with another of PNG's five filter
/// types, read back to the exact pixels. (GDI+'s own frames are read by the Windows designer host tests.)
/// </summary>
public sealed class PngPixelsTests
{
    /// <summary>3×5 pixels, each a different color: row y uses filter type y.</summary>
    private static readonly Color[,] s_pixels = new Color[3, 5];

    static PngPixelsTests()
    {
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                s_pixels[x, y] = Color.FromArgb(255 - (x * 40) - y, (x * 70) + (y * 21), 200 - (y * 37) + x, (y * 50) + (x * 3));
            }
        }
    }

    /// <summary>A PNG of <see cref="s_pixels"/>, encoded with a row's filter type its row number.</summary>
    private static byte[] Encode(bool alpha)
    {
        var bytesPerPixel = alpha ? 4 : 3;
        var stride = 3 * bytesPerPixel;
        var rows = new byte[5][];
        for (var y = 0; y < 5; y++)
        {
            rows[y] = new byte[stride];
            for (var x = 0; x < 3; x++)
            {
                var c = s_pixels[x, y];
                rows[y][x * bytesPerPixel] = c.R;
                rows[y][(x * bytesPerPixel) + 1] = c.G;
                rows[y][(x * bytesPerPixel) + 2] = c.B;
                if (alpha)
                {
                    rows[y][(x * bytesPerPixel) + 3] = c.A;
                }
            }
        }

        using var filtered = new MemoryStream();
        for (var y = 0; y < 5; y++)
        {
            var prior = y == 0 ? new byte[stride] : rows[y - 1];
            filtered.WriteByte((byte)y);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= bytesPerPixel ? rows[y][i - bytesPerPixel] : 0, up = prior[i], upLeft = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                var predicted = y switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => PngPixels.Paeth(left, up, upLeft) };
                filtered.WriteByte((byte)(rows[y][i] - predicted));
            }
        }

        using var deflated = new MemoryStream();
        using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(filtered.ToArray());
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 5);
        header[8] = 8;
        header[9] = (byte)(alpha ? 6 : 2);
        Chunk(png, "IHDR", header);

        // The pixels in two IDAT chunks, as encoders split them: the reader joins them.
        var pixels = deflated.ToArray();
        Chunk(png, "IDAT", pixels[..(pixels.Length / 2)]);
        Chunk(png, "IDAT", pixels[(pixels.Length / 2)..]);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        png.Write(length);
        png.Write(System.Text.Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write(new byte[4]); // the CRC, which the reader doesn't check
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_filter_type_reads_back_to_the_exact_pixels(bool alpha)
    {
        var png = PngPixels.Decode(Encode(alpha));
        Assert.Equal((3, 5), (png.Width, png.Height));
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                var expected = alpha ? s_pixels[x, y] : Color.FromArgb(255, s_pixels[x, y]);
                Assert.True(expected.ToArgb() == png.At(x, y).ToArgb(), $"({x}, {y}) with filter type {y}: {png.At(x, y)}, expected {expected}");
            }
        }

        // Regions: the same picture is the same everywhere; another differs where its pixels do (clipped to the pictures).
        var again = PngPixels.FromBase64(Convert.ToBase64String(Encode(alpha)));
        Assert.True(png.Same(again, new Rectangle(0, 0, 3, 5)));
        var other = PngPixels.Decode(Encode(!alpha));
        Assert.Equal(15 - CountOpaque(), png.Differing(other, new Rectangle(-2, -2, 10, 10))); // the alpha, opaque only at (0, 0)
    }

    [Fact]
    public void Only_8_bit_rgb_and_rgba_are_read()
    {
        Assert.Throws<InvalidDataException>(() => PngPixels.Decode([1, 2, 3]));
        var png = Encode(alpha: true);
        png[8 + 8 + 8] = 16; // IHDR's bit depth
        Assert.Throws<InvalidDataException>(() => PngPixels.Decode(png));
    }

    private static int CountOpaque()
    {
        var count = 0;
        foreach (var c in s_pixels)
        {
            count += c.A == 255 ? 1 : 0;
        }

        return count;
    }
}
