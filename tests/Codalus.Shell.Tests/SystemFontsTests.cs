using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>
/// The shell's system font collection (<see cref="ShellSystemFonts"/>): its synthetic bold and italic faces come from the
/// platform's font manager instead of a copy of the font file. Each behavior test starts from a fresh collection, so
/// what other tests looked up before doesn't decide which path a lookup takes.
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
    public void An_italic_of_a_family_with_only_an_oblique_is_that_face_not_a_slanted_copy()
    {
        using var fonts = ShellSystemFonts.Create();
        var family = fonts.Select(f => f.Name).FirstOrDefault(name =>
            fonts.TryGetFamilyTypefaces(name, out var faces) && faces.Any(f => f.Style == FontStyle.Oblique) && faces.All(f => f.Style != FontStyle.Italic));
        Assert.SkipWhen(family is null, "No installed font family has an oblique face and no italic one (DejaVu Sans Mono on Linux).");
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

        // Avalonia's own collection slants the regular face it already has, from a copy of the font file.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Italic, FontWeight.Normal, FontStretch.Normal, out var italic));

        Assert.Equal(FontStyle.Oblique, italic.PlatformTypeface.Style);
        Assert.Equal(FontSimulations.None, italic.PlatformTypeface.FontSimulations);
        Assert.Equal(FontSimulations.None, italic.FontSimulations);
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
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var regular));

        Assert.NotSame(italic, regular);
        Assert.Equal(FontStyle.Normal, regular.PlatformTypeface.Style);
        Assert.Equal(FontSimulations.None, regular.FontSimulations);
    }
}
