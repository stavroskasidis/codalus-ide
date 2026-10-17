using System.Xml.Linq;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>M40a (ADR 0071): which projects run only on Windows, the WPF and Windows Forms item templates, and the markup compiler's errors in their files.</summary>
public sealed class WindowsDesktopTests
{
    private static ProjectInfo Project(string[] frameworks, bool wpf = false, bool winForms = false) =>
        new("/src/App/App.csproj", "App", ProjectStates.Loaded, null, frameworks, "WinExe", [], [], [], true) { UsesWpf = wpf, UsesWindowsForms = winForms };

    [Theory]
    [InlineData("net11.0-windows", true)]
    [InlineData("net10.0-windows10.0.19041.0", true)]
    [InlineData("net8.0-Windows", true)]
    [InlineData("net11.0", false)]
    [InlineData("net10.0-android", false)]
    [InlineData("net48", false)]
    [InlineData(null, false)]
    public void Windows_frameworks_are_told_apart(string? framework, bool windows) => Assert.Equal(windows, WindowsDesktopProjects.IsWindowsFramework(framework));

    [Fact]
    public void Wpf_winforms_and_windows_only_projects_run_only_on_windows()
    {
        var wpf = Project(["net11.0-windows"], wpf: true);
        Assert.True(WindowsDesktopProjects.RunsOnlyOnWindows(wpf));
        Assert.True(WindowsDesktopProjects.CannotRunOn(wpf, windows: false));
        Assert.False(WindowsDesktopProjects.CannotRunOn(wpf, windows: true));
        Assert.Equal("WPF", WindowsDesktopProjects.Kind(wpf));
        Assert.Equal("Windows Forms", WindowsDesktopProjects.Kind(Project(["net11.0-windows"], winForms: true)));

        // A console app for Windows only, and one that multi-targets: only the first is Windows only.
        var windowsConsole = Project(["net11.0-windows"]);
        Assert.True(WindowsDesktopProjects.RunsOnlyOnWindows(windowsConsole));
        Assert.Null(WindowsDesktopProjects.Kind(windowsConsole));
        Assert.False(WindowsDesktopProjects.RunsOnlyOnWindows(Project(["net11.0", "net11.0-windows"])));
        Assert.False(WindowsDesktopProjects.RunsOnlyOnWindows(Project(["net11.0"])));
    }

    [Fact]
    public void Wpf_and_windows_forms_items_are_offered_to_their_projects_with_their_companions()
    {
        var wpf = new ItemTemplateProject("C#", IsWpf: true);
        var forms = new ItemTemplateProject("C#", IsWinForms: true);
        var console = new ItemTemplateProject("C#");
        string[] wpfItems = ["wpfwindow", "wpfpage", "wpfusercontrol", "wpfresourcedictionary"];
        string[] formsItems = ["winformsform", "winformsusercontrol"];
        foreach (var name in wpfItems)
        {
            var template = ItemTemplates.BuiltIn.Single(t => t.ShortName == name);
            Assert.True(ItemTemplates.AppliesTo(template, wpf), name);
            Assert.False(ItemTemplates.AppliesTo(template, forms), name);
            Assert.False(ItemTemplates.AppliesTo(template, console), name);
        }

        foreach (var name in formsItems)
        {
            Assert.True(ItemTemplates.AppliesTo(ItemTemplates.BuiltIn.Single(t => t.ShortName == name), forms), name);
            Assert.False(ItemTemplates.AppliesTo(ItemTemplates.BuiltIn.Single(t => t.ShortName == name), wpf), name);
        }

        // A window: XAML with its x:Class in the folder's namespace, and its code-behind.
        var window = ItemTemplates.BuiltIn.Single(t => t.ShortName == "wpfwindow");
        var context = new ItemTemplateContext("AboutWindow", "WpfApp.Views");
        var xaml = XDocument.Parse(window.Content!(context));
        Assert.Equal("Window", xaml.Root!.Name.LocalName);
        Assert.Equal("WpfApp.Views.AboutWindow", xaml.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value);
        Assert.Equal("AboutWindow", xaml.Root.Attribute("Title")?.Value);
        Assert.Equal(["AboutWindow.xaml.cs"], window.CompanionNames("AboutWindow.xaml"));
        var codeBehind = window.Companions.Single().Content(context);
        Assert.StartsWith("using System.Windows;\n\nnamespace WpfApp.Views;\n", codeBehind, StringComparison.Ordinal);
        Assert.Contains("public partial class AboutWindow : Window", codeBehind, StringComparison.Ordinal);
        Assert.Contains("InitializeComponent();", codeBehind, StringComparison.Ordinal);

        // A user control's base class comes from System.Windows.Controls; a resource dictionary has no code.
        Assert.Contains("using System.Windows.Controls;", ItemTemplates.BuiltIn.Single(t => t.ShortName == "wpfusercontrol").Companions.Single().Content(context), StringComparison.Ordinal);
        var dictionary = ItemTemplates.BuiltIn.Single(t => t.ShortName == "wpfresourcedictionary");
        Assert.Empty(dictionary.Companions);
        Assert.Equal("ResourceDictionary", XDocument.Parse(dictionary.Content!(context)).Root!.Name.LocalName);

        // A form: its code and its Designer.cs.
        var form = ItemTemplates.BuiltIn.Single(t => t.ShortName == "winformsform");
        var formContext = new ItemTemplateContext("SettingsForm", "WinFormsApp", FileScopedNamespace: false);
        Assert.Equal(["SettingsForm.Designer.cs"], form.CompanionNames("SettingsForm.cs"));
        Assert.Contains("public partial class SettingsForm : Form", form.Content!(formContext), StringComparison.Ordinal);
        var designer = form.Companions.Single().Content(formContext);
        Assert.StartsWith("namespace WinFormsApp\n{\n", designer, StringComparison.Ordinal);
        Assert.Contains("partial class SettingsForm", designer, StringComparison.Ordinal);
        Assert.Contains("private void InitializeComponent()", designer, StringComparison.Ordinal);
        Assert.Contains("Text = \"SettingsForm\";", designer, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSize", ItemTemplates.BuiltIn.Single(t => t.ShortName == "winformsusercontrol").Companions.Single().Content(formContext), StringComparison.Ordinal);
    }

    [Fact]
    public void Markup_compiler_errors_land_in_their_xaml_files()
    {
        var error = new BuildDiagnosticInfo(BuildSeverities.Error, "MC3072", "The property 'Bogus' does not exist in XML namespace 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'.",
            "/src/WpfApp/MainWindow.xaml", 31, 18, 0, 0, "/src/WpfApp/WpfApp.csproj", null);
        var compile = error with { Code = "CS0103", File = "/src/WpfApp/MainWindow.xaml.cs" };
        var byFile = XamlBuildDiagnostics.ByFile([error, compile, error]);
        var squiggle = Assert.Single(Assert.Single(byFile).Value);
        Assert.Equal("MC3072", squiggle.Code);
        Assert.Equal((30, 17), (squiggle.Range.Start.Line, squiggle.Range.Start.Character));
    }
}
