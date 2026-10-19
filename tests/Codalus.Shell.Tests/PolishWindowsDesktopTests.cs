using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Setup;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34g (the polish pass over the Windows desktop UI and the new UI since M34f): the views M40–M47 added, each in its
/// states (empty, loading, error, disabled, with data), at its minimum size and in all sixteen themes: no text cut off
/// without an ellipsis and a tooltip, every control named, every text readable on what it is drawn on (4.5:1, 7:1 in
/// the theme families' High contrast variants), and every icon in the palette. Windows-only views are checked through
/// their view models and views with fakes, as M34f did.
/// </summary>
public partial class PolishTests
{
    /// <summary>The setup window's minimum size (SetupWindow.axaml).</summary>
    private const double SetupMinWidth = 760;

    private const double SetupMinHeight = 560;

    /// <summary>Code Search's floating window at its smallest (CodeSearchViewModel's minimum size).</summary>
    private const double CodeSearchMinWidth = 420;

    [AvaloniaFact]
    public async Task The_new_views_in_every_state_cut_no_text_name_every_control_and_stay_readable_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var checkedSurfaces = new List<string>();
        void Check(string surface, Control view, double width, double height)
        {
            checkedSurfaces.Add(surface);
            foreach (var problem in InspectInEveryTheme(view, width, height))
            {
                problems.Add($"{surface}: {problem}");
            }
        }

        // M40: the XAML Previewer, nothing previewed, WPF off Windows (a status), the loader's error; and the live tools.
        var preview = new XamlPreviewViewModel();
        var previewView = new XamlPreviewView { DataContext = preview };
        Check("xaml-previewer (empty)", previewView, ToolWindowMinWidth, 420);
        preview.SupportsThemes = false;
        preview.Status = "The WPF previewer runs on Windows only: on this OS a WPF project's XAML opens, edits and builds, with IntelliSense.";
        Check("xaml-previewer (wpf, windows only)", previewView, ToolWindowMinWidth, 420);
        preview.Error = "MainWindow.xaml(12,9): 'Buton' isn't a type in the WPF namespace. The last good preview stays until the XAML loads again.";
        Check("xaml-previewer (error)", previewView, ToolWindowMinWidth, 420);

        var liveTree = new LiveTreeViewModel(() => null, _ => Task.CompletedTask);
        var liveTreeView = new LiveTreeView { DataContext = liveTree };
        Check("live-visual-tree (empty)", liveTreeView, ToolWindowMinWidth, 420);
        var window = new LiveNodeViewModel(1, "MainWindow", "OrdersWindowWithAVeryLongNameForTheTree", false, true) { IsExpanded = true };
        var grid = new LiveNodeViewModel(2, "Grid", "LayoutRoot", false, false) { IsExpanded = true };
        grid.Children.Add(new LiveNodeViewModel(3, "Button", "PlaceOrderButton", false, false));
        var part = new LiveNodeViewModel(4, "ContentPresenter", string.Empty, true, false);
        grid.Children.Add(part);
        window.Children.Add(grid);
        liveTree.Roots.Add(window);
        liveTree.Selected = grid;
        Check("live-visual-tree (data)", liveTreeView, ToolWindowMinWidth, 420);
        liveTree.Selected = part; // a template part (muted) selected
        Check("live-visual-tree (a template part selected)", liveTreeView, ToolWindowMinWidth, 420);

        var liveProperties = new LivePropertiesViewModel(() => null);
        var livePropertiesView = new LivePropertiesView { DataContext = liveProperties };
        Check("live-property-explorer (empty)", livePropertiesView, ToolWindowMinWidth, 420);
        await liveProperties.ShowAsync(grid);
        liveProperties.Rows.Add(new LivePropertyRow(new LiveProperty("HorizontalContentAlignment", "Stretch", "HorizontalAlignment", "Style", false), (_, _) => Task.CompletedTask));
        liveProperties.Rows.Add(new LivePropertyRow(new LiveProperty("Margin", "4,8,4,8", "Thickness", "Local", false), (_, _) => Task.CompletedTask));
        liveProperties.Rows.Add(new LivePropertyRow(new LiveProperty("IsMouseOver", "False", "Boolean", "Default", true), (_, _) => Task.CompletedTask));
        liveProperties.Rows.Add(new LivePropertyRow(new LiveProperty("Width", "Auto", "Double", "Default", false), (_, _) => Task.CompletedTask) { Error = "'wide' isn't a valid Double: use a number or Auto." });
        Check("live-property-explorer (data, an error)", livePropertiesView, ToolWindowMinWidth, 420);

        // M41: the Toolbox without a designer, with one, an item armed, and a filter that matches nothing.
        var toolbox = new ToolboxViewModel();
        var toolboxView = new ToolboxView { DataContext = toolbox };
        Check("toolbox (no designer)", toolboxView, ToolWindowMinWidth, 420);
        toolbox.IsAvailable = true;
        toolbox.SetProjectItems([new Codalus.LanguageServices.Protocol.WinFormsToolboxItem("WinFormsApp.Controls.CounterControlWithALongName", "CounterControlWithALongName", "WinFormsApp", true)]);
        Check("toolbox (data)", toolboxView, ToolWindowMinWidth, 420);
        toolbox.Armed = toolbox.Items.First(i => i.Name == "Button");
        Check("toolbox (armed)", toolboxView, ToolWindowMinWidth, 420);
        toolbox.Armed = null;
        toolbox.Filter = "Gauge";
        Assert.True(toolbox.HasNoMatches, "a filter that matches nothing says so");
        Check("toolbox (no matches)", toolboxView, ToolWindowMinWidth, 420);
        toolbox.Filter = string.Empty;

        // M42: the WCF Test Client: empty, a missing address, loading, the metadata with a form, a response, a fault.
        var wsdl = Path.Combine(ShellFixture.RepoRoot, "samples", "Wcf", "contracts", "CalculatorService.wsdl");
        var gate = new TaskCompletionSource();
        MockHttpServer? server = null;
        server = new MockHttpServer(async request =>
        {
            await gate.Task.ConfigureAwait(false);
            if (request.Method == "GET")
            {
                return MockHttpResponse.Text(File.ReadAllText(wsdl).Replace("http://localhost:5280/", server!.BaseUrl + "/", StringComparison.Ordinal), contentType: "text/xml; charset=utf-8");
            }

            var name = request.Body.Contains("<Divide", StringComparison.Ordinal) ? "response-divide11.xml" : "response-greet11.xml";
            var recorded = Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "Wcf", name);
            return MockHttpResponse.Text(File.ReadAllText(recorded), name.Contains("divide", StringComparison.Ordinal) ? 500 : 200, "text/xml; charset=utf-8");
        });
        await using (server)
        {
            using var client = new WcfTestClient();
            var wcf = new WcfTestClientViewModel(client, null, () => Task.FromResult<IReadOnlyList<DiscoveredService>>([]), () => Task.FromResult<string?>(null));
            var wcfView = new WcfTestClientView { DataContext = wcf };
            Check("wcf-test-client (empty)", wcfView, DocumentMinWidth, 560);
            await wcf.LoadAsync();
            Assert.NotNull(wcf.Error);
            Check("wcf-test-client (error)", wcfView, DocumentMinWidth, 560);
            wcf.Address = server.BaseUrl + "/CalculatorService.svc";
            var loading = wcf.LoadAsync();
            Assert.True(wcf.IsBusy);
            Check("wcf-test-client (loading)", wcfView, DocumentMinWidth, 560);
            gate.SetResult();
            await loading;
            Assert.True(wcf.Error is null, wcf.Error);
            wcf.Select("Greet");
            Check("wcf-test-client (data)", wcfView, DocumentMinWidth, 560);
            wcf.Row("person.Name")!.Value = "Ada";
            wcf.Row("person.Age")!.Value = "36";
            await wcf.SendAsync();
            Assert.True(wcf.HasResponse, wcf.Error);
            Check("wcf-test-client (response)", wcfView, DocumentMinWidth, 560);
            wcf.Select("Divide");
            wcf.Row("a")!.Value = "1";
            wcf.Row("b")!.Value = "0";
            await wcf.SendAsync();
            Assert.True(wcf.IsFault);
            Check("wcf-test-client (fault)", wcfView, DocumentMinWidth, 560);
        }

        // M46: the setup window at its minimum size, each step; the Tools step with a missing SDK's plan shown.
        f.Shell.Run("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var setup = f.Shell.SetupModel!;
        var setupWindow = f.Shell.SetupWindowView!;
        foreach (var step in new[] { "appearance", "keyboard", "settings", "tools" })
        {
            if (step == "tools")
            {
                setup.ShowTools([new ToolStatus("dotnet", ".NET SDK", true, ToolState.Missing, "Opening .NET projects"), new ToolStatus("git", "Git", true, ToolState.Found, "Source control") { Version = "2.47.1" }]);
                setup.Tools[0].InstallCommand.Execute(null);
                ShellFixture.Pump();
            }

            checkedSurfaces.Add("setup " + step);
            foreach (var problem in InspectWindowInEveryTheme(setupWindow, SetupMinWidth, SetupMinHeight))
            {
                problems.Add($"setup ({step}): {problem}");
            }

            if (step != "tools")
            {
                await setup.NextAsync();
            }
        }

        await setup.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);

        // M45: Options' color theme cards.
        f.Shell.OpenSettings(ThemeService.SettingKey);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is SettingsDocument);
        var settings = (SettingsDocument)f.Shell.Layout.ActiveDocument!;
        Check("options color theme (cards)", new ContentControl { Content = settings }, DocumentMinWidth, 560);
        f.Shell.Layout.CloseDocument(settings);

        // M47: Code Search's floating window without a solution: Code Search empty, nothing found, Feature Search's results.
        await f.Shell.GoToAllAsync();
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearch is { IsOpen: true, IsFloatingOpen: true });
        var search = f.Shell.CodeSearch;
        var searchView = f.Window.GetVisualDescendants().OfType<CodeSearchView>().First(v => v.IsEffectivelyVisible);
        search.Width = CodeSearchMinWidth;
        search.Height = 300;
        foreach (var (state, query, tab) in new[] { ("empty", string.Empty, 0), ("no results", "zqxwv", 0), ("feature search", "build", 1) })
        {
            search.TabIndex = tab;
            search.Query = query;
            await ShellFixture.PumpUntil(() => !search.IsSearching, seconds: 30);
            checkedSurfaces.Add("code-search " + state);
            foreach (var problem in InspectInPlaceInEveryTheme(f.Window, searchView))
            {
                problems.Add($"code-search ({state}): {problem}");
            }
        }

        search.CloseCommand.Execute(null);
        Assert.True(problems.Count == 0, $"{checkedSurfaces.Count} surfaces checked; problems:\n" + string.Join('\n', problems));
    }

    [AvaloniaFact]
    public async Task Code_search_and_the_wcf_service_references_page_with_a_solution_stay_readable_in_every_theme()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var search = f.Shell.CodeSearch;

        // M47: Code Search while the solution loads (files first, the incomplete notice), then complete with a type
        // selected and its preview, floating at its smallest; then docked, at the tool window's 280 px.
        await f.Shell.GoToAllAsync();
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsFloatingOpen: true });
        search.Width = CodeSearchMinWidth;
        search.Height = 420;
        search.Query = "Shapes";
        await ShellFixture.PumpUntil(() => search.Rows.Any(r => r.Name == "Shapes.cs"), seconds: 60);
        var floating = f.Window.GetVisualDescendants().OfType<CodeSearchView>().First(v => v.IsEffectivelyVisible);
        var states = new List<string>();
        if (search.IsIncomplete)
        {
            states.Add("incomplete");
            foreach (var problem in InspectInPlaceInEveryTheme(f.Window, floating))
            {
                problems.Add($"code-search (incomplete): {problem}");
            }
        }

        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 180);
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchIndex.IsComplete && !search.IsIncomplete, seconds: 60);
        search.Query = "Circle";
        await ShellFixture.PumpUntil(() => search.Rows.Any(r => r.Result is { Name: "Circle" }) && !search.IsSearching, seconds: 60);
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview is not null, seconds: 30);

        // The preview as the user sees it, on every OS: with its navigation bar, the caret on the type, so the member
        // picker shows its placeholder. (A preview made before the language service was ready never asked for the
        // document's symbols again, so only where the service won the race, on Windows, did it show.) In applied
        // themes: the editor's colors, sticky scroll's pinned lines when docked, follow the theme, not a theme variant.
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview is { NavigationBar.IsVisible: true }, seconds: 60, () => "the preview's navigation bar");
        states.Add("data");
        foreach (var problem in await InspectInEveryAppliedThemeAsync(f, floating))
        {
            problems.Add($"code-search (data, preview): {problem}");
        }

        search.ToggleDockCommand.Execute(null);
        await ShellFixture.PumpUntil(() => search.IsDocked && f.Shell.Layout.IsVisible(ShellHost.CodeSearchToolWindowId));
        var tool = f.Shell.Layout.ShowToolWindow(ShellHost.CodeSearchToolWindowId, focus: false)!;
        states.Add("docked");
        foreach (var problem in await InspectInEveryAppliedThemeAsync(f, new ContentControl { Content = tool }, new Size(ToolWindowMinWidth, 420)))
        {
            problems.Add($"code-search (docked, data): {problem}");
        }

        search.ToggleDockCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !search.IsDocked);
        search.CloseCommand.Execute(null);

        // M42: the Service References document's WCF page (svcutil's state, the add form) for a project with none.
        var project = f.Shell.Solutions.Projects.Keys.First(p => p.EndsWith("Lib.csproj", StringComparison.Ordinal));
        var references = f.Shell.OpenServiceReferences(project, ServiceReferencesViewModel.WcfKind);
        Assert.NotNull(references);
        await ShellFixture.PumpUntil(() => !references.IsBusy && references.WcfToolMessage is { Length: > 0 }, seconds: 120);
        states.Add("service references (wcf)");
        foreach (var problem in InspectInEveryTheme(new ContentControl { Content = f.Shell.Layout.ActiveDocument }, DocumentMinWidth, 560))
        {
            problems.Add($"service-references (wcf): {problem}");
        }

        await references.RunAsync("add"); // no address: the error state
        Assert.NotNull(references.Error);
        foreach (var problem in InspectInEveryTheme(new ContentControl { Content = f.Shell.Layout.ActiveDocument }, DocumentMinWidth, 560))
        {
            problems.Add($"service-references (wcf, error): {problem}");
        }

        Assert.True(problems.Count == 0, $"States checked: {string.Join(", ", states)}; problems:\n" + string.Join('\n', problems));
    }

    [AvaloniaFact]
    public async Task The_windows_forms_designer_its_toolbox_and_properties_stay_readable_in_every_theme()
    {
        // M41 is Windows only (D31): the designer host is the fake of WinFormsDesignerShellTests, on any OS, over the real
        // build and C# hosts, so the shell's designer document, the Toolbox and the Properties window show their data.
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        var app = Path.Combine(folder, "WinFormsApp");
        var failing = false;
        f.Shell.WinFormsDesigner = new Codalus.Workbench.WinForms.WinFormsDesignerService(f.Shell.Solutions, _ => true, (_, _) => Task.FromResult(true), async (project, names, ct) =>
        {
            var properties = await f.Shell.ProjectManagement.GetPropertiesAsync(project, names, null, ct);
            return properties.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        })
        {
            IsWindows = () => true,
            DesignerHost = () => typeof(PolishTests).Assembly.Location,
            HostFactory = (_, _) => failing
                ? Task.FromException<Codalus.Workbench.WinForms.IWinFormsDesignerHost>(new Codalus.Workbench.Previewer.PreviewerException("The designer host exited at start (code 1): the app's Windows Desktop runtime isn't installed."))
                : Task.FromResult<Codalus.Workbench.WinForms.IWinFormsDesignerHost>(new FakeWinFormsDesignerHost()),
        };
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 1 }, seconds: 300);
        var problems = new SortedSet<string>(StringComparer.Ordinal);
        void Check(string surface, Control view, double width, double height)
        {
            foreach (var problem in InspectInEveryTheme(view, width, height))
            {
                problems.Add($"{surface}: {problem}");
            }
        }

        // The form in the designer with a selection; the Toolbox and the Properties window (properties, then events).
        Assert.Null(await f.Shell.OpenFileAsync(Path.Combine(app, "OrderForm.cs")));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120, () => designer.Status);
        designer.SetSelection(["placeOrderButton"], notify: true);
        Check("winforms-designer (data)", new ContentControl { Content = designer }, DocumentMinWidth, 480);
        Check("toolbox (designer)", new ContentControl { Content = f.Shell.Layout.ShowToolWindow(ToolboxViewModel.ToolWindowId, focus: false) }, ToolWindowMinWidth, 420);
        var propertiesTool = f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        await f.Shell.PropertiesWindow.RefreshAsync();
        Assert.True(f.Shell.PropertiesWindow.HasDesignerSource);
        Check("properties (designer)", new ContentControl { Content = propertiesTool }, ToolWindowMinWidth, 480);
        f.Shell.PropertiesWindow.ShowEvents = true;
        await f.Shell.PropertiesWindow.RefreshAsync();
        Check("properties (designer, events)", new ContentControl { Content = propertiesTool }, ToolWindowMinWidth, 480);
        f.Shell.PropertiesWindow.ShowEvents = false;
        f.Shell.Layout.CloseDocument(designer);

        // A designer that can't start: its reason where the form would be.
        failing = true;
        Assert.Null(await f.Shell.OpenFileAsync(Path.Combine(app, "OrderForm.cs")));
        designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        await ShellFixture.PumpUntil(() => designer.Status.Contains("Windows Desktop runtime", StringComparison.Ordinal), seconds: 120, () => designer.Status);
        Check("winforms-designer (error)", new ContentControl { Content = designer }, DocumentMinWidth, 480);
        var host = new Window { Width = DocumentMinWidth, Height = 480, Content = new ContentControl { Content = designer } };
        host.Show();
        ShellFixture.Pump();
        Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("emptyState") && t.IsEffectivelyVisible && t.Text == designer.Status);
        host.Content = null;
        host.Close();
        f.Shell.Layout.CloseDocument(designer);
        Assert.True(problems.Count == 0, "Problems:\n" + string.Join('\n', problems));
    }

    // ------------------------------------------------------------------ the inspection

    /// <summary>
    /// Hosts <paramref name="view"/> on a panel at the size given, in each of the sixteen themes, and returns what the
    /// checklist finds (the same finding in several themes once, with the themes it's in for contrast).
    /// </summary>
    internal static IReadOnlyList<string> InspectInEveryTheme(Control view, double width, double height)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, variant) in s_themes)
        {
            var panel = new Border { Child = view };
            panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("Shell.Panel"));
            var host = new Window { Width = width, Height = height, RequestedThemeVariant = variant, Content = panel };
            host.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            Collect(host, variant, name, found);
            panel.Child = null;
            host.Close();
            ShellFixture.Pump();
        }

        return [.. found];
    }

    /// <summary>A window of the shell's own (the setup window) at the size given, in each theme.</summary>
    internal static IReadOnlyList<string> InspectWindowInEveryTheme(Window window, double width, double height)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var (oldWidth, oldHeight, oldTheme) = (window.Width, window.Height, window.RequestedThemeVariant);
        window.Width = width;
        window.Height = height;
        foreach (var (name, variant) in s_themes)
        {
            window.RequestedThemeVariant = variant;
            ShellFixture.Pump();
            ShellFixture.Pump();
            Collect(window, variant, name, found);
        }

        (window.Width, window.Height, window.RequestedThemeVariant) = (oldWidth, oldHeight, oldTheme);
        ShellFixture.Pump();
        return [.. found];
    }

    /// <summary>A part of the main window (Code Search's floating window) where it is, in each theme.</summary>
    internal static IReadOnlyList<string> InspectInPlaceInEveryTheme(Window window, Control part)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var oldTheme = window.RequestedThemeVariant;
        foreach (var (name, variant) in s_themes)
        {
            window.RequestedThemeVariant = variant;
            ShellFixture.Pump();
            ShellFixture.Pump();
            Collect(part, variant, name, found);
        }

        window.RequestedThemeVariant = oldTheme;
        ShellFixture.Pump();
        return [.. found];
    }

    /// <summary>
    /// <paramref name="part"/> in each theme applied as the user applies it (the setting), not only as a theme variant: an
    /// editor's text and sticky scroll's pinned lines take the active theme's syntax colors, which only the setting
    /// changes. With <paramref name="size"/>, on a window of its own at that size; without it, where it is.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> InspectInEveryAppliedThemeAsync(ShellFixture f, Control part, Size? size = null)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var (id, variant) in ThemeService.Themes)
            {
                await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, id, SettingScope.User, CancellationToken.None);
                await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == id && (size is not null || part.ActualThemeVariant == variant));
                if (size is not { } hostSize)
                {
                    ShellFixture.Pump();
                    ShellFixture.Pump();
                    Collect(part, variant, ThemeService.Title(id), found);
                    continue;
                }

                var panel = new Border { Child = part };
                panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("Shell.Panel"));
                var host = new Window { Width = hostSize.Width, Height = hostSize.Height, RequestedThemeVariant = variant, Content = panel };
                host.Show();
                for (var frame = 0; frame < 6; frame++)
                {
                    ShellFixture.Pump(); // an editor scrolls to its caret, then pins sticky scroll's lines, a few frames after it's shown
                }

                Collect(host, variant, ThemeService.Title(id), found);
                panel.Child = null;
                host.Close();
                ShellFixture.Pump();
            }
        }
        finally
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None);
        }

        return [.. found];
    }

    private static void Collect(Visual root, ThemeVariant variant, string theme, SortedSet<string> found)
    {
        foreach (var problem in Clipped(root).Concat(Unnamed(root)).Concat(Uncolored(root)).Concat(Squeezed(root)).Concat(Overlapping(root)))
        {
            found.Add(problem);
        }

        foreach (var problem in LowContrast(root, variant))
        {
            found.Add($"{problem} in {theme}");
        }
    }

    /// <summary>The narrowest a text box or a combo box may be: room for a few characters and its own padding.</summary>
    private const double InputMinWidth = 48;

    /// <summary>
    /// Inputs squeezed below <see cref="InputMinWidth"/> by the columns around them (the checklist's minimum sizes: fixed
    /// columns that add up to most of the width leave the editable one nothing), where the content doesn't scroll sideways.
    /// </summary>
    internal static IEnumerable<string> Squeezed(Visual root)
    {
        foreach (var input in root.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or ComboBox))
        {
            if (!input.IsEffectivelyVisible || input.TemplatedParent is not null && input.TemplatedParent is not ContentPresenter
                || input.GetVisualAncestors().OfType<ScrollContentPresenter>().Any(p => p.CanHorizontallyScroll)
                || InOverflow(input) || input.Bounds.Width >= InputMinWidth)
            {
                continue;
            }

            yield return $"a {input.GetType().Name} is {input.Bounds.Width:0} px wide, below {InputMinWidth} px ({Describe(input, root)})";
        }
    }

    /// <summary>
    /// Text drawn over other text (M34g on M43): a text that isn't constrained to its cell (in a horizontal stack panel,
    /// say) runs under the next column's, which neither the clipping check nor the eye at a glance catches. Each text's
    /// ink (its laid-out width within its box, by its alignment), clipped by what clips it, against every other's.
    /// </summary>
    internal static IEnumerable<string> Overlapping(Visual root)
    {
        var rootRect = new Rect(root.Bounds.Size);
        var inks = new List<(TextBlock Text, string Content, Rect Ink)>();
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            var content = text.Text ?? text.Inlines?.Text;
            if (!text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(content) || text.Bounds.Width < 1 || text.Bounds.Height < 1
                || text.FindAncestorOfType<TextBox>() is not null || InOverflow(text) || InThemePreview(text) || text.TranslatePoint(default, root) is not { } origin)
            {
                continue;
            }

            var needed = text.TextLayout.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
            var width = Math.Min(needed, text.TextWrapping == TextWrapping.NoWrap && text.TextTrimming == TextTrimming.None ? needed : text.Bounds.Width);
            var left = text.TextAlignment switch
            {
                TextAlignment.Right or TextAlignment.End => origin.X + text.Bounds.Width - width,
                TextAlignment.Center => origin.X + ((text.Bounds.Width - width) / 2),
                _ => origin.X,
            };
            var ink = new Rect(left, origin.Y, width, text.Bounds.Height).Intersect(rootRect);
            for (var v = text.GetVisualParent(); v is not null && v != root; v = v.GetVisualParent())
            {
                if (v.ClipToBounds && v.TranslatePoint(default, root) is { } at)
                {
                    ink = ink.Intersect(new Rect(at, v.Bounds.Size));
                }
            }

            if (ink.Width >= 1 && ink.Height >= 1)
            {
                inks.Add((text, content, ink));
            }
        }

        for (var i = 0; i < inks.Count; i++)
        {
            for (var j = i + 1; j < inks.Count; j++)
            {
                // A link inside a paragraph (an inline container, rendered Markdown) is laid out by the text around it.
                var inline = inks[i].Text.IsVisualAncestorOf(inks[j].Text) || inks[j].Text.IsVisualAncestorOf(inks[i].Text);
                var overlap = inks[i].Ink.Intersect(inks[j].Ink);
                if (!inline && overlap.Width > 1 && overlap.Height > 2)
                {
                    static string Label(string s) => s.Length > 30 ? s[..30] + "…" : s;
                    yield return $"\"{Label(inks[i].Content)}\" and \"{Label(inks[j].Content)}\" are drawn over each other ({overlap.Width:0} × {overlap.Height:0} px; {Describe(inks[j].Text, root)})";
                }
            }
        }
    }

    /// <summary>
    /// Icons nothing colors whose kind has no palette color of its own (CommandIcons' kind colors): every icon is colored
    /// in the palette (the maintainer's rule), and IconColorTests only sees the kinds written in the views' XAML.
    /// </summary>
    internal static IEnumerable<string> Uncolored(Visual root)
    {
        foreach (var icon in root.GetVisualDescendants().OfType<Icon>())
        {
            if (!icon.IsEffectivelyVisible || icon.Kind is not { Length: > 0 } kind || kind.Contains(':', StringComparison.Ordinal) || kind.Contains('@', StringComparison.Ordinal)
                || icon.IsSet(Icon.ForegroundProperty) || CommandIcons.DefaultColorOf(kind) is null || CommandIcons.HasColor(kind))
            {
                continue;
            }

            yield return $"the icon '{kind}' has no palette color: add it to CommandIcons' kind colors ({Describe(icon, root)})";
        }
    }

    /// <summary>
    /// Text that doesn't meet its contrast on what it is drawn on: the nearest background under it (the text's own, a
    /// border's, a panel's, a presenter's, a templated control's), blended where it is translucent, against the text's
    /// color (with the opacity of what holds it). 4.5:1, or 3:1 for large text; in a theme family's High contrast
    /// variant 7:1 (4.5:1 large), as the token pairs. Disabled text is exempt (WCAG); text boxes draw Fluent's.
    /// </summary>
    internal static IEnumerable<string> LowContrast(Visual root, ThemeVariant variant)
    {
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            // The text's own variant: a theme's preview (M34h) draws in its variant inside a window in another.
            var textVariant = text.ActualThemeVariant ?? variant;
            var aaa = ThemeService.IsHighContrast(textVariant) && textVariant != ThemeService.HighContrast;
            var content = text.Text ?? text.Inlines?.Text;
            if (!text.IsEffectivelyVisible || !text.IsEffectivelyEnabled || string.IsNullOrWhiteSpace(content) || text.Bounds.Width < 1 || text.Bounds.Height < 1
                || text.FindAncestorOfType<TextBox>() is not null || InThemePreview(text) || text.Foreground is not ISolidColorBrush foreground)
            {
                continue;
            }

            if (BackgroundOf(text, root) is not { } background)
            {
                continue;
            }

            var opacity = 1.0; // what holds the text
            for (Visual? v = text; v is not null && v != root; v = v.GetVisualParent())
            {
                opacity *= v.Opacity;
            }

            // The text, and each run of it in a color of its own (a muted detail after a name, M34g on M43).
            var parts = new List<(string Content, ISolidColorBrush Brush, double Size, FontWeight Weight)> { (content, foreground, text.FontSize, text.FontWeight) };
            foreach (var run in text.Inlines?.OfType<Avalonia.Controls.Documents.Run>() ?? [])
            {
                if (!string.IsNullOrWhiteSpace(run.Text) && run.Foreground is ISolidColorBrush own && own.Color != foreground.Color)
                {
                    parts.Add((run.Text, own, run.FontSize, run.FontWeight));
                }
            }

            foreach (var (part, brush, size, weight) in parts)
            {
                var color = Blend(brush.Color, opacity * brush.Opacity * brush.Color.A / 255.0, background);
                var large = size >= 24 || size >= 18.66 && weight >= FontWeight.Bold;
                var required = aaa ? large ? 4.5 : 7 : large ? 3 : 4.5;
                var ratio = Contrast(color, background);
                if (ratio < required - 0.005)
                {
                    var label = part.Length > 40 ? part[..40] + "…" : part;
                    yield return $"\"{label}\" is {ratio:0.00}:1 ({brush.Color} on {background}), below {required}:1 ({Describe(text, root)})";
                }
            }
        }
    }

    /// <summary>The opaque color under <paramref name="visual"/>: the nearest backgrounds up its ancestors, blended.</summary>
    private static Color? BackgroundOf(Visual visual, Visual root)
    {
        var layers = new List<(Color Color, double Alpha)>();
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
        {
            var brush = v switch
            {
                TextBlock t => t.Background,
                Border b => b.Background,
                Panel p => p.Background,
                ContentPresenter c => c.Background,
                TemplatedControl t => t.Background,
                _ => null,
            };
            if (brush is ISolidColorBrush solid && solid.Opacity * solid.Color.A / 255.0 * v.Opacity > 0.01)
            {
                var alpha = solid.Opacity * solid.Color.A / 255.0 * v.Opacity;
                layers.Add((solid.Color, alpha));
                if (alpha >= 0.99)
                {
                    var color = solid.Color;
                    for (var i = layers.Count - 2; i >= 0; i--)
                    {
                        color = Blend(layers[i].Color, layers[i].Alpha, color);
                    }

                    return color;
                }
            }
        }

        return null;
    }

    private static Color Blend(Color top, double alpha, Color under)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round((a * alpha) + (b * (1 - alpha)));
        return Avalonia.Media.Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }
}
