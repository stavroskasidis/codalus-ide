using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The system fonts, found as Avalonia's own collection finds them (this mirrors its internal <c>SystemFontCollection</c>,
/// MIT), except for the synthetic bold or italic face a family lacks (an italic of a family that has only an oblique,
/// common on Linux). Avalonia makes that face from the font file's bytes, read in <c>SkiaTypeface.TryGetStream</c> with
/// <c>SKStream.Read</c>, which doesn't keep its stream alive: a collection on any thread during the read can finalize the
/// stream and unmap the font file Skia copies from, a segmentation fault in memcpy: the start window on Linux CI, then the
/// first-run setup in the arm64 package's smoke test (develop run 39513048761) even with background collections turned
/// off. Here the platform's font manager matches the family again with the style asked for and applies the simulations
/// itself, reading no bytes. The font manager's members are Avalonia's unstable API,
/// left out of its reference assemblies: they're reached through <see cref="UnsafeAccessorAttribute"/>, which Native AOT
/// binds at compile time. TODO(issue): drop once Avalonia's TryGetStream keeps its stream alive.
/// </summary>
internal sealed class ShellSystemFonts : FontCollectionBase
{
    private static readonly Uri s_key = new("fonts:SystemFonts", UriKind.Absolute);

    private readonly IFontManagerImpl _platform;

    /// <summary>The family and style lookups the platform had no font for, so it isn't asked again.</summary>
    private readonly ConcurrentDictionary<(string Family, FontCollectionKey Key), byte> _missing = new();

    public ShellSystemFonts(IFontManagerImpl platform)
    {
        _platform = platform;
        foreach (var familyName in GetInstalledFontFamilyNames(platform, checkForUpdates: false).Where(x => !string.IsNullOrEmpty(x)))
        {
            AddFontFamily(familyName);
        }
    }

    /// <summary>Avalonia's system font collection key: the font manager uses this collection instead of creating its own.</summary>
    public override Uri Key => s_key;

    /// <summary>Replaces Avalonia's system font collection; call it before any text is laid out.</summary>
    public static void Install() => FontManager.Current.AddFontCollection(new ShellSystemFonts(PlatformImpl(FontManager.Current)));

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        var typeface = new Typeface(familyName, style, weight, stretch).Normalize(out familyName);
        var key = new FontCollectionKey(typeface.Style, typeface.Weight, typeface.Stretch);
        if (TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface))
        {
            return true;
        }

        if (_missing.ContainsKey((familyName, key)))
        {
            return false;
        }

        if (!TryCreateGlyphTypeface(_platform, familyName, style, weight, stretch, out var platformTypeface))
        {
            _missing.TryAdd((familyName, key), 0);
            TryAddGlyphTypeface(familyName, key, null);
            return false;
        }

        // Not a perfect match: the nearest the collection already has (or a synthetic made from it) may be closer.
        if (key != KeyOf(platformTypeface) && TryGetGlyphTypeface(familyName, key, allowNearestMatch: true, out glyphTypeface))
        {
            return true;
        }

        if (Create(platformTypeface, FontSimulations.None) is not { } created)
        {
            return false;
        }

        TryAddGlyphTypeface(platformTypeface.FamilyName, key, created);
        if (!string.Equals(familyName, platformTypeface.FamilyName, StringComparison.OrdinalIgnoreCase))
        {
            TryAddGlyphTypeface(familyName, key, created);
        }

        // Another thread may have added this key meanwhile: the one in the collection wins.
        TryAddGlyphTypeface(created);
        return TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface);
    }

    public override bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces) =>
        TryGetFamilyTypefaces(_platform, familyName, out familyTypefaces);

    public override bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        syntheticGlyphTypeface = null;
        var key = new FontCollectionKey(style, weight, stretch);
        var source = new FontCollectionKey(glyphTypeface.Style, glyphTypeface.Weight, glyphTypeface.Stretch);
        if (source == key)
        {
            return false;
        }

        if (TryGetGlyphTypeface(glyphTypeface.FamilyName, key, allowNearestMatch: false, out syntheticGlyphTypeface))
        {
            return true;
        }

        // The platform's match for the style asked for: a face the family has (an oblique for an italic), or the nearest
        // with the bold or italic simulations it lacks, drawn by Skia from the same typeface. Skia slants only for an
        // italic, so an oblique is asked for as one.
        var asked = style == FontStyle.Oblique ? FontStyle.Italic : style;
        if (!TryCreateGlyphTypeface(_platform, glyphTypeface.FamilyName, asked, weight, stretch, out var platformTypeface)
            || (platformTypeface.FontSimulations == FontSimulations.None && KeyOf(platformTypeface) == source))
        {
            return false;
        }

        if (Create(platformTypeface, platformTypeface.FontSimulations) is not { } created)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(glyphTypeface.TypographicFamilyName))
        {
            TryAddGlyphTypeface(glyphTypeface.TypographicFamilyName, key, created);
        }

        foreach (var name in glyphTypeface.FamilyNames.Values)
        {
            TryAddGlyphTypeface(name, key, created);
        }

        syntheticGlyphTypeface = created;
        return true;
    }

    protected override bool TryMatchCharacterFromPlatform(int codepoint, FontCollectionKey key, string? familyName, CultureInfo? culture, [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (!TryMatchCharacter(_platform, codepoint, key.Style, key.Weight, key.Stretch, familyName, culture, out var platformTypeface))
        {
            return false;
        }

        var platformKey = KeyOf(platformTypeface);
        if (TryGetGlyphTypeface(platformTypeface.FamilyName, platformKey, allowNearestMatch: false, out glyphTypeface))
        {
            return true;
        }

        if (Create(platformTypeface, FontSimulations.None) is not { } created)
        {
            return false;
        }

        TryAddGlyphTypeface(platformTypeface.FamilyName, platformKey, created);
        TryAddGlyphTypeface(created, platformKey);
        glyphTypeface = created;
        return true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PlatformImpl")]
    private static extern IFontManagerImpl PlatformImpl(FontManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(GetInstalledFontFamilyNames))]
    private static extern string[] GetInstalledFontFamilyNames(IFontManagerImpl platform, bool checkForUpdates);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryCreateGlyphTypeface))]
    private static extern bool TryCreateGlyphTypeface(IFontManagerImpl platform, string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryGetFamilyTypefaces))]
    private static extern bool TryGetFamilyTypefaces(IFontManagerImpl platform, string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryMatchCharacter))]
    private static extern bool TryMatchCharacter(IFontManagerImpl platform, int codepoint, FontStyle style, FontWeight weight, FontStretch stretch, string? familyName, CultureInfo? culture, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface);

    private static FontCollectionKey KeyOf(IPlatformTypeface typeface) => new(typeface.Style, typeface.Weight, typeface.Stretch);

    /// <summary>A glyph typeface for <paramref name="platformTypeface"/>, or null for a font whose tables can't be read.</summary>
    private static GlyphTypeface? Create(IPlatformTypeface platformTypeface, FontSimulations simulations)
    {
        try
        {
            return new GlyphTypeface(platformTypeface, simulations);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        {
            return null;
        }
    }
}
