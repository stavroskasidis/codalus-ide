using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>How the system draws text: what <c>workbench.textRendering</c>'s <c>system</c> follows.</summary>
/// <param name="Antialias">Whether text is smoothed at all.</param>
/// <param name="Subpixel">Whether it's smoothed per subpixel (ClearType, fontconfig's <c>rgba</c> = <c>rgb</c>).</param>
/// <param name="Hinting">How hard outlines are fitted to the pixel grid (fontconfig's <c>hintstyle</c>).</param>
internal readonly record struct SystemTextRendering(bool Antialias, bool Subpixel, TextHintingMode Hinting)
{
    /// <summary>Grayscale with slight hinting: fontconfig's own defaults, and what GNOME and KDE ship.</summary>
    public static SystemTextRendering Grayscale { get; } = new(Antialias: true, Subpixel: false, TextHintingMode.Light);
}

/// <summary>
/// How the UI's text is smoothed (#556): <c>workbench.textRendering</c>, by default the system's way. Avalonia smooths per
/// subpixel unless told otherwise, whatever the system does; on Linux, where fontconfig says grayscale on most desktops
/// (GNOME's and KDE's defaults), every native app beside Codalus drew grayscale text while Codalus's had colored fringes,
/// which read as blur, more so on a dark theme and on a display the compositor scales. The choice is set on every top
/// level (windows and popups) as its template is applied, before its first frame, and redrawn when the setting changes.
/// </summary>
internal sealed partial class TextRendering : IDisposable
{
    public const string Setting = "workbench.textRendering";

    public const string System = "system";
    public const string Grayscale = "grayscale";
    public const string Subpixel = "subpixel";
    public const string Aliased = "aliased";

    public static IReadOnlyList<string> Choices { get; } = [System, Grayscale, Subpixel, Aliased];

    private readonly SettingsService _settings;
    private readonly Lazy<SystemTextRendering> _system;
    private readonly List<WeakReference<TopLevel>> _topLevels = [];
    private readonly IDisposable _templated;
    private TextOptions _options;
    private string? _preview;

    public TextRendering(SettingsService settings, Func<SystemTextRendering>? system = null)
    {
        _settings = settings;
        _system = new Lazy<SystemTextRendering>(system ?? ReadSystem);
        _options = Options(settings.GetString(Setting), _system.Value);
        _templated = TemplatedControl.TemplateAppliedEvent.AddClassHandler<TopLevel>((topLevel, _) => Templated(topLevel));
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>The options every top level gets now.</summary>
    public TextOptions Current => _options;

    /// <summary>The options for a setting's value, <paramref name="system"/> standing for <c>system</c> (and anything unknown).</summary>
    internal static TextOptions Options(string? setting, SystemTextRendering system)
    {
        var mode = setting?.Trim().ToLowerInvariant() switch
        {
            Grayscale => TextRenderingMode.Antialias,
            Subpixel => TextRenderingMode.SubpixelAntialias,
            Aliased => TextRenderingMode.Alias,
            _ when !system.Antialias => TextRenderingMode.Alias,
            _ => system.Subpixel ? TextRenderingMode.SubpixelAntialias : TextRenderingMode.Antialias,
        };
        return new TextOptions { TextRenderingMode = mode, TextHintingMode = system.Hinting };
    }

    /// <summary>The system's text rendering: fontconfig on Linux, ClearType on Windows, grayscale on macOS.</summary>
    internal static SystemTextRendering ReadSystem()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            return Fontconfig.Read() ?? SystemTextRendering.Grayscale;
        }

        if (OperatingSystem.IsWindows())
        {
            return Windows.Read();
        }

        // macOS draws text in grayscale since 10.14, and doesn't hint it.
        return new SystemTextRendering(Antialias: true, Subpixel: false, TextHintingMode.Unspecified);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _templated.Dispose();
    }

    /// <summary>
    /// Shows a choice without saving it (the first-run setup's appearance step, as it previews a theme); null goes back to
    /// the setting's. Call it on the UI thread.
    /// </summary>
    public void Preview(string? setting)
    {
        _preview = setting;
        Refresh();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Affects(Setting))
        {
            Ui.Post(Refresh);
        }
    }

    private void Refresh()
    {
        var options = Options(_preview ?? _settings.GetString(Setting), _system.Value);
        if (options == _options)
        {
            return;
        }

        _options = options;
        foreach (var topLevel in Live())
        {
            Apply(topLevel, options, redraw: true);
        }
    }

    private void Templated(TopLevel topLevel)
    {
        Apply(topLevel, _options, redraw: false);
        lock (_topLevels)
        {
            _topLevels.RemoveAll(r => !r.TryGetTarget(out var t) || t == topLevel);
            _topLevels.Add(new WeakReference<TopLevel>(topLevel));
        }
    }

    private List<TopLevel> Live()
    {
        lock (_topLevels)
        {
            _topLevels.RemoveAll(r => !r.TryGetTarget(out _));
            return [.. _topLevels.Select(r => r.TryGetTarget(out var t) ? t : null).OfType<TopLevel>()];
        }
    }

    /// <summary>
    /// Sets the options on a top level; its descendants are drawn with them. A visual already drawn keeps what it drew
    /// until it's invalidated, so a change redraws each one.
    /// </summary>
    internal static void Apply(TopLevel topLevel, TextOptions options, bool redraw)
    {
        TextOptions.SetTextOptions(topLevel, options);
        if (redraw)
        {
            foreach (var visual in topLevel.GetVisualDescendants())
            {
                visual.InvalidateVisual();
            }

            topLevel.InvalidateVisual();
        }
    }

    /// <summary>fontconfig's answer for the default sans-serif font: what GTK, Qt and Chromium draw with.</summary>
    internal static partial class Fontconfig
    {
        private const string Library = "libfontconfig.so.1";

        /// <summary>FC_RGBA_RGB: the one subpixel order Skia's subpixel smoothing draws.</summary>
        private const int RgbaRgb = 1;

        /// <summary>Reads it, or null when fontconfig can't be loaded or matches nothing.</summary>
        public static SystemTextRendering? Read()
        {
            try
            {
                var pattern = FcNameParse("sans-serif"u8);
                if (pattern == 0)
                {
                    return null;
                }

                try
                {
                    // The match applies the rules a desktop writes for fonts (KDE's fonts.conf edits the matched font).
                    _ = FcConfigSubstitute(0, pattern, 0); // FcMatchPattern
                    FcDefaultSubstitute(pattern);
                    var font = FcFontMatch(0, pattern, out _);
                    if (font == 0)
                    {
                        return null;
                    }

                    try
                    {
                        return From(
                            Bool(font, "antialias"u8),
                            Int(font, "rgba"u8),
                            Bool(font, "hinting"u8),
                            Int(font, "hintstyle"u8));
                    }
                    finally
                    {
                        FcPatternDestroy(font);
                    }
                }
                finally
                {
                    FcPatternDestroy(pattern);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// The rendering for fontconfig's values (null: not set). Subpixel only for RGB: Skia draws no other order, and a
        /// wrong order is worse than grayscale. Hint styles: none, slight, medium and full.
        /// </summary>
        internal static SystemTextRendering From(bool? antialias, int? rgba, bool? hinting, int? hintStyle) => new(
            antialias ?? true,
            rgba == RgbaRgb,
            hinting == false ? TextHintingMode.None : hintStyle switch
            {
                0 => TextHintingMode.None,
                2 or 3 => TextHintingMode.Strong,
                _ => TextHintingMode.Light,
            });

        private static unsafe bool? Bool(nint pattern, ReadOnlySpan<byte> name)
        {
            fixed (byte* p = name)
            {
                return FcPatternGetBool(pattern, p, 0, out var value) == 0 ? value != 0 : null;
            }
        }

        private static unsafe int? Int(nint pattern, ReadOnlySpan<byte> name)
        {
            fixed (byte* p = name)
            {
                return FcPatternGetInteger(pattern, p, 0, out var value) == 0 ? value : null;
            }
        }

        private static unsafe nint FcNameParse(ReadOnlySpan<byte> name)
        {
            Span<byte> terminated = stackalloc byte[name.Length + 1];
            name.CopyTo(terminated);
            fixed (byte* p = terminated)
            {
                return FcNameParse(p);
            }
        }

        [LibraryImport(Library, EntryPoint = "FcNameParse")]
        private static unsafe partial nint FcNameParse(byte* name);

        [LibraryImport(Library)]
        private static partial int FcConfigSubstitute(nint config, nint pattern, int kind);

        [LibraryImport(Library)]
        private static partial void FcDefaultSubstitute(nint pattern);

        [LibraryImport(Library)]
        private static partial nint FcFontMatch(nint config, nint pattern, out int result);

        [LibraryImport(Library)]
        private static unsafe partial int FcPatternGetBool(nint pattern, byte* name, int n, out int value);

        [LibraryImport(Library)]
        private static unsafe partial int FcPatternGetInteger(nint pattern, byte* name, int n, out int value);

        [LibraryImport(Library)]
        private static partial void FcPatternDestroy(nint pattern);
    }

    /// <summary>Windows' font smoothing: off, standard (grayscale) or ClearType.</summary>
    internal static partial class Windows
    {
        private const uint SpiGetFontSmoothing = 0x004A;
        private const uint SpiGetFontSmoothingType = 0x200A;
        private const uint SpiGetFontSmoothingOrientation = 0x2012;
        private const int FontSmoothingClearType = 2;
        private const int FontSmoothingOrientationRgb = 1;

        public static SystemTextRendering Read()
        {
            if (!OperatingSystem.IsWindows())
            {
                return SystemTextRendering.Grayscale;
            }

            try
            {
                return From(
                    Get(SpiGetFontSmoothing) is { } on ? on != 0 : null,
                    Get(SpiGetFontSmoothingType),
                    Get(SpiGetFontSmoothingOrientation));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return new SystemTextRendering(Antialias: true, Subpixel: true, TextHintingMode.Unspecified);
            }
        }

        /// <summary>The rendering for Windows' values (null: not read): ClearType is subpixel, in RGB order only.</summary>
        internal static SystemTextRendering From(bool? smoothing, int? type, int? orientation) => new(
            smoothing ?? true,
            (type ?? FontSmoothingClearType) == FontSmoothingClearType && (orientation ?? FontSmoothingOrientationRgb) == FontSmoothingOrientationRgb,
            TextHintingMode.Unspecified);

        private static int? Get(uint action) => SystemParametersInfoW(action, 0, out var value, 0) ? value : null;

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
    }
}
