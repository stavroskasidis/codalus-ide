using System.Runtime.InteropServices;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Daedalus.Shell.Theming;
using Daedalus.Workbench.Settings;

namespace Daedalus.Shell.Tests;

/// <summary>
/// Renders the main surfaces in every theme with Skia. Catches templates that fail to render; with
/// <c>DAEDALUS_UI_SNAPSHOTS=&lt;dir&gt;</c> the frames are saved for a visual review.
/// </summary>
public class RenderingTests
{
    [AvaloniaFact]
    public async Task Main_surfaces_render_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1280;
        f.Window.Height = 800;
        var snapshots = Environment.GetEnvironmentVariable("DAEDALUS_UI_SNAPSHOTS");

        // A C# file outside any project: the language service still colors it (Miscellaneous Files).
        var folder = f.CopySample("LanguageFeatures");
        var editor = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        await ShellFixture.PumpUntil(() => editor!.Controller?.Colorizer.HasTokens == true, seconds: 120);

        foreach (var theme in new[] { "light", "dark", "highContrast" })
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);

            f.Shell.Layout.OpenDocument(editor!);
            Capture(f, snapshots, $"{theme}-editor");

            f.Shell.OpenSettings();
            Capture(f, snapshots, $"{theme}-settings");

            f.Shell.OpenKeyboardShortcuts();
            Capture(f, snapshots, $"{theme}-shortcuts");

            f.Shell.Workbench.Notifications.Warning("A warning toast with an action.", "Do It");
            f.Shell.ShowCommandPalette();
            f.Type("set");
            Capture(f, snapshots, $"{theme}-palette");
            f.Shell.Palette.Cancel();
            f.Shell.Notifications.HideToasts();
        }
    }

    private static void Capture(ShellFixture f, string? directory, string name)
    {
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        using var frame = f.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(DistinctColors(frame) > 8, $"{name}: the frame is (nearly) blank.");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>Distinct colors on a coarse grid; a blank or failed render has one or two.</summary>
    private static int DistinctColors(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var colors = new HashSet<int>();
        for (var y = 0; y < buffer.Size.Height; y += 7)
        {
            for (var x = 0; x < buffer.Size.Width; x += 7)
            {
                colors.Add(Marshal.ReadInt32(buffer.Address, (y * buffer.RowBytes) + (x * 4)));
            }
        }

        return colors.Count;
    }
}
