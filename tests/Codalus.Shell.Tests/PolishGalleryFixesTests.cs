using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Output;
using MediaColor = Avalonia.Media.Color;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h (the maintainer's review of the M34 gallery): the Output window colors what its lines report and the processes'
/// ANSI colors, readably in every theme; tool window toolbars color each icon by its family; every Toolbox item has an
/// icon of its own in the palette; and the theme previews are the shell's chrome in the variant's real resources.
/// </summary>
public partial class PolishTests
{
    [AvaloniaFact]
    public async Task The_output_window_colors_errors_warnings_successes_standard_error_and_ansi_readably_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        var channel = f.Shell.Workbench.Output.GetOrCreate("test.colors", "Colors");
        channel.AppendLine("Build started: App.csproj (Debug|Any CPU)");
        channel.AppendLine("/src/App/Program.cs(12,5): error CS1002: ; expected [/src/App/App.csproj]");
        channel.AppendLine("/src/App/Program.cs(3,13): warning CS0168: The variable 'e' is declared but never used");
        channel.AppendLine("Build succeeded: 0 errors, 1 warning. Elapsed 00:00:01.20.");
        channel.AppendErrorLine("the program wrote this to standard error");
        channel.AppendLine("\e[33mjust yellow\e[0m and \e[1;32mbold green\e[0m and \e[38;2;255;255;0mrgb yellow\e[0m");
        channel.Append("\e[3");
        channel.AppendLine("4msplit blue\e[0m");
        channel.AppendLine("\e[34mdark blue\e[0m and \e[1;34mbold blue\e[0m");
        channel.AppendLine("\e[32mplain green\e[0m and \e[1;32mbolder green\e[0m");

        var view = new OutputView { DataContext = f.Shell.Output };
        f.Shell.Output.Selected = channel;
        var host = new Window { Width = 700, Height = 300, Content = view };
        host.Show();
        await ShellFixture.PumpUntil(() => view.Text.Contains("split blue", StringComparison.Ordinal));

        // Escapes never show: the text is what the processes meant.
        Assert.DoesNotContain('\e', view.Text);
        Assert.Contains("just yellow and bold green and rgb yellow\nsplit blue\n", view.Text, StringComparison.Ordinal);
        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single();

        var problems = new List<string>();
        foreach (var (name, variant) in s_themes)
        {
            host.RequestedThemeVariant = variant;
            ShellFixture.Pump();
            ShellFixture.Pump();
            var background = Token("Shell.Editor.Background", variant);
            var required = ThemeService.IsHighContrast(variant) ? 7.0 : 4.5;
            MediaColor? ColorAt(int line, string text)
            {
                var documentLine = editor.Document.GetLineByNumber(line);
                var column = editor.Document.GetText(documentLine).IndexOf(text, StringComparison.Ordinal);
                Assert.True(column >= 0, $"'{text}' isn't on line {line}");
                var visual = editor.TextArea.TextView.GetOrConstructVisualLine(documentLine);
                var element = visual.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
                return (element.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color;
            }

            FontWeight WeightAt(int line, string text)
            {
                var documentLine = editor.Document.GetLineByNumber(line);
                var column = editor.Document.GetText(documentLine).IndexOf(text, StringComparison.Ordinal);
                var visual = editor.TextArea.TextView.GetOrConstructVisualLine(documentLine);
                return visual.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength).TextRunProperties.Typeface.Weight;
            }

            void Expect(int line, string text, string? token)
            {
                var color = ColorAt(line, text);
                if (token is not null && color != Token(token, variant))
                {
                    problems.Add($"{name}: '{text}' is {color}, not {token} ({Token(token, variant)})");
                }

                if (color is { } c && PolishTests.Contrast(c, background) < required - 0.005)
                {
                    problems.Add($"{name}: '{text}' is {PolishTests.Contrast(c, background):0.00}:1 on the editor's {background}");
                }
            }

            Expect(1, "Build started", null);
            Expect(2, "error CS1002", "Shell.Error.Foreground");
            Expect(3, "warning CS0168", "Shell.Warning.Foreground");
            Expect(4, "Build succeeded", "Shell.Diff.Added.Foreground");
            Expect(5, "standard error", "Shell.Error.Foreground");
            Expect(6, "just yellow", null);
            Expect(6, "bold green", null);
            Expect(6, "rgb yellow", null);
            Expect(7, "split blue", null);
            Expect(8, "dark blue", null);
            Expect(8, "bold blue", null);
            Assert.NotEqual(ColorAt(1, "Build started"), ColorAt(6, "just yellow")); // the ANSI color shows

            // Bold is the bright color at the regular weight (M50): the bold typeface was hard to read.
            Assert.Equal(WeightAt(6, "just yellow"), WeightAt(6, "bold green"));
            Assert.Equal(WeightAt(8, "dark blue"), WeightAt(8, "bold blue"));

            // ...and stands out from the plain color in every theme (M51: Harbor Light's bold green looked like its green).
            Expect(9, "plain green", null);
            Expect(9, "bolder green", null);
            // (High Contrast's palette has one green for both.)
            if (!ThemeService.IsHighContrast(variant) && ColorAt(9, "plain green") is { } plain && ColorAt(9, "bolder green") is { } bold
                && Math.Abs(PolishTests.Contrast(bold, background) - PolishTests.Contrast(plain, background)) < 1)
            {
                problems.Add($"{name}: bold green {bold} ({PolishTests.Contrast(bold, background):0.00}:1) hardly stands out from green {plain} ({PolishTests.Contrast(plain, background):0.00}:1)");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));

        // A large channel stays cheap: plain text keeps no runs, and lines are classified only as they're drawn.
        var big = f.Shell.Workbench.Output.GetOrCreate("test.big", "Big");
        for (var i = 0; i < 20_000; i++)
        {
            big.AppendLine($"  Lib -> /src/Lib/bin/Debug/net9.0/Lib{i}.dll");
        }

        f.Shell.Output.Selected = big;
        await ShellFixture.PumpUntil(() => view.Text.Length == big.GetText().Length);
        Assert.Empty(view.Coloring.Runs);
        host.Close();
    }

    [Fact]
    public void Ansi_colors_are_raised_to_the_contrast_the_theme_needs_and_keep_their_hue()
    {
        var white = Colors.White;
        var yellow = MediaColor.FromRgb(0x94, 0x98, 0x00); // VS Code's light yellow, 2.9:1 on white
        var readable = OutputPresenter.Readable(yellow, white, 4.5);
        Assert.True(OutputPresenter.Contrast(readable, white) >= 4.5);
        Assert.True(readable.R > readable.B && readable.G > readable.B, $"{readable} isn't yellowish any more");
        var black = MediaColor.FromRgb(0, 0, 0);
        Assert.True(OutputPresenter.Contrast(OutputPresenter.Readable(black, MediaColor.FromRgb(0x1E, 0x1E, 0x1E), 7), MediaColor.FromRgb(0x1E, 0x1E, 0x1E)) >= 7);
        Assert.Equal(MediaColor.FromRgb(0x00, 0x00, 0xFF), OutputPresenter.Readable(MediaColor.FromRgb(0x00, 0x00, 0xFF), white, 4.5)); // already readable
    }

    [AvaloniaFact]
    public async Task Tool_window_toolbars_color_each_icon_by_its_family()
    {
        await using var f = ShellFixture.Create();
        var variant = ThemeService.HarborLight;
        MediaColor IconColor(Icon icon) => icon.IsSet(Icon.ForegroundProperty) && icon.Foreground is ISolidColorBrush set
            ? set.Color
            : Token("Shell.Icon." + Capitalized(CommandIcons.DefaultColorOf(icon.Kind!)!), variant);

        IReadOnlyList<(string Name, MediaColor Color)> Toolbar(Control view)
        {
            var host = new Window { Width = 700, Height = 300, RequestedThemeVariant = variant, Content = view };
            host.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            var icons = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolbar")) // toggle buttons too
                .Select(b => (Name: Avalonia.Automation.AutomationProperties.GetName(b), Icon: b.Content as Icon ?? (b.Content as Panel)?.Children.OfType<Icon>().FirstOrDefault()))
                .Where(i => i.Icon?.Kind is not null && !i.Icon.Kind.StartsWith("chevron", StringComparison.Ordinal) && i.Icon.Kind != "more")
                .Select(i => (i.Name ?? i.Icon!.Kind!, IconColor(i.Icon!)))
                .ToList();
            host.Content = null;
            host.Close();
            return icons;
        }

        // Solution Explorer (the maintainer's finding): Home blue, Sync green, Track Active Item blue (M50), Collapse All and
        // Switch Views purple, Show All Files tan, Properties blue.
        var explorer = Toolbar(new SolutionExplorerView { DataContext = f.Shell.Explorer });
        Assert.True(explorer.Count == 7, string.Join(", ", explorer));
        Assert.Equal(Token("Shell.Icon.Blue", variant), explorer.Single(i => i.Name == "Track Active Item").Color);
        Assert.True(explorer.Select(i => i.Color).Distinct().Count() >= 4, string.Join(", ", explorer));
        Assert.Equal(Token("Shell.Icon.Blue", variant), explorer.Single(i => i.Name == "Home").Color);
        Assert.Equal(Token("Shell.Icon.Green", variant), explorer.Single(i => i.Name == "Sync with Active Document").Color);
        Assert.Equal(Token("Shell.Icon.Folder", variant), explorer.Single(i => i.Name == "Show All Files").Color);
        Assert.Equal(Token("Shell.Icon.Purple", variant), explorer.Single(i => i.Name == "Switch Views").Color);
        Assert.Equal(Token("Shell.Icon.Purple", variant), explorer.Single(i => i.Name == "Collapse All").Color);

        // The others that were one color: Git Changes' Git commands in the Git family's color (as in the menus), the Live
        // Visual Tree, and refresh green everywhere.
        var git = Toolbar(new GitChangesView { DataContext = f.Shell.GitChanges });
        foreach (var command in new[] { "Fetch", "Pull", "Push", "Sync" })
        {
            Assert.Equal(Token("Shell.Icon.Git", variant), git.Single(i => i.Name == command).Color);
        }

        var liveTree = Toolbar(new LiveTreeView { DataContext = new LiveTreeViewModel(() => null, _ => Task.CompletedTask) });
        Assert.True(liveTree.Select(i => i.Color).Distinct().Count() >= 3, string.Join(", ", liveTree));
        Assert.Equal(CommandIcons.Green, CommandIcons.ColorOf("refresh"));
    }

    [AvaloniaFact]
    public async Task Every_Toolbox_item_has_an_icon_of_its_own_in_the_palette()
    {
        await using var f = ShellFixture.Create();
        var toolbox = new ToolboxViewModel { IsAvailable = true };
        var items = toolbox.Categories.SelectMany(c => c.Items).Where(i => !i.IsPointer).ToList();
        Assert.True(items.Count >= 50);

        // One icon per item (the maintainer: "Toolbox icons are repeated"), each drawn and colored.
        var repeated = items.GroupBy(i => i.Icon).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(i => i.Name))}").ToList();
        Assert.True(repeated.Count == 0, "icons shared by several items:\n" + string.Join('\n', repeated));
        foreach (var icon in items.Select(i => i.Icon).Append(ToolboxViewModel.PointerIcon).Append(ToolboxViewModel.GenericControlIcon).Append(ToolboxViewModel.GenericComponentIcon))
        {
            Assert.True(IconSet.GetFilled(icon) is not null || IconSet.Get(icon) is not null, $"{icon} draws nothing");
            Assert.True(CommandIcons.HasColor(icon), $"{icon} has no palette color: add it to CommandIcons' kind colors");
        }

        // The common controls the maintainer listed, each with its own (not the generic) icon.
        string[] common =
        [
            "Button", "CheckBox", "RadioButton", "Label", "LinkLabel", "TextBox", "RichTextBox", "MaskedTextBox", "ComboBox", "ListBox", "CheckedListBox",
            "ListView", "TreeView", "DataGridView", "PictureBox", "ProgressBar", "TrackBar", "NumericUpDown", "DateTimePicker", "MonthCalendar", "Panel",
            "GroupBox", "TabControl", "SplitContainer", "FlowLayoutPanel", "TableLayoutPanel", "MenuStrip", "ToolStrip", "StatusStrip", "ContextMenuStrip", "Timer",
        ];
        foreach (var name in common)
        {
            var item = items.Single(i => i.Name == name);
            Assert.NotEqual(ToolboxViewModel.GenericControlIcon, item.Icon);
            Assert.NotEqual(ToolboxViewModel.GenericComponentIcon, item.Icon);
        }

        // Unknown and third-party controls: the control they end with, else the generic control or component.
        Assert.Equal("control-button", ToolboxViewModel.IconFor("Telerik.WinControls.UI.RadButton", isControl: true));
        Assert.Equal("control-rich-text-box", ToolboxViewModel.IconFor("Shop.Controls.OrderRichTextBox", isControl: true));
        Assert.Equal(ToolboxViewModel.GenericControlIcon, ToolboxViewModel.IconFor("Contoso.Gauges.Speedometer", isControl: true));
        Assert.Equal(ToolboxViewModel.GenericComponentIcon, ToolboxViewModel.IconFor("Contoso.Audit.Recorder", isControl: false));
        toolbox.SetProjectItems([new("Shop.Controls.OrderTextBox", "OrderTextBox", "Shop", true), new("Shop.Controls.Speedometer", "Speedometer", "Shop", true), new("Shop.Recorder", "Recorder", "Shop", false)]);
        var project = toolbox.Categories.First().Items.Where(i => !i.IsPointer).ToDictionary(i => i.Name, i => i.Icon);
        Assert.Equal("control-text-box", project["OrderTextBox"]);
        Assert.Equal(ToolboxViewModel.GenericControlIcon, project["Speedometer"]);
        Assert.Equal(ToolboxViewModel.GenericComponentIcon, project["Recorder"]);
        Assert.Equal(ToolboxViewModel.PointerIcon, toolbox.Categories.First().Items[0].Icon);

        // The Toolbox as drawn: every icon colored, readable, nothing cut off, in every theme.
        var problems = InspectInEveryTheme(new ToolboxView { DataContext = new ToolboxViewModel { IsAvailable = true } }, ToolWindowMinWidth, 900);
        Assert.True(problems.Count == 0, string.Join('\n', problems));

        // With CODALUS_UI_SNAPSHOTS, the Toolbox's frame in a light, a dark and a High Contrast theme, for a look (its
        // gallery picture with the designer comes from Windows only).
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            foreach (var (name, variant) in new[] { ("harbor-light", ThemeService.HarborLight), ("dark", ThemeVariant.Dark), ("graphite-high-contrast", ThemeService.GraphiteHighContrast) })
            {
                var host = new Window { Width = ToolWindowMinWidth, Height = 1500, RequestedThemeVariant = variant, Content = new ToolboxView { DataContext = new ToolboxViewModel { IsAvailable = true } } };
                host.Show();
                ShellFixture.Pump();
                ShellFixture.Pump();
                using var frame = host.CaptureRenderedFrame();
                frame?.Save(Path.Combine(snapshots, $"toolbox-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                host.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task A_theme_preview_is_the_shells_chrome_in_its_variants_real_resources()
    {
        await using var f = ShellFixture.Create();
        foreach (var (id, variant) in ThemeService.Themes)
        {
            // In a window of the same variant (the setup's chosen card; it drew no fills once) and of another.
            foreach (var windowVariant in new[] { variant, variant == ThemeService.EmberDark ? ThemeService.HarborLight : ThemeService.EmberDark })
            {
                var picture = new ThemePicture(variant);
                var view = new ThemePictureView { DataContext = picture };
                var host = new Window { Width = 178, Height = 100, RequestedThemeVariant = windowVariant, Content = view, Background = Brushes.Magenta };
                host.Show();
                for (var i = 0; i < 3; i++)
                {
                    ShellFixture.Pump();
                }

                var scope = view.GetVisualDescendants().OfType<ThemeVariantScope>().Single();
                Assert.Equal(variant, scope.ActualThemeVariant);
                var borders = scope.GetVisualDescendants().OfType<Border>().ToList();
                MediaColor? Fill(Border b) => (b.Background as ISolidColorBrush)?.Color;
                Assert.Contains(borders, b => b.Height == 24 && Fill(b) == Token("Shell.TitleBar", variant));
                Assert.Contains(borders, b => b.Height == 24 && Fill(b) == Token("Shell.Toolbar", variant));
                Assert.Contains(borders, b => b.Height == 20 && Fill(b) == Token("Shell.StatusBar", variant));
                Assert.Contains(borders, b => b.Height == 20 && Fill(b) == Token("Shell.Caption.Active", variant));
                Assert.Contains(borders, b => b.Classes.Contains("selected") && Fill(b) == Token("Shell.Selection", variant));
                Assert.Contains(borders, b => Fill(b) == Token("Shell.Editor.Background", variant));
                Assert.Contains(borders, b => Fill(b) == Token("Shell.Tab.Active", variant));

                // The editor's code in the variant's syntax colors, the icons in its palette (white in High Contrast).
                var keyword = EditorTheme.For(id).TokenColor("keyword")!.Value;
                Assert.Contains(scope.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "public " && (t.Foreground as ISolidColorBrush)?.Color == MediaColor.FromUInt32(keyword));
                Assert.All(scope.GetVisualDescendants().OfType<Icon>(), icon => Assert.Equal(variant, icon.ActualThemeVariant));

                // As rendered: the title bar's and the status bar's pixels are the variant's tokens.
                using (var frame = host.CaptureRenderedFrame())
                {
                    Assert.NotNull(frame);
                    Assert.Equal(Token("Shell.TitleBar", variant), Pixel(frame!, 172, 3));
                    Assert.Equal(Token("Shell.StatusBar", variant), Pixel(frame!, 100, 96));
                }

                host.Content = null;
                host.Close();
            }
        }
    }

    private static MediaColor Token(string key, ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException($"{key} in {variant}");

    private static string Capitalized(string color) => char.ToUpperInvariant(color[0]) + color[1..];

    private static MediaColor Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var offset = (y * buffer.RowBytes) + (x * 4);
        var (b0, b1, b2, b3) = (Marshal.ReadByte(buffer.Address, offset), Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset + 2), Marshal.ReadByte(buffer.Address, offset + 3));
        return buffer.Format == PixelFormat.Rgba8888 ? MediaColor.FromArgb(b3, b0, b1, b2) : MediaColor.FromArgb(b3, b2, b1, b0);
    }
}
