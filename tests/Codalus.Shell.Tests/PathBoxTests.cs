using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Paths are typed or browsed for (the maintainer's 0.5 review: New Project's location had no way to open the OS's
/// picker): every path field has the same Browse button, the input dialog's included.
/// </summary>
public sealed partial class PathBoxTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>With <c>CODALUS_UI_SNAPSHOTS</c>, the window saved there as <paramref name="name"/>.</summary>
    private static void Snapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            frame!.Save(Path.Combine(snapshots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    [AvaloniaFact]
    public async Task A_path_input_browses_from_the_typed_folder_and_takes_the_choice()
    {
        await using var f = ShellFixture.Create();
        var repos = Directory.CreateDirectory(Path.Combine(f.UserData, "repos")).FullName;
        var chosen = Directory.CreateDirectory(Path.Combine(f.UserData, "elsewhere")).FullName;
        (PathKind Kind, string? Start)? asked = null;
        f.Shell.Dialogs.Picker = (kind, start) =>
        {
            asked = (kind, start);
            return Task.FromResult<string?>(chosen);
        };

        // The typed folder doesn't exist yet: the picker opens in the nearest one that does.
        var answer = f.Shell.Dialogs.InputAsync("Location", "The folder to create it in.", Path.Combine(repos, "new", "deeper"), browse: PathKind.Folder);
        ShellFixture.Pump();
        var view = f.Window.GetVisualDescendants().OfType<DialogView>().Single();
        var browse = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BrowseButton");
        Assert.True(browse.IsEffectivelyVisible);
        Assert.Equal("Browse…", ToolTip.GetTip(browse));
        Assert.Equal("folder-open", ButtonIcon.GetKind(browse));
        Snapshot(f.Window, "path-input");

        Click(browse);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.InputText == chosen);
        Assert.Equal((PathKind.Folder, repos), asked);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "OK").Command.Execute(null);
        Assert.Equal(chosen, await answer);

        // A name isn't a path: no button.
        var name = f.Shell.Dialogs.InputAsync("Project name", "In " + repos, "ConsoleApp1");
        ShellFixture.Pump();
        Assert.False(browse.IsEffectivelyVisible);
        f.Shell.Dialogs.Cancel();
        Assert.Null(await name);
    }

    [AvaloniaFact]
    public async Task A_path_relative_to_a_project_stays_relative_when_browsed_inside_it()
    {
        await using var f = ShellFixture.Create();
        var project = Directory.CreateDirectory(Path.Combine(f.UserData, "Shop")).FullName;
        var inside = Directory.CreateDirectory(Path.Combine(project, "Data", "Compiled")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(f.UserData, "Shared")).FullName;
        var starts = new List<string?>();
        var picks = new Queue<string>([inside, outside]);
        f.Shell.Dialogs.Picker = (_, start) =>
        {
            starts.Add(start);
            return Task.FromResult<string?>(picks.Dequeue());
        };

        var answer = f.Shell.Dialogs.InputAsync("Compiled Model", "The folder (relative to the project):", "CompiledModels", browse: PathKind.Folder, browseFolder: project);
        ShellFixture.Pump();
        var browse = f.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BrowseButton");
        Click(browse);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.InputText != "CompiledModels");
        Assert.Equal(Path.Combine("Data", "Compiled"), f.Shell.Dialogs.InputText);

        // From the relative path's folder; a folder outside the project stays whole.
        Click(browse);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.InputText == outside);
        Assert.Equal([project, inside], starts);
        f.Shell.Dialogs.Cancel();
        await answer;
    }

    [AvaloniaFact]
    public async Task A_path_setting_has_the_browse_button_and_a_choice_is_saved()
    {
        await using var f = ShellFixture.Create();
        var jdk = Directory.CreateDirectory(Path.Combine(f.UserData, "jdk-21")).FullName;
        f.Shell.OpenSettings();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SettingsView>().Any());
        var settings = (SettingsViewModel)f.Window.GetVisualDescendants().OfType<SettingsView>().Single().DataContext!;
        settings.Search = "android.jdkDirectory";
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PathBox>().Any(b => b.DataContext is PathSettingViewModel));
        var box = f.Window.GetVisualDescendants().OfType<PathBox>().Single(b => b.DataContext is PathSettingViewModel { Key: "android.jdkDirectory" });
        Assert.Equal(PathKind.Folder, box.Kind);
        box.Picker = (_, _) => Task.FromResult<string?>(jdk);

        Click(box.BrowseButton);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString("android.jdkDirectory") == jdk);
        Assert.Equal(jdk, box.TextBox.Text);
        Snapshot(f.Window, "path-setting");
    }

    [AvaloniaFact]
    public async Task A_path_box_writes_through_its_binding_and_names_its_text_box()
    {
        await using var f = ShellFixture.Create();
        var project = Directory.CreateDirectory(Path.Combine(f.UserData, "App")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(project, "bin", "publish")).FullName;
        var model = new PublishViewModel(Path.Combine(project, "App.csproj"), ["net10.0"], _ => Task.FromResult(string.Empty), _ => Task.FromResult<string?>(null));
        var window = new Window { Width = 800, Height = 600, Content = new PublishView { DataContext = model } };
        window.Show();
        try
        {
            ShellFixture.Pump();
            var box = window.GetVisualDescendants().OfType<PathBox>().Single();
            Assert.Equal(project, box.BaseFolder);
            Assert.Equal(Avalonia.Automation.AutomationProperties.GetName(box), Avalonia.Automation.AutomationProperties.GetName(box.TextBox));
            model.Folder = "not-yet";
            string? start = "unset";
            box.Picker = (_, from) =>
            {
                start = from;
                return Task.FromResult<string?>(output);
            };

            Click(box.BrowseButton);
            await ShellFixture.PumpUntil(() => model.Folder == Path.Combine("bin", "publish"));
            Assert.Equal(project, start);

            // Typing still works, and the box follows the model.
            box.TextBox.Text = "out";
            Assert.Equal("out", model.Folder);
            model.Folder = "dist";
            Assert.Equal("dist", box.TextBox.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Recent_paths_drop_down_from_the_field_and_a_label_focuses_its_text()
    {
        // New Project's location (0.6.0, ADR 0082): the recent locations in the field's own chevron; Location's access key.
        var box = new PathBox { Text = "/work/start" };
        var label = new Label { Content = "_Location", Target = box };
        var before = new TextBox { Text = "before" };
        var window = new Window { Width = 640, Height = 320, Content = new StackPanel { Children = { before, label, box } } };
        window.Show();
        try
        {
            ShellFixture.Pump();
            Assert.Null(box.TextBox.InnerRightContent); // no recent paths: no chevron
            box.Recent = ["/work/one", "/work/two_folder"];
            ShellFixture.Pump();
            Assert.Same(box.RecentButton, box.TextBox.InnerRightContent);
            Assert.True(box.RecentButton.IsEffectivelyVisible);
            Assert.Equal("Recent locations", AutomationProperties.GetName(box.RecentButton));
            Assert.Equal("Recent locations (Alt+Down)", ToolTip.GetTip(box.RecentButton));
            Assert.False(box.RecentButton.IsTabStop);

            // Alt+Down in the field opens the list, newest first, a path's underscore kept (no access keys); choosing one
            // puts it in the field, raises RecentChosen and gives the field the focus back.
            var chosen = 0;
            box.RecentChosen += (_, _) => chosen++;
            box.TextBox.Focus();
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            ShellFixture.Pump();
            Assert.True(box.RecentMenu.IsOpen);
            var items = box.RecentMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["/work/one", "/work/two_folder"], items.Select(AutomationProperties.GetName));
            Assert.Equal("/work/two_folder", Assert.IsType<TextBlock>(items[1].Header).Text);
            Assert.Equal("folder", Assert.IsType<Icon>(items[1].Icon).Kind);
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ShellFixture.Pump();
            Assert.Equal("/work/two_folder", box.Text);
            Assert.Equal(1, chosen);
            Assert.False(box.RecentMenu.IsOpen);
            Assert.True(box.TextBox.IsFocused);

            // F4 opens it too; closing it keeps the text.
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            ShellFixture.Pump();
            Assert.True(box.RecentMenu.IsOpen);
            box.RecentMenu.Hide();
            ShellFixture.Pump();
            Assert.Equal("/work/two_folder", box.Text);

            // The label's access key lands in the text box (the box forwards its focus), and Tab goes straight there.
            before.Focus();
            window.KeyPress(Key.L, RawInputModifiers.Alt, PhysicalKey.L, "l");
            window.KeyRelease(Key.L, RawInputModifiers.Alt, PhysicalKey.L, "l");
            ShellFixture.Pump();
            Assert.True(box.TextBox.IsFocused, $"focused: {window.FocusManager?.GetFocusedElement()}");
            before.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            ShellFixture.Pump();
            Assert.True(box.TextBox.IsFocused, $"focused: {window.FocusManager?.GetFocusedElement()}");
            Assert.False(box.IsTabStop);

            // Fields without recent paths don't change.
            box.Recent = null;
            ShellFixture.Pump();
            Assert.Null(box.TextBox.InnerRightContent);
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
            ShellFixture.Pump();
            Assert.False(box.RecentMenu.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Every_view_field_bound_to_a_path_is_a_path_box()
    {
        // A TextBox bound to a path-like property has no Browse button. Paths in a container aren't on this computer.
        string[] notLocal = ["ContainersView.axaml: FilesPath"];
        var views = Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (Match m in TextBoxBinding().Matches(File.ReadAllText(file)))
            {
                var entry = $"{Path.GetFileName(file)}: {m.Groups[1].Value}";
                if (!notLocal.Contains(entry))
                {
                    found.Add(entry);
                }
            }
        }

        Assert.True(found.Count == 0, "Path fields without Browse (use controls:PathBox):\n" + string.Join('\n', found));
    }

    [Fact]
    public void The_picker_starts_in_the_nearest_existing_folder_of_what_was_typed()
    {
        var root = Directory.CreateTempSubdirectory("codalus-pathbox-").FullName;
        try
        {
            var sub = Directory.CreateDirectory(Path.Combine(root, "a", "b")).FullName;
            var file = Path.Combine(sub, "app.db");
            File.WriteAllText(file, string.Empty);
            Assert.Equal(sub, PathBox.StartFolder(Path.Combine(sub, "missing", "deeper"), null));
            Assert.Equal(sub, PathBox.StartFolder(file, null));
            Assert.Equal(sub, PathBox.StartFolder(Path.Combine("a", "b", "c"), root));
            Assert.Equal(root, PathBox.StartFolder("not a rooted path", root));
            Assert.Null(PathBox.StartFolder("relative", null));
            Assert.Null(PathBox.StartFolder("   ", null));
            Assert.Equal(Path.Combine("a", "b"), PathBox.Relative(sub, root));
            Assert.Equal(".", PathBox.Relative(root, root));
            Assert.Equal(file, PathBox.Relative(file, Path.Combine(root, "elsewhere")));
            Assert.Equal(file, PathBox.Relative(file, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [GeneratedRegex(@"<TextBox\b[^>]*?\bText=""\{Binding ([A-Za-z.]*(?:Path|Folder|Directory|Dir|Location|Executable|File))\b")]
    private static partial Regex TextBoxBinding();
}
