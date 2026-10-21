using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.Shell.Infrastructure;
using SkiaSharp;

namespace Codalus.Shell.Tests;

/// <summary>
/// The shell's system font collection (<see cref="ShellSystemFonts"/>): its synthetic bold and italic faces are Avalonia's,
/// made from a copy of the font file read with its stream kept alive. Each behavior test starts from a fresh collection,
/// so what other tests looked up before doesn't decide which path a lookup takes.
/// </summary>
public sealed class SystemFontsTests
{
    [AvaloniaFact]
    public void The_shell_finds_system_fonts_through_its_own_collection() =>
        Assert.IsType<ShellSystemFonts>(FontManager.Current.SystemFonts);

    [AvaloniaFact]
    public void A_bold_italic_the_collection_has_no_face_for_yet_is_bold_and_slanted()
    {
        using var fonts = ShellSystemFonts.Create();
        var family = FontManager.Current.DefaultFontFamily.Name;
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

        // Not the platform's exact match (its bold italic is 700): the nearest cached face, then a synthetic.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Italic, FontWeight.SemiBold, FontStretch.Normal, out var face));

        // As drawn: a face the platform has or synthesized (fontconfig's slant and embolden report themselves, their
        // file's tables don't), or Skia's simulations.
        Assert.True(face.PlatformTypeface.Style != FontStyle.Normal || face.FontSimulations.HasFlag(FontSimulations.Oblique), $"{face.FamilyName} is upright");
        Assert.True(face.PlatformTypeface.Weight >= FontWeight.SemiBold || face.FontSimulations.HasFlag(FontSimulations.Bold), $"{face.FamilyName} is {face.PlatformTypeface.Weight}");
    }

    [AvaloniaFact]
    public void A_semibold_of_a_family_without_one_is_its_regular_face_emboldened_as_Avalonia_draws_it()
    {
        using var fonts = ShellSystemFonts.Create();
        GlyphTypeface? regular = null;
        var family = fonts.Select(f => f.Name).FirstOrDefault(name =>
            fonts.TryGetFamilyTypefaces(name, out var faces)
            && faces.Any(f => f.Style == FontStyle.Normal && f.Weight == FontWeight.Normal)
            && faces.Any(f => f.Style == FontStyle.Normal && f.Weight == FontWeight.Bold)
            && faces.All(f => f.Weight != FontWeight.SemiBold)
            && fonts.TryGetGlyphTypeface(name, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out regular));
        Assert.SkipWhen(family is null || regular is null, "No installed font family has a regular and a bold face and no semibold one (DejaVu Sans on Linux, Arial on Windows).");

        // Not the family's bold face, which the platform matches for it: that's wider, and the setup window's text cut off.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var semibold));

        Assert.Equal(FontSimulations.Bold, semibold.FontSimulations);
        Assert.Equal(regular.PlatformTypeface.Weight, semibold.PlatformTypeface.Weight);
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal, out var again));
        Assert.Same(semibold, again);
    }

    [AvaloniaFact]
    public void A_font_file_is_copied_whole_and_its_stream_disposed_only_after_the_read()
    {
        using var skia = SKTypeface.FromFamilyName(FontManager.Current.DefaultFontFamily.Name);
        SKStreamAsset? opened = null;

        using var file = ShellSystemFonts.FontFile(skia, asset => opened = asset);

        // Avalonia's copy leaves the stream to its finalizer, which a collection can run during the read, unmapping the
        // file Skia copies from. Disposed when the copy returns, it was alive until then.
        Assert.NotNull(file);
        Assert.NotNull(opened);
        Assert.Equal(IntPtr.Zero, opened.Handle);
        using var copy = SKTypeface.FromStream(file);
        Assert.NotNull(copy);
        Assert.Equal(skia.FamilyName, copy.FamilyName);
        Assert.Equal(skia.GlyphCount, copy.GlyphCount);
    }

    [AvaloniaFact]
    public void An_italic_asked_for_first_does_not_become_the_family_s_regular_face()
    {
        using var fonts = ShellSystemFonts.Create();
        var family = fonts.Select(f => f.Name).FirstOrDefault(name =>
            fonts.TryGetFamilyTypefaces(name, out var faces) && faces.Count > 0 && faces.All(f => f.Style == FontStyle.Normal));
        Assert.SkipWhen(family is null, "No installed font family without an italic or oblique face (DejaVu Sans on Linux).");

        // The italic first: a face slanted for it (by fontconfig or Skia), which mustn't be filed as the regular one.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Italic, FontWeight.Normal, FontStretch.Normal, out var italic));
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var regular), family);

        Assert.NotSame(italic, regular);
        Assert.Equal(FontStyle.Normal, regular.PlatformTypeface.Style);
        Assert.Equal(FontSimulations.None, regular.FontSimulations);
    }
}
