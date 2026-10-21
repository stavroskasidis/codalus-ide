using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

public sealed class SystemFontsTests
{
    [AvaloniaFact]
    public void The_shell_finds_system_fonts_through_its_own_collection() =>
        Assert.IsType<ShellSystemFonts>(FontManager.Current.SystemFonts);

    [AvaloniaFact]
    public void A_bold_italic_the_collection_has_no_face_for_yet_is_bold_and_slanted()
    {
        var fonts = FontManager.Current.SystemFonts;
        var family = FontManager.Current.DefaultFontFamily.Name;
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

        // Not the platform's exact match (its bold italic is 700): the nearest cached face, then a synthetic.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Italic, FontWeight.SemiBold, FontStretch.Normal, out var face));

        Assert.NotEqual(FontStyle.Normal, face.Style);
        Assert.True(face.Weight >= FontWeight.SemiBold, $"{face.FamilyName} is {face.Weight}");
    }

    [AvaloniaFact]
    public void An_italic_of_a_family_with_only_an_oblique_is_that_face_not_a_slanted_copy()
    {
        var fonts = FontManager.Current.SystemFonts;
        var family = fonts.Select(f => f.Name).FirstOrDefault(name =>
            fonts.TryGetFamilyTypefaces(name, out var faces) && faces.Any(f => f.Style == FontStyle.Oblique) && faces.All(f => f.Style != FontStyle.Italic));
        Assert.SkipWhen(family is null, "No installed font family has an oblique face and no italic one (DejaVu Sans on Linux).");
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));

        // Avalonia's own collection slants the regular face it already has, from a copy of the font file.
        Assert.True(fonts.TryGetGlyphTypeface(family, FontStyle.Italic, FontWeight.Normal, FontStretch.Normal, out var italic));

        Assert.Equal(FontStyle.Oblique, italic.PlatformTypeface.Style);
        Assert.Equal(FontSimulations.None, italic.PlatformTypeface.FontSimulations);
        Assert.Equal(FontSimulations.None, italic.FontSimulations);
    }
}
