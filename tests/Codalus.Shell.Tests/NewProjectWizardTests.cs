using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Tests;

namespace Codalus.Shell.Tests;

/// <summary>
/// New Project's window (0.6.0, ADR 0082) in the headless shell: File → New Project opens it owned by the main window,
/// not modal; its keys walk the three pages (Alt+S, Down, Enter, the labels' access keys, Alt+Left and Alt+Right,
/// Escape); the filters, the template rows and the options are the view model's; Create from the window writes the
/// project with the real SDK, closes the window, and the main window opens the trusted solution with Program.cs. The
/// recorded SDK 11.0 templates and a stand-in <c>dotnet new</c> serve everything but Create.
/// </summary>
public sealed class NewProjectWizardTests
{
    /// <summary><c>dotnet new sln</c> and <c>dotnet new &lt;template&gt;</c> as files, as the view model's tests have them.</summary>
    private static CliResult StandInDotnet(IReadOnlyList<string> args)
    {
        if (args is not ["new", var template, ..])
        {
            return new CliResult(1, "unexpected: " + string.Join(' ', args));
        }

        var list = args.ToList();
        var name = list[list.IndexOf("--name") + 1];
        var output = list[list.IndexOf("--output") + 1];
        Directory.CreateDirectory(output);
        if (template == "sln")
        {
            File.WriteAllText(Path.Combine(output, name + ".slnx"), "<Solution />");
        }
        else
        {
            File.WriteAllText(Path.Combine(output, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(output, "Program.cs"), string.Empty);
        }

        return new CliResult(0, "The template was created successfully.");
    }

    /// <summary>
    /// The shell's New Project on the recorded templates and the stand-in CLI, as off Windows and macOS on every OS (the
    /// Windows only tags, Alt+arrows), with its location in the fixture's folder: never the user's own source/repos,
    /// where a BlazorApp1 of their own would stop Next.
    /// </summary>
    private static RecordedCli UseRecordedTemplates(ShellFixture f, TemplateTestData data)
    {
        var cli = RecordedCli.Sdk11();
        cli.Fallback = StandInDotnet;
        f.Shell.ProjectTemplates = new ProjectTemplateCatalog(cli, new FixedCacheLocator(data.Cache11), () => new DateOnly(2026, 10, 21));
        f.Shell.NewProjectCli = cli;
        f.Shell.NewProjects = new NewProjectService(cli, new PathRules(IsWindows: false, LongPathsEnabled: true));
        f.Shell.NewProjectIsWindows = false;
        f.Shell.NewProjectIsMacOS = false;
        f.Shell.NewProjectHistory.AddRecentLocation(Directory.CreateDirectory(Path.Combine(f.UserData, "repos")).FullName);
        return cli;
    }

    private static async Task<(NewProjectViewModel Model, NewProjectWindow Window)> OpenedAsync(ShellFixture f, int seconds = 30)
    {
        await ShellFixture.PumpUntil(() => f.Shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0 } && f.Shell.NewProjectWindowView is { IsVisible: true }, seconds);
        var model = f.Shell.NewProjectModel!;
        model.SearchDelay = TimeSpan.Zero;
        model.ValidationDelay = TimeSpan.Zero;
        ShellFixture.Pump();
        return (model, (NewProjectWindow)f.Shell.NewProjectWindowView!);
    }

    private static T Named<T>(Window window, string name)
        where T : Control => window.FindControl<T>(name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}");

    private static IInputElement? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None, string? symbol = null)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, symbol);
        if (window.IsVisible)
        {
            window.KeyRelease(key, modifiers, PhysicalKey.None, symbol);
        }

        ShellFixture.Pump();
    }

    /// <summary>Presses a button as a screen reader does (its command runs only while it's enabled).</summary>
    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled && button.IsEffectivelyVisible, $"{button.Name} can't be pressed");
        ((Avalonia.Automation.Provider.IInvokeProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
        ShellFixture.Pump();
    }

    [AvaloniaFact]
    public async Task The_keyboard_walks_the_three_pages_of_the_owned_window()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        var location = Directory.CreateDirectory(Path.Combine(f.UserData, "repos")).FullName;
        f.Shell.NewProjectHistory.AddRecentLocation(location);
        f.Shell.NewProjectHistory.AddRecentTemplate("dotnet:Microsoft.Test.xUnit|C#");

        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f);

        // Owned by the main window, not modal: the command waits for it without blocking anything.
        Assert.Same(f.Window, window.Owner);
        Assert.Equal("New Project", window.Title);
        Assert.Equal((760d, 520d), (window.MinWidth, window.MinHeight));
        Assert.False(command.IsCompleted);
        var search = Named<TextBox>(window, "SearchBox");
        await ShellFixture.PumpUntil(() => search.IsFocused, describe: () => $"focused: {Focused(window)}");

        // The recent templates on the left; Delete removes one.
        var recentList = Named<ListBox>(window, "RecentList");
        Assert.True(recentList.IsEffectivelyVisible);
        Assert.Equal(["xUnit Test Project"], model.RecentTemplates.Select(r => r.Name));

        // Typing searches (the matches marked); the language filter narrows; Escape clears the search first.
        window.KeyTextInput("console");
        await ShellFixture.PumpUntil(() => model.Search == "console" && model.Templates.Count > 0 && model.Templates.All(r => r.Name.Contains("Console", StringComparison.Ordinal)));
        Assert.Equal("Console App", model.Templates[0].Name);
        Assert.Equal(Enumerable.Range(0, 7), model.Templates[0].NameHighlights);
        Assert.True(Named<Button>(window, "ClearSearchButton").IsEffectivelyVisible);
        Named<ComboBox>(window, "LanguageFilter").SelectedItem = model.Languages.Single(l => l.Value == "C#");
        ShellFixture.Pump();
        Assert.Equal("C#", model.SelectedLanguage!.Value);
        Assert.All(model.Templates, r => Assert.Equal("C#", r.Group.Language));
        window.KeyTextInput("zz");
        await ShellFixture.PumpUntil(() => model.IsEmpty);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "No templates match your search.");
        Press(window, Key.Escape);
        Assert.Equal(string.Empty, model.Search);
        Assert.True(window.IsVisible);
        Assert.False(model.IsEmpty);

        // Alt+S goes back to the search; Down goes into the templates; Enter is Next.
        Named<ComboBox>(window, "LanguageFilter").Focus();
        Press(window, Key.S, RawInputModifiers.Alt, "s");
        Assert.True(search.IsFocused);
        window.KeyTextInput("console");
        await ShellFixture.PumpUntil(() => model.Search == "console" && model.Templates[0].Name == "Console App");
        Press(window, Key.Down);
        var list = Named<ListBox>(window, "TemplateList");
        await ShellFixture.PumpUntil(() => list.IsKeyboardFocusWithin, describe: () => $"focused: {Focused(window)}");
        Assert.Same(model.Templates[0], model.SelectedTemplate);
        Press(window, Key.Enter);
        Assert.Equal(NewProjectPage.Configure, model.Page);

        // Configure: the project name has the focus with its text selected; Location's access key lands in its text box.
        var name = Named<TextBox>(window, "ProjectNameBox");
        await ShellFixture.PumpUntil(() => name.IsFocused, describe: () => $"focused: {Focused(window)}");
        Assert.Equal("ConsoleApp1", name.Text);
        Assert.Equal((0, name.Text!.Length), (Math.Min(name.SelectionStart, name.SelectionEnd), Math.Max(name.SelectionStart, name.SelectionEnd)));
        Assert.Equal("Configure your new project", model.PageTitle);
        var pathBox = window.LocationField;
        Assert.Equal([location], pathBox.Recent);
        Assert.True(pathBox.RecentButton.IsEffectivelyVisible);
        Press(window, Key.L, RawInputModifiers.Alt, "l");
        Assert.True(pathBox.TextBox.IsFocused, $"focused: {Focused(window)}");
        pathBox.TextBox.Text = Path.Combine(location, "elsewhere");
        Assert.Equal(Path.Combine(location, "elsewhere"), model.Location);
        pathBox.ChooseRecent(location); // the recent locations' drop-down (PathBoxTests drives its keys)
        Assert.Equal(location, model.Location);
        Press(window, Key.J, RawInputModifiers.Alt, "j");
        Assert.True(name.IsFocused);
        Assert.True(Named<CheckBox>(window, "TrustBox").IsEffectivelyVisible);
        Assert.False(Named<ComboBox>(window, "SolutionCombo").IsEffectivelyVisible); // nothing is open
        await ShellFixture.PumpUntil(() => model.CanGoNext);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == model.CreatedIn && t.Text.StartsWith("Project will be created in", StringComparison.Ordinal));

        // Alt+Right: Additional information, the framework first and focused, with its support labels.
        Press(window, Key.Right, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Additional, model.Page);
        var framework = Named<ComboBox>(window, "FrameworkCombo");
        await ShellFixture.PumpUntil(() => framework.IsFocused, describe: () => $"focused: {Focused(window)}");
        Assert.Equal([".NET 11.0 (Preview)", ".NET 10.0 (Long Term Support)"], framework.Items.OfType<FrameworkChoice>().Select(c => c.Label));
        Assert.True(Named<Button>(window, "CreateButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(window, "NextButton").IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible && Equals(c.Content, "Do not use _top-level statements"));

        // Alt+Left twice: back to the templates, the chosen one focused.
        Press(window, Key.Left, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Configure, model.Page);
        Press(window, Key.Left, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Choose, model.Page);
        await ShellFixture.PumpUntil(() => list.IsKeyboardFocusWithin, describe: () => $"focused: {Focused(window)}");

        // A recent template: chosen from the left, then removed with Delete.
        model.Search = string.Empty;
        recentList.SelectedItem = model.RecentTemplates[0];
        ShellFixture.Pump();
        Assert.Equal("xUnit Test Project", model.SelectedTemplate!.Name);
        (recentList.ContainerFromIndex(0) as Control)!.Focus();
        Press(window, Key.Delete);
        Assert.Empty(model.RecentTemplates);
        Assert.Empty(f.Shell.NewProjectHistory.RecentTemplates);

        // Escape closes it: the command returns nothing.
        Press(window, Key.Escape);
        Assert.Null(await command.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(f.Shell.IsNewProjectOpen);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_pages_show_the_templates_rows_and_options_as_the_model_has_them()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f);
        var list = Named<ListBox>(window, "TemplateList");

        // Template rows: the icon in its kind's color, the name, the description, the tags; Windows only off Windows (the
        // fixture's wizard is off Windows on every OS); a pack.
        Assert.False(model.IsWindows);
        var wpf = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.WPF|C#");
        list.ScrollIntoView(wpf);
        ShellFixture.Pump();
        var wpfRow = (Control)list.ContainerFromItem(wpf)!;
        Assert.Equal("WPF Application, A project for creating a .NET WPF Application, C#, Windows, Desktop, Windows only", Avalonia.Automation.AutomationProperties.GetName(wpfRow));
        Assert.Equal("template-desktop", wpfRow.GetVisualDescendants().OfType<Icon>().First().Kind);
        Assert.Contains(wpfRow.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("warning") && b.IsEffectivelyVisible);
        var pack = model.Templates.First(r => r.IsPack);
        list.SelectedItem = pack;
        ShellFixture.Pump();
        Assert.Same(pack, model.SelectedTemplate);
        Assert.True(Named<Button>(window, "InstallButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(window, "NextButton").IsEffectivelyVisible);
        var packRow = (Control)list.ContainerFromItem(pack)!;
        Assert.Contains(packRow.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == pack.InstallDescription);
        Assert.Contains(packRow.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("info") && b.IsEffectivelyVisible);

        // Blazor: the IDE's options first, the template's own after them, Aspire last; untrusted, container support and
        // Aspire are disabled with the reason; Individual Accounts locks HTTPS with its reason.
        list.SelectedItem = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Web.Blazor|C#");
        await model.WaitForTemplateAsync();
        Click(Named<Button>(window, "NextButton"));
        await ShellFixture.PumpUntil(() => model.CanGoNext);
        Click(Named<Button>(window, "NextButton"));
        Assert.Equal(NewProjectPage.Additional, model.Page);
        ShellFixture.Pump();
        var https = Named<CheckBox>(window, "HttpsBox");
        var aspire = Named<CheckBox>(window, "AspireBox");
        Assert.Equal(("C_onfigure for HTTPS", "E_nlist in Aspire orchestration"), (https.Content, aspire.Content));
        Assert.True(https.IsEffectivelyEnabled);
        Assert.False(aspire.IsEffectivelyEnabled);

        // An option's access key is its page's: Alt+O presses C_onfigure for HTTPS here, not Configure's hidden S_olution.
        var before = https.IsChecked;
        Press(window, Key.O, RawInputModifiers.Alt, "o");
        Assert.NotEqual(before, https.IsChecked);
        Assert.NotSame(Named<ComboBox>(window, "SolutionCombo"), Focused(window));
        https.IsChecked = true;
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Needs a trusted location (previous page).");
        Named<ComboBox>(window, "AuthCombo").SelectedItem = model.AuthChoices.Single(a => a.Value == "Individual");
        ShellFixture.Pump();
        Assert.False(https.IsEffectivelyEnabled);
        Assert.True(https.IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Individual Accounts needs HTTPS.");
        var options = Named<ItemsControl>(window, "OptionsList");
        Assert.Contains(options.GetVisualDescendants().OfType<CheckBox>(), c => Equals(c.Content, "Include _sample pages"));
        var platform = options.GetVisualDescendants().OfType<ComboBox>().First();
        var location = options.GetVisualDescendants().OfType<ComboBox>().Skip(1).First();
        Assert.True(location.IsEffectivelyEnabled);
        platform.SelectedItem = ((ChoiceOptionViewModel)model.Options[0]).Choices.Single(c => c.Value == "None");
        ShellFixture.Pump();
        Assert.False(location.IsEffectivelyEnabled); // Interactivity location needs a render mode

        // The framework's member: container support shows for Blazor's 11.0 template only.
        Assert.False(Named<CheckBox>(window, "DockerBox").IsEffectivelyVisible);
        Named<ComboBox>(window, "FrameworkCombo").SelectedItem = model.Frameworks.Single(c => c.Moniker == "net11.0");
        ShellFixture.Pump();
        Assert.True(Named<CheckBox>(window, "DockerBox").IsEffectivelyVisible);

        // Blank Solution has no third page: Configure's button is Create, and it asks for the solution's name only.
        Press(window, Key.Left, RawInputModifiers.Alt);
        Press(window, Key.Left, RawInputModifiers.Alt);
        list.SelectedItem = model.Templates.Single(r => r.Group.Kind == ProjectTemplateKind.BlankSolution);
        Press(window, Key.Right, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Configure, model.Page);
        Assert.False(Named<TextBox>(window, "ProjectNameBox").IsEffectivelyVisible);
        Assert.True(Named<Button>(window, "CreateButton").IsEffectivelyVisible);
        await ShellFixture.PumpUntil(() => Named<TextBox>(window, "SolutionNameBox").IsFocused, describe: () => $"focused: {Focused(window)}");

        model.Cancel();
        Assert.Null(await command);
    }

    [AvaloniaFact]
    public async Task Choosing_a_recent_template_then_one_in_the_list_keeps_both_lists_in_step()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        f.Shell.NewProjectHistory.AddRecentTemplate("dotnet:Microsoft.Test.xUnit|C#");
        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f);
        var list = Named<ListBox>(window, "TemplateList");
        var recent = Named<ListBox>(window, "RecentList");

        recent.SelectedItem = model.RecentTemplates[0];
        ShellFixture.Pump();
        Assert.Same(model.RecentTemplates[0], model.SelectedTemplate);
        Assert.Null(list.SelectedItem); // the recent row isn't the list's

        var console = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.Console|C#");
        model.SelectedTemplate = console;
        ShellFixture.Pump();
        Assert.Same(console, list.SelectedItem); // the list's binding survived the row it didn't have
        Assert.Null(recent.SelectedItem);
        list.SelectedItem = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Web.Blazor|C#");
        ShellFixture.Pump();
        Assert.Equal("Blazor Web App", model.SelectedTemplate!.Name);

        // A search keeps the chosen template selected when it still matches.
        model.Search = "blazor";
        ShellFixture.Pump();
        Assert.Same(model.SelectedTemplate, list.SelectedItem);
        model.Cancel();
        Assert.Null(await command);
    }

    [AvaloniaFact]
    public async Task Enter_in_the_search_goes_on_with_what_the_search_shows()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f);
        Named<ListBox>(window, "TemplateList").SelectedItem = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.Console|C#");
        ShellFixture.Pump();
        Assert.Equal("Console App", model.SelectedTemplate!.Name);

        // Typed and Enter at once (the search waits for more typing): Enter takes the best match of "blazor", not the
        // Console App it hid.
        model.SearchDelay = TimeSpan.FromSeconds(30);
        var search = Named<TextBox>(window, "SearchBox");
        search.Focus();
        window.KeyTextInput("blazor");
        Press(window, Key.Enter);
        await ShellFixture.PumpUntil(() => model.Page == NewProjectPage.Configure, describe: () => $"page {model.Page}, chosen {model.SelectedTemplate?.Name}");
        Assert.Equal("Blazor Web App", model.SelectedTemplate!.Name);
        model.Cancel();
        Assert.Null(await command);
    }

    [AvaloniaFact]
    public async Task On_macOS_option_arrows_stay_in_the_text_boxes_and_command_brackets_move_between_pages()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        f.Shell.NewProjectIsMacOS = true;
        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "⌘[");
        Named<ListBox>(window, "TemplateList").SelectedItem = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Common.Console|C#");
        await model.WaitForTemplateAsync();
        Press(window, Key.OemCloseBrackets, RawInputModifiers.Meta); // ⌘]
        Assert.Equal(NewProjectPage.Configure, model.Page);
        await ShellFixture.PumpUntil(() => model.CanGoNext);

        // ⌥← and ⌥→ in the project name are the text box's (a word back, a word on): the page stays.
        var name = Named<TextBox>(window, "ProjectNameBox");
        await ShellFixture.PumpUntil(() => name.IsFocused, describe: () => $"focused: {Focused(window)}");
        Press(window, Key.Left, RawInputModifiers.Alt);
        Press(window, Key.Right, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Configure, model.Page);
        window.LocationField.TextBox.Focus();
        Press(window, Key.Left, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Configure, model.Page);

        // ⌘] and ⌘[ move between pages from anywhere; ⌥← still does outside a text box.
        Press(window, Key.OemCloseBrackets, RawInputModifiers.Meta);
        Assert.Equal(NewProjectPage.Additional, model.Page);
        Press(window, Key.OemOpenBrackets, RawInputModifiers.Meta);
        Assert.Equal(NewProjectPage.Configure, model.Page);
        Named<CheckBox>(window, "TrustBox").Focus();
        Press(window, Key.Left, RawInputModifiers.Alt);
        Assert.Equal(NewProjectPage.Choose, model.Page);
        model.Cancel();
        Assert.Null(await command);
    }

    [AvaloniaFact]
    public async Task Each_page_s_tab_order_reaches_every_control()
    {
        await using var f = ShellFixture.Create();
        using var data = new TemplateTestData();
        UseRecordedTemplates(f, data);
        f.Shell.NewProjectHistory.AddRecentTemplate("dotnet:Microsoft.Common.Console|C#");
        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f);
        var problems = new List<string>();
        model.SelectedTemplate = model.Templates.Single(r => r.Id == "dotnet:Microsoft.Web.Blazor|C#");
        await model.WaitForTemplateAsync();
        problems.AddRange(UxPassTests.Unreached(window).Select(p => $"choose: {p}"));
        await model.GoForwardAsync();
        await ShellFixture.PumpUntil(() => model.CanGoNext);
        problems.AddRange(UxPassTests.Unreached(window).Select(p => $"configure: {p}"));
        await model.GoForwardAsync();
        ShellFixture.Pump();
        problems.AddRange(UxPassTests.Unreached(window).Select(p => $"additional: {p}"));
        Assert.True(problems.Count == 0, "Not reached with Tab:\n" + string.Join('\n', problems));
        model.Cancel();
        await command;
    }

    [AvaloniaFact]
    public async Task Create_from_the_window_with_trust_opens_the_trusted_solution_and_its_start_file()
    {
        // The real SDK, in a template hive of its own (the user's templates untouched), with --no-restore; restore on open
        // is off, so nothing reaches a package feed.
        var userData = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "codalus-wizard-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(TemplateService.HiveVariable, Path.Combine(userData, "template-hive"));
        await using var f = ShellFixture.Create(userData);
        using (var off = JsonDocument.Parse("false"))
        {
            await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, off.RootElement.Clone(), SettingScope.User, TestContext.Current.CancellationToken);
        }

        var location = Path.Combine(userData, "repos");
        var dialogs = false;
        f.Shell.Dialogs.PropertyChanged += (_, e) => dialogs |= e.PropertyName == nameof(DialogViewModel.IsOpen) && f.Shell.Dialogs.IsOpen;

        var command = f.Shell.Workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        var (model, window) = await OpenedAsync(f, seconds: 120);
        Named<TextBox>(window, "SearchBox").Text = "console";
        await ShellFixture.PumpUntil(() => model.Templates.Any(r => r.Group.ShortName == "console" && r.Group.Language == "C#"));
        Named<ListBox>(window, "TemplateList").SelectedItem = model.Templates.First(r => r.Group.ShortName == "console" && r.Group.Language == "C#");
        await model.WaitForTemplateAsync();
        Click(Named<Button>(window, "NextButton"));
        Assert.Equal(NewProjectPage.Configure, model.Page);

        Named<TextBox>(window, "ProjectNameBox").Text = "Hello";
        window.LocationField.TextBox.Text = location;
        var trust = Named<CheckBox>(window, "TrustBox");
        Assert.False(trust.IsChecked); // unchecked for a new location
        trust.IsChecked = true;
        Assert.True(model.TrustLocation);
        await ShellFixture.PumpUntil(() => Named<Button>(window, "NextButton").IsEffectivelyEnabled, seconds: 60);
        Click(Named<Button>(window, "NextButton"));
        Assert.Equal(NewProjectPage.Additional, model.Page);
        Assert.Contains(model.Frameworks, c => c.Label.Contains("(Long Term Support)", StringComparison.Ordinal) || c.Label.Contains("(Standard Term Support)", StringComparison.Ordinal));
        Assert.True(Named<Button>(window, "CreateButton").IsEffectivelyEnabled);
        Click(Named<Button>(window, "CreateButton"));

        var project = Path.Combine(location, "Hello", "Hello", "Hello.csproj");
        var result = await command.WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        Assert.Equal(project, result!.Value.GetString());
        Assert.False(f.Shell.IsNewProjectOpen); // closed before the solution opened
        Assert.False(window.IsVisible);
        Assert.False(dialogs); // trust was asked on the wizard's page: no prompt in the main window
        Assert.True(f.Shell.Trust.IsTrusted(location));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is { Trusted: true, Projects.Length: 1 } && f.Shell.Solutions.Projects.Count == 1, seconds: 240);
        Assert.Equal(project, Path.GetFullPath(f.Shell.Solutions.Solution!.Projects[0].Path));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(location, "Hello", "Hello", "Program.cs"), seconds: 60);
        Assert.Equal("dotnet:Microsoft.Common.Console|C#", f.Shell.NewProjectHistory.RecentTemplates[0].Id);
        Assert.Equal(location, f.Shell.NewProjectHistory.RecentLocations[0]);
    }
}
