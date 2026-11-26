using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Media;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Variable fonts laid out at the weight they're drawn at (#556). A variable font (Fedora's Noto Sans, Cantarell, Source
/// Sans) is one file with a weight axis: fontconfig matches its named instance for a weight (SemiBold, 600) and Skia draws
/// that instance's outlines, while Avalonia's HarfBuzz font, made from the file's tables with no variation set, lays the
/// text out at the default instance's advances. Bold text came out bold glyphs at Regular's widths, crowded into each
/// other: the smeared bold the issue showed (headings, settings' names, the active tab). Setting the HarfBuzz font's
/// <c>wght</c> to the weight asked for makes the advances the drawn instance's (HarfBuzz clamps it to the axis). Avalonia's
/// HarfBuzz typeface is internal; its font is reached through <see cref="UnsafeAccessorAttribute"/>, as
/// <see cref="ShellSystemFonts"/> reaches the font manager. TODO(issue): drop once Avalonia applies a face's variation.
/// </summary>
internal static partial class VariableFonts
{
    private const string HarfBuzzTypefaceType = "Avalonia.Harfbuzz.HarfBuzzTypeface, Avalonia.HarfBuzz";

    /// <summary>HarfBuzzSharp's native library, which Avalonia's text shaper loads.</summary>
    private const string HarfBuzz = "libHarfBuzzSharp";

    /// <summary>The <c>wght</c> axis tag.</summary>
    internal const uint WeightAxis = ('w' << 24) | ('g' << 16) | ('h' << 8) | 't';

    /// <summary>
    /// Whether <paramref name="glyphTypeface"/>'s font has variation axes; if so, lays it out at <paramref name="weight"/>.
    /// False for a static font, or one whose shaper isn't HarfBuzz's (the headless platform without Skia).
    /// </summary>
    public static bool Apply(GlyphTypeface glyphTypeface, FontWeight weight)
    {
        if (Font(glyphTypeface) is not { } font)
        {
            return false;
        }

        try
        {
            if (hb_ot_var_has_data(hb_font_get_face(font)) == 0)
            {
                return false;
            }

            var variation = new Variation { Tag = WeightAxis, Value = (float)(int)weight };
            unsafe
            {
                hb_font_set_variations(font, &variation, 1);
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="glyphTypeface"/>'s font has variation axes.</summary>
    public static bool IsVariable(GlyphTypeface glyphTypeface)
    {
        try
        {
            return Font(glyphTypeface) is { } font && hb_ot_var_has_data(hb_font_get_face(font)) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The native HarfBuzz font behind <paramref name="glyphTypeface"/>'s shaper, or null.</summary>
    private static nint? Font(GlyphTypeface glyphTypeface)
    {
        try
        {
            return HarfBuzzFont(glyphTypeface.TextShaperTypeface)?.Handle is { } handle and not 0 ? handle : null;
        }
        catch (InvalidCastException)
        {
            return null; // another shaper's typeface
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_HBFont")]
    private static extern HarfBuzzSharp.Font? HarfBuzzFont([UnsafeAccessorType(HarfBuzzTypefaceType)] object typeface);

    [StructLayout(LayoutKind.Sequential)]
    private struct Variation
    {
        public uint Tag;
        public float Value;
    }

    [LibraryImport(HarfBuzz)]
    private static partial nint hb_font_get_face(nint font);

    [LibraryImport(HarfBuzz)]
    private static partial int hb_ot_var_has_data(nint face);

    [LibraryImport(HarfBuzz)]
    private static unsafe partial void hb_font_set_variations(nint font, Variation* variations, uint length);
}
