using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using SkiaSharp;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The system fonts, found and synthesized as Avalonia's own collection does (this mirrors its internal
/// <c>SystemFontCollection</c> and <c>FontCollectionBase.TryCreateSyntheticGlyphTypeface</c>, MIT), except for how a
/// synthetic face reads its font file. A bold or italic a family lacks is the nearest face it has, emboldened or slanted
/// by Skia, created again from the font file's bytes. Avalonia reads them in <c>SkiaTypeface.TryGetStream</c> with
/// <c>SKStream.Read</c>, which doesn't keep its stream alive: a collection on any thread during the read can finalize the
/// stream and unmap the font file Skia copies from, a segmentation fault in memcpy: the start window on Linux CI, then the
/// first-run setup in the arm64 package's smoke test (develop run 39513048761) even with background collections turned
/// off. Here the stream lives until the read is done. Avalonia's font manager and Skia typeface are its unstable API,
/// left out of its reference assemblies: they're reached through <see cref="UnsafeAccessorAttribute"/>, which Native AOT
/// binds at compile time. TODO(issue): drop once Avalonia's TryGetStream keeps its stream alive.
/// </summary>
internal sealed class ShellSystemFonts : FontCollectionBase
{
    private const string SkiaTypefaceType = "Avalonia.Skia.SkiaTypeface, Avalonia.Skia";

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
    public static void Install() => FontManager.Current.AddFontCollection(Create());

    /// <summary>A collection on the platform's font manager, with nothing looked up yet (tests).</summary>
    internal static ShellSystemFonts Create() => new(PlatformImpl(FontManager.Current));

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

        // Also under the style the platform says the face has, never the one its tables say: a face it slanted or
        // emboldened (fontconfig's synthetic faces report it, Skia's simulations don't) read as the family's regular one
        // once an italic or a bold was asked for first, and every regular text after it came out slanted or bold.
        // Another thread may have added these keys meanwhile: the faces in the collection win.
        if (platformTypeface.FontSimulations == FontSimulations.None)
        {
            TryAddGlyphTypeface(created, KeyOf(platformTypeface));
        }

        return TryGetGlyphTypeface(familyName, key, allowNearestMatch: false, out glyphTypeface);
    }

    public override bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces) =>
        TryGetFamilyTypefaces(_platform, familyName, out familyTypefaces);

    public override bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        syntheticGlyphTypeface = null;
        var key = new FontCollectionKey(style, weight, stretch);
        if (new FontCollectionKey(glyphTypeface.Style, glyphTypeface.Weight, glyphTypeface.Stretch) == key)
        {
            return false;
        }

        var simulations = FontSimulations.None;
        if (style != FontStyle.Normal && glyphTypeface.Style != style)
        {
            simulations |= FontSimulations.Oblique;
        }

        if ((int)weight >= 600 && glyphTypeface.Weight < weight)
        {
            simulations |= FontSimulations.Bold;
        }

        if (simulations == FontSimulations.None)
        {
            return false;
        }

        // One made already, through another of the family's names or by another thread.
        if (TryGetGlyphTypeface(glyphTypeface.FamilyName, key, allowNearestMatch: false, out var cached) && cached.FontSimulations == simulations)
        {
            syntheticGlyphTypeface = cached;
            return true;
        }

        using var file = FontFile(glyphTypeface.PlatformTypeface);
        if (file is null
            || !TryCreateGlyphTypeface(_platform, file, simulations, out var platformTypeface)
            || Create(platformTypeface, simulations) is not { } created)
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

        // A face cached for the family and style, when it has the character: the lookup also matches another family by its
        // name's prefix ("SimSun-ExtB" for "SimSun"), where Avalonia's reads only the family's own entry.
        var platformKey = KeyOf(platformTypeface);
        if (TryGetGlyphTypeface(platformTypeface.FamilyName, platformKey, allowNearestMatch: false, out glyphTypeface)
            && glyphTypeface.CharacterToGlyphMap.ContainsGlyph(codepoint))
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

    /// <summary>A copy of <paramref name="typeface"/>'s font file, or null when it can't be read.</summary>
    internal static MemoryStream? FontFile(IPlatformTypeface typeface)
    {
        SKTypeface skia;
        try
        {
            skia = SkiaTypeface(typeface);
        }
        catch (InvalidCastException)
        {
            // Not Skia's (the headless platform's): its own read.
            return typeface.TryGetStream(out var stream) ? stream as MemoryStream ?? Copy(stream) : null;
        }

        return FontFile(skia);

        static MemoryStream Copy(Stream stream)
        {
            using (stream)
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return copy;
            }
        }
    }

    /// <summary>
    /// A copy of <paramref name="skia"/>'s font file, or null when it can't be read. The stream Skia opens on the file
    /// (the file mapped into memory, unmapped when the stream is finalized) is disposed only after the read, so it stays
    /// alive through it; <paramref name="opened"/> sees it (tests).
    /// </summary>
    internal static MemoryStream? FontFile(SKTypeface skia, Action<SKStreamAsset>? opened = null)
    {
        using var asset = skia.OpenStream();
        if (asset is null || asset.Length <= 0)
        {
            return null;
        }

        opened?.Invoke(asset);
        var bytes = new byte[asset.Length];
        return asset.Read(bytes, bytes.Length) == bytes.Length ? new MemoryStream(bytes, writable: false) : null;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PlatformImpl")]
    private static extern IFontManagerImpl PlatformImpl(FontManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(GetInstalledFontFamilyNames))]
    private static extern string[] GetInstalledFontFamilyNames(IFontManagerImpl platform, bool checkForUpdates);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryCreateGlyphTypeface))]
    private static extern bool TryCreateGlyphTypeface(IFontManagerImpl platform, string familyName, FontStyle style, FontWeight weight, FontStretch stretch, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryCreateGlyphTypeface))]
    private static extern bool TryCreateGlyphTypeface(IFontManagerImpl platform, Stream stream, FontSimulations fontSimulations, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryGetFamilyTypefaces))]
    private static extern bool TryGetFamilyTypefaces(IFontManagerImpl platform, string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(TryMatchCharacter))]
    private static extern bool TryMatchCharacter(IFontManagerImpl platform, int codepoint, FontStyle style, FontWeight weight, FontStretch stretch, string? familyName, CultureInfo? culture, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface);

    /// <summary>The Skia typeface of Avalonia's; <see cref="InvalidCastException"/> for another platform's.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SKTypeface")]
    private static extern SKTypeface SkiaTypeface([UnsafeAccessorType(SkiaTypefaceType)] object typeface);

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
