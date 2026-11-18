using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A Toolbox item's own picture (#460): the bytes its <c>[ToolboxBitmap]</c> names, as the C# host read them from the
/// item's assembly (<c>WinFormsToolboxItem.Bitmap</c>, base64), decoded once. A <c>.bmp</c> without transparency is
/// made transparent where it has the color of its bottom-left pixel, as Windows Forms' <c>ToolboxBitmapAttribute</c>
/// does (its <c>MakeTransparent</c>); a <c>.png</c> or <c>.ico</c>, or a 32-bit <c>.bmp</c> with alpha, keeps its own.
/// What doesn't decode has no picture: the item keeps its icon.
/// </summary>
public static class ToolboxPictures
{
    private static readonly ConcurrentDictionary<string, IImage?> s_pictures = new(StringComparer.Ordinal);

    /// <summary>The picture of <paramref name="base64"/>, or null.</summary>
    public static IImage? From(string? base64) => base64 is { Length: > 0 } ? s_pictures.GetOrAdd(base64, Decode) : null;

    private static IImage? Decode(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var picture = WriteableBitmap.Decode(new MemoryStream(bytes, writable: false));
            var size = picture.PixelSize;
            if (size.Width is < 1 or > 256 || size.Height is < 1 or > 256)
            {
                picture.Dispose();
                return null;
            }

            if (bytes is [(byte)'B', (byte)'M', ..])
            {
                using var buffer = picture.Lock();
                if (buffer.Format == PixelFormat.Bgra8888 || buffer.Format == PixelFormat.Rgba8888)
                {
                    MakeTransparent(buffer);
                }
            }

            return picture;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>A bitmap's pixels of its bottom-left pixel's color made transparent, unless it has transparency of its own.</summary>
    private static void MakeTransparent(ILockedFramebuffer buffer)
    {
        var (width, height, stride) = (buffer.Size.Width, buffer.Size.Height, buffer.RowBytes);
        var pixels = new byte[stride * height];
        Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[(y * stride) + (x * 4) + 3] != 255)
                {
                    return; // a 32-bit bitmap with its own alpha
                }
            }
        }

        // The three color bytes compared as they are (Bgra or Rgba alike), and a match cleared whole: transparent black
        // reads the same premultiplied or not.
        var corner = (height - 1) * stride;
        var (c0, c1, c2) = (pixels[corner], pixels[corner + 1], pixels[corner + 2]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * stride) + (x * 4);
                if (pixels[at] == c0 && pixels[at + 1] == c1 && pixels[at + 2] == c2)
                {
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = pixels[at + 3] = 0;
                }
            }
        }

        Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
    }
}
