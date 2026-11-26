using System.Globalization;
using System.Text.Json;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>workbench.uiScale</c> (#556, Linux): a factor over the display's scale, for a desktop whose scale Codalus doesn't
/// read (an X11 session without <c>Xft.dpi</c>) or to make the whole UI larger or smaller. Avalonia's X11 backend reads its
/// own <c>AVALONIA_GLOBAL_SCALE_FACTOR</c> once, as it starts, and multiplies the scale it found by it: the window is drawn
/// at that many pixels, so text stays as sharp as at the system's own scale. Windows and macOS have no such factor (the
/// system's scale is the app's). So it's read from the user's settings before the windowing system starts, takes effect
/// at the next start, and the variable is removed once Avalonia read it, so no process Codalus starts (its hosts, the
/// terminal, the user's own Avalonia app) inherits it. A variable of Avalonia's the user set themselves wins.
/// </summary>
internal static class UiScale
{
    public const string Setting = "workbench.uiScale";

    /// <summary>Avalonia's factor over the scale it found.</summary>
    public const string GlobalVariable = "AVALONIA_GLOBAL_SCALE_FACTOR";

    /// <summary>Avalonia's per-screen scales, which replace the scale it finds.</summary>
    public const string ScreenVariable = "AVALONIA_SCREEN_SCALE_FACTORS";

    public const double Min = 0.5;
    public const double Max = 4;

    /// <summary>The factor in a settings file's text, or null when it's unset, 1, or not a number in range.</summary>
    internal static double? Read(string settingsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(settingsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(Setting, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var factor)
                && factor is >= Min and <= Max
                && factor != 1
                    ? factor
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The factor to hand Avalonia for <paramref name="settingsFile"/>, or null: not on Linux, a variable of Avalonia's
    /// already set, or no factor in the file.
    /// </summary>
    internal static double? Factor(string settingsFile, Func<string, string?> environment, bool linux)
    {
        if (!linux || !string.IsNullOrEmpty(environment(GlobalVariable)) || !string.IsNullOrEmpty(environment(ScreenVariable)))
        {
            return null;
        }

        try
        {
            return File.Exists(settingsFile) ? Read(File.ReadAllText(settingsFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Sets Avalonia's factor for the start; returns whether it did (then <see cref="Started"/> removes it).</summary>
    public static bool Apply(string settingsFile)
    {
        if (Factor(settingsFile, Environment.GetEnvironmentVariable, OperatingSystem.IsLinux()) is not { } factor)
        {
            return false;
        }

        Environment.SetEnvironmentVariable(GlobalVariable, factor.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Removes the factor <see cref="Apply"/> set, once the windowing system read it.</summary>
    public static void Started() => Environment.SetEnvironmentVariable(GlobalVariable, null);
}
