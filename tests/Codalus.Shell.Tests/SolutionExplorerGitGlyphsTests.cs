using System.Diagnostics;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Solution Explorer's source control glyphs (#357), the classic IDE's, left of each row's icon: a lock on a committed and
/// unchanged file, a check mark on a changed one, a plus on a new one and an exclamation mark on a conflict. A project
/// row has its project file's, the solution row its solution file's; folders and what isn't in git have none.
/// </summary>
public sealed class SolutionExplorerGitGlyphsTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "codalus-git-glyphs");

    private static string P(params string[] segments) => Path.Combine([s_root, .. segments]);

    [Fact]
    public void Each_rows_glyph_is_its_files_state_and_folders_have_none()
    {
        var states = new Dictionary<string, GitFileState>(SolutionService.PathComparer)
        {
            [P("App.slnx")] = GitFileState.Unchanged,
            [P("src", "App", "App.csproj")] = GitFileState.Modified,
            [P("src", "App", "New.cs")] = GitFileState.Added,
            [P("src", "App", "Merged.cs")] = GitFileState.Conflict,
            [P("src", "App")] = GitFileState.Unchanged, // a folder is never asked
        };
        GitFileState State(string path) => states.GetValueOrDefault(path);
        string? Glyph(ExplorerNode node) => new ExplorerRowViewModel(node, 0, false, git: State).GitGlyph;

        Assert.Equal("git-unchanged", Glyph(new ExplorerNode("solution", ExplorerNodeKind.Solution, "Solution 'App'") { Path = P("App.slnx") }));
        Assert.Equal("git-modified", Glyph(new ExplorerNode("p:app", ExplorerNodeKind.Project, "App") { Path = P("src", "App", "App.csproj") }));
        Assert.Equal("git-added", Glyph(new ExplorerNode("f:new", ExplorerNodeKind.File, "New.cs") { Path = P("src", "App", "New.cs") }));
        Assert.Equal("git-conflict", Glyph(new ExplorerNode("f:merged", ExplorerNodeKind.File, "Merged.cs") { Path = P("src", "App", "Merged.cs") }));
        Assert.Null(Glyph(new ExplorerNode("f:ignored", ExplorerNodeKind.File, "Ignored.cs") { Path = P("src", "App", "Ignored.cs") }));
        Assert.Null(Glyph(new ExplorerNode("d:app", ExplorerNodeKind.Folder, "App") { Path = P("src", "App") }));

        // In folder mode the solution's path is its folder: no glyph for it.
        Assert.Null(Glyph(new ExplorerNode("solution", ExplorerNodeKind.Solution, "Folder 'App'") { Path = P("src", "App") }));

        // Without git (the shell's hasn't started) there is none at all.
        Assert.Null(new ExplorerRowViewModel(new ExplorerNode("f:new", ExplorerNodeKind.File, "New.cs") { Path = P("src", "App", "New.cs") }, 0, false).GitGlyph);
    }

    [Fact]
    public void A_change_of_state_updates_the_glyph_the_tooltip_and_the_accessible_name()
    {
        var path = P("src", "App", "Program.cs");
        var state = GitFileState.Unchanged;
        var row = new ExplorerRowViewModel(new ExplorerNode("f:program", ExplorerNodeKind.File, "Program.cs") { Path = path }, 1, false, git: _ => state);
        Assert.Equal("git-unchanged", row.GitGlyph);
        Assert.Equal(path + "\nCommitted, unchanged", row.ToolTip);
        Assert.Equal("Program.cs, Committed, unchanged", row.AccessibleName);

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.RefreshGit();
        Assert.Empty(changed); // the same state raises nothing

        state = GitFileState.Modified;
        row.RefreshGit();
        Assert.Equal("git-modified", row.GitGlyph);
        Assert.Contains(nameof(ExplorerRowViewModel.GitGlyph), changed);
        Assert.Contains(nameof(ExplorerRowViewModel.ToolTip), changed);
        Assert.Equal("Program.cs, Changed", row.AccessibleName);
    }

    /// <summary>
    /// Each glyph is a filled shape compiled at build time, in the classic IDE's colors (a blue lock, a red check mark, a
    /// green plus, red for a conflict), drawn on its own at 12 px in the column left of the row's icon.
    /// </summary>
    [AvaloniaFact]
    public void The_glyphs_are_filled_shapes_in_their_colors()
    {
        foreach (var (kind, color) in new[] { ("git-unchanged", CommandIcons.Blue), ("git-modified", CommandIcons.Red), ("git-added", CommandIcons.Green), ("git-conflict", CommandIcons.Red) })
        {
            Assert.NotNull(IconSet.GetFilled(kind));
            Assert.True(CommandIcons.HasColor(kind), kind);
            Assert.Equal(color, CommandIcons.ColorOf(kind));
        }

        // The check mark on its own, as the row draws it: red, a solid mark (not a hairline), and nothing else.
        var icon = new Icon { Kind = "git-modified", Width = 12, Height = 12 };
        var window = new Window { Width = 40, Height = 40, Background = Avalonia.Media.Brushes.White, Content = new Panel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Children = { icon } } };
        window.Show();
        try
        {
            ShellFixture.RenderFrame(window);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!;
            using var buffer = frame.Lock();
            Avalonia.Media.Color Pixel(int x, int y)
            {
                var offset = (y * buffer.RowBytes) + (x * 4);
                var (b0, b1, b2, b3) = (System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 1), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 2), System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + 3));
                return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Avalonia.Media.Color.FromArgb(b3, b0, b1, b2) : Avalonia.Media.Color.FromArgb(b3, b2, b1, b0);
            }

            var scale = frame.PixelSize.Width / window.Bounds.Width;
            // Solid red where the mark is, its antialiased edge pink on the white, and nothing in another hue.
            int red = 0, other = 0;
            for (var y = 0; y < (int)(16 * scale); y++)
            {
                for (var x = 0; x < (int)(16 * scale); x++)
                {
                    var c = Pixel(x, y);
                    if (c is { R: > 245, G: > 245, B: > 245 })
                    {
                        continue;
                    }

                    if (c.R > 150 && c.G < 110 && c.B < 110)
                    {
                        red++;
                    }
                    else if (c.G > c.R || c.B > c.R)
                    {
                        other++;
                    }
                }
            }

            Assert.True(red >= 8 * scale * scale, $"red: {red}");
            Assert.Equal(0, other);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ in the shell, on a real repository

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} (exit code {process.ExitCode}, 0x{process.ExitCode:X8}): {error.GetAwaiter().GetResult()}");
    }

    private static ExplorerRowViewModel? Row(SolutionExplorerViewModel explorer, string id) => explorer.Rows.FirstOrDefault(r => r.Id == id);

    /// <summary>With <c>CODALUS_UI_SNAPSHOTS</c>, the window in Harbor Light, Classic Dark and Harbor High contrast, saved there.</summary>
    private static void Snapshots(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var before = window.RequestedThemeVariant;
        foreach (var variant in new[] { Codalus.Shell.Theming.ThemeService.HarborLight, Avalonia.Styling.ThemeVariant.Dark, Codalus.Shell.Theming.ThemeService.HarborHighContrast })
        {
            window.RequestedThemeVariant = variant;
            for (var i = 0; i < 3; i++)
            {
                ShellFixture.Pump();
            }

            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            frame?.Save(Path.Combine(folder, $"{name}-{variant.Key}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        window.RequestedThemeVariant = before;
        ShellFixture.Pump();
    }

    /// <summary>
    /// A folder in a repository, in the folder view: the committed README has the lock, a changed guide the check mark, a
    /// new page the plus, the ignored output none, and every row keeps the glyph column. An edit the status then reads turns
    /// the README's lock into a check mark; with the setting off every glyph and the column go, and with it on again they
    /// come back.
    /// </summary>
    [AvaloniaFact]
    public async Task The_folder_views_rows_show_their_files_state_and_follow_the_status()
    {
        await using var f = ShellFixture.Create();
        var ct = TestContext.Current.CancellationToken;
        var folder = Path.Combine(f.UserData, "ws");
        var readme = Path.Combine(folder, "README.md");
        var guide = Path.Combine(folder, "guide.md");
        var page = Path.Combine(folder, "page.md");
        var output = Path.Combine(folder, "out.log");
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(readme, "# Readme\n", ct);
        await File.WriteAllTextAsync(guide, "# Guide\n", ct);
        await File.WriteAllTextAsync(Path.Combine(folder, ".gitignore"), "*.log\n", ct);
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        await File.WriteAllTextAsync(Path.Combine(folder, "docs", "notes.md"), "# Notes\n", ct);
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        await File.AppendAllTextAsync(guide, "More.\n", ct);
        await File.WriteAllTextAsync(page, "# Page\n", ct);
        await File.WriteAllTextAsync(output, "log\n", ct);

        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderWorkspaceAsync(folder);
        var explorer = f.Shell.Explorer;
        string Describe() => string.Join(", ", explorer.Rows.Select(r => $"{r.Name}={r.GitState}"));
        await ShellFixture.PumpUntil(() => explorer.IsFolderView && Row(explorer, "f:" + readme)?.GitState == GitFileState.Unchanged, seconds: 60, describe: Describe);
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + guide)?.GitState == GitFileState.Modified && Row(explorer, "f:" + page)?.GitState == GitFileState.Added, seconds: 60, describe: Describe);
        Assert.Equal("git-unchanged", Row(explorer, "f:" + readme)!.GitGlyph);
        Assert.Equal("git-modified", Row(explorer, "f:" + guide)!.GitGlyph);
        Assert.Equal("git-added", Row(explorer, "f:" + page)!.GitGlyph);
        Assert.Null(Row(explorer, "f:" + output)?.GitGlyph);
        Assert.Null(explorer.Rows.First(r => r.Node.Kind == ExplorerNodeKind.Solution).GitGlyph);
        Assert.True(explorer.ShowsGitGlyphs);

        // In the view, as the classic IDE's: the glyph in its own column left of the icon, and a folder's icon (no glyph)
        // lined up with its sibling files'.
        var window = new Window { Width = 320, Height = 260, Content = new SolutionExplorerView { DataContext = explorer } };
        window.Show();
        try
        {
            ShellFixture.RenderFrame(window);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            double Left(Icon icon) => icon.TranslatePoint(default, window)!.Value.X;
            var readmeIcons = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is ExplorerRowViewModel { Id: var id } && id == "f:" + readme).GetVisualDescendants().OfType<Icon>().Where(i => i.IsEffectivelyVisible).ToArray();
            var glyph = readmeIcons.Single(i => i.Kind == "git-unchanged");
            var fileIcon = readmeIcons.Single(i => i.Kind is { } kind && !kind.StartsWith("git-", StringComparison.Ordinal) && !kind.StartsWith("chevron", StringComparison.Ordinal) && i.Bounds.Width > 12);
            Assert.True(Left(glyph) + glyph.Bounds.Width <= Left(fileIcon) + 1, $"glyph at {Left(glyph)}, icon at {Left(fileIcon)}");
            Assert.True(glyph.Bounds.Width == 12 && glyph.Bounds.Height == 12, $"glyph {glyph.Bounds}");
            var docsIcons = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.DataContext is ExplorerRowViewModel { Node.Kind: ExplorerNodeKind.Folder, Name: "docs" }).GetVisualDescendants().OfType<Icon>().Where(i => i.IsEffectivelyVisible).ToArray();
            var docsIcon = docsIcons.Single(i => i.Bounds.Width > 12);
            Assert.Equal(Left(fileIcon), Left(docsIcon), 1);
            Snapshots(window, "solution-explorer-git-glyphs");
        }
        finally
        {
            window.Close();
        }

        // An edit, read by the status: the README's lock becomes a check mark.
        await File.AppendAllTextAsync(readme, "Edited.\n", ct);
        await f.Shell.Git.RefreshAsync(ct);
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + readme)?.GitState == GitFileState.Modified, seconds: 60, describe: Describe);

        // The setting off: no glyph; on again: back.
        await f.Shell.Workbench.Settings.SetAsync(BuiltIns.ExplorerGlyphsSetting, false, SettingScope.User, ct);
        await ShellFixture.PumpUntil(() => explorer.Rows.All(r => r.GitGlyph is null) && !explorer.ShowsGitGlyphs, seconds: 60, describe: Describe);
        await f.Shell.Workbench.Settings.SetAsync(BuiltIns.ExplorerGlyphsSetting, true, SettingScope.User, ct);
        await ShellFixture.PumpUntil(() => Row(explorer, "f:" + readme)?.GitState == GitFileState.Modified && Row(explorer, "f:" + guide)?.GitState == GitFileState.Modified, seconds: 60, describe: Describe);
        Assert.True(explorer.ShowsGitGlyphs);
    }
}
