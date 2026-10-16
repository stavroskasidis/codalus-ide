using Codalus.Previewer.Wpf;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Previewer;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M40b (ADR 0072) on every OS: which previewer a XAML file gets (Avalonia's or WPF's, by the file and its project),
/// which WPF apps the WPF previewer and live agent load into, how the WPF designer host starts (the agent served by the
/// host itself), what it reads from the root's start tag before loading (x:Class, the design size, d:DesignInstance;
/// <see cref="DesignXaml"/> is compiled into these tests), and its messages through the workbench's codec.
/// </summary>
public sealed class WpfPreviewerRoutingTests
{
    private static string Sample(params string[] parts) =>
        Path.Combine([DiagnosticsFixture.Metadata("CodalusRepoRoot"), "samples", "WindowsDesktop", .. parts]);

    private static ProjectInfo Project(string name, bool wpf, string outputType = "WinExe", string[]? frameworks = null, params ReferenceInfo[] references) =>
        new(Path.Combine(Path.GetTempPath(), name, name + ".csproj"), name, "Loaded", null, frameworks ?? ["net11.0-windows"], outputType, [], references, [], true)
        {
            UsesWpf = wpf,
        };

    private static ReferenceInfo Package(string name) => new(ReferenceKinds.Package, name, "11.0.0", null, "net11.0", true);

    // ------------------------------------------------------------------ routing

    [Fact]
    public void Wpf_projects_xaml_goes_to_the_wpf_previewer_and_everything_else_to_avalonias()
    {
        var wpf = Project("WpfApp", wpf: true);
        Assert.Equal(PreviewDialect.Wpf, PreviewService.DialectOf("/src/WpfApp/MainWindow.xaml", wpf));
        Assert.Equal(PreviewDialect.Wpf, PreviewService.DialectOf("/src/WpfApp/Views/Dialog.XAML", wpf));

        // An .axaml file is Avalonia's wherever it is; so is a WPF project's XAML when it references Avalonia.
        Assert.Equal(PreviewDialect.Avalonia, PreviewService.DialectOf("/src/WpfApp/Island.axaml", wpf));
        Assert.Equal(PreviewDialect.Avalonia, PreviewService.DialectOf("/src/Mixed/View.xaml", Project("Mixed", wpf: true, references: Package("Avalonia.Desktop"))));
        Assert.Equal(PreviewDialect.Avalonia, PreviewService.DialectOf("/src/AvaloniaApp/MainWindow.xaml", Project("AvaloniaApp", wpf: false, references: Package("Avalonia"))));
        Assert.Equal(PreviewDialect.Avalonia, PreviewService.DialectOf("/src/AvaloniaApp/MainWindow.axaml", Project("AvaloniaApp", wpf: false)));
    }

    [Fact]
    public void The_wpf_previewer_needs_dotnet_8_or_later()
    {
        Assert.Null(PreviewService.WpfFrameworkProblem(Project("App", wpf: true, frameworks: ["net8.0-windows"])));
        Assert.Null(PreviewService.WpfFrameworkProblem(Project("App", wpf: true, frameworks: ["net11.0-windows10.0.19041.0"])));
        Assert.Null(PreviewService.WpfFrameworkProblem(Project("App", wpf: true, frameworks: ["net48", "net9.0-windows"])));
        Assert.Null(PreviewService.WpfFrameworkProblem(Project("App", wpf: true, frameworks: [])));
        Assert.Contains("need .NET 8 or later", PreviewService.WpfFrameworkProblem(Project("Old", wpf: true, frameworks: ["net6.0-windows", "netcoreapp3.1"])), StringComparison.Ordinal);
        Assert.Contains(".NET Framework (net48)", PreviewService.WpfFrameworkProblem(Project("Classic", wpf: true, frameworks: ["net48"])), StringComparison.Ordinal);

        Assert.Equal(8, PreviewService.DotNetMajor("net8.0-windows"));
        Assert.Equal(11, PreviewService.DotNetMajor("net11.0"));
        Assert.Equal(3, PreviewService.DotNetMajor("netcoreapp3.1"));
        Assert.Null(PreviewService.DotNetMajor("net48"));
        Assert.Null(PreviewService.DotNetMajor("netstandard2.0"));
    }

    [Fact]
    public void The_wpf_designer_host_serves_the_agent_itself_and_avalonias_gets_the_startup_hook()
    {
        var agent = typeof(PreviewService).Assembly.Location; // any file that exists
        var target = new PreviewTarget("/a/MainWindow.xaml", "/a/A.csproj", "/a/A.csproj", "/a/bin/A.dll", "/a/bin/A.dll", "/ide/wpfpreviewer/codalus-wpfpreviewer.dll", "/MainWindow.xaml")
        {
            Dialect = PreviewDialect.Wpf,
        };
        var wpf = PreviewService.LaunchFor(target, agent);
        Assert.True(wpf.AgentInHost);
        Assert.Null(wpf.Agent);
        Assert.Equal("/ide/wpfpreviewer/codalus-wpfpreviewer.dll", wpf.HostApp);
        Assert.Equal("/a/bin/A.runtimeconfig.json", wpf.RuntimeConfig.Replace('\\', '/'));

        var environment = new Dictionary<string, string?> { [PreviewerSession.StartupHooksVariable] = "/other/hook.dll" };
        PreviewerSession.ApplyAgentEnvironment(environment, wpf, 4242, "token");
        Assert.Equal("4242", environment[PreviewerSession.AgentPortVariable]);
        Assert.Equal("token", environment[PreviewerSession.AgentTokenVariable]);
        Assert.Equal("/other/hook.dll", environment[PreviewerSession.StartupHooksVariable]); // no agent hook for WPF

        var avalonia = PreviewService.LaunchFor(target with { Dialect = PreviewDialect.Avalonia }, agent);
        Assert.False(avalonia.AgentInHost);
        environment = new Dictionary<string, string?> { [PreviewerSession.StartupHooksVariable] = "/other/hook.dll" };
        PreviewerSession.ApplyAgentEnvironment(environment, avalonia, 4242, "token");
        Assert.Equal(agent + Path.PathSeparator + "/other/hook.dll", environment[PreviewerSession.StartupHooksVariable]);
        Assert.Equal("4242", environment[PreviewerSession.AgentPortVariable]);

        // Without an agent file, Avalonia's previews go without selection: nothing is set.
        environment = [];
        PreviewerSession.ApplyAgentEnvironment(environment, new PreviewerLaunch("/host.dll", "/app.dll", "/missing/agent.dll"), 4242, "token");
        Assert.Empty(environment);
    }

    // ------------------------------------------------------------------ the root's start tag

    [Fact]
    public void The_fixtures_window_names_its_class_design_size_and_design_instance()
    {
        var design = DesignXaml.Read(File.ReadAllText(Sample("WpfApp", "MainWindow.xaml")));
        Assert.Equal(DesignXaml.Presentation, design.RootNamespace);
        Assert.Equal("Window", design.RootName);
        Assert.Equal("WpfApp.MainWindow", design.ClassName);
        Assert.Equal("MainWindow", design.RootType);
        Assert.Equal(480, design.DesignWidth);
        Assert.Equal(360, design.DesignHeight);
        Assert.Equal(new DesignInstance("clr-namespace:WpfApp.ViewModels", "MainViewModel", IsDesignTimeCreatable: true, CreateList: false), design.DataContext);
        Assert.Null(design.DataContextProblem);
        Assert.False(design.IsApplication);

        var control = DesignXaml.Read(File.ReadAllText(Sample("WpfApp", "Controls", "RatingControl.xaml")));
        Assert.Equal("RatingControl", control.RootType);
        Assert.Equal((120d, 24d), (control.DesignWidth, control.DesignHeight));
        Assert.Null(control.DataContext);

        Assert.True(DesignXaml.Read(File.ReadAllText(Sample("WpfApp", "App.xaml"))).IsApplication);
        var styles = DesignXaml.Read(File.ReadAllText(Sample("WpfApp", "Themes", "Styles.xaml")));
        Assert.Equal("ResourceDictionary", styles.RootType);
        Assert.Null(styles.ClassName);
    }

    [Fact]
    public void Design_instances_are_read_in_each_of_their_forms()
    {
        static DesignXaml Root(string attributes) => DesignXaml.Read(
            "<!-- a comment first -->\n<UserControl xmlns=\"" + DesignXaml.Presentation + "\" xmlns:x=\"" + DesignXaml.Xaml + "\" xmlns:d=\"" + DesignXaml.Blend
            + "\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:vm=\"clr-namespace:App.ViewModels;assembly=App.Core\" mc:Ignorable=\"d\" " + attributes + "><Grid/></UserControl>");

        Assert.Equal(new DesignInstance("clr-namespace:App.ViewModels;assembly=App.Core", "ItemViewModel", false, false), Root("d:DataContext=\"{d:DesignInstance vm:ItemViewModel}\"").DataContext);
        Assert.Equal(new DesignInstance("clr-namespace:App.ViewModels;assembly=App.Core", "ItemViewModel", true, true),
            Root("d:DataContext=\"{d:DesignInstance Type={x:Type vm:ItemViewModel}, IsDesignTimeCreatable=True, CreateList=True}\"").DataContext);
        Assert.Equal("ItemViewModel", Root("d:DataContext=\"{d:DesignInstance {x:Type TypeName=vm:ItemViewModel}, IsDesignTimeCreatable=true}\"").DataContext?.TypeName);
        Assert.Equal(DesignXaml.Presentation, Root("d:DataContext=\"{d:DesignInstance Type=Button}\"").DataContext?.Namespace);

        // What it can't use says why, and the preview goes on without design-time data.
        Assert.Contains("isn't declared", Root("d:DataContext=\"{d:DesignInstance Type=missing:Thing}\"").DataContextProblem, StringComparison.Ordinal);
        Assert.Contains("d:DesignData", Root("d:DataContext=\"{d:DesignData Source=Sample.xaml}\"").DataContextProblem, StringComparison.Ordinal);
        Assert.Contains("names no type", Root("d:DataContext=\"{d:DesignInstance IsDesignTimeCreatable=True}\"").DataContextProblem, StringComparison.Ordinal);
        Assert.Contains("isn't a d:DesignInstance", Root("d:DataContext=\"Hello\"").DataContextProblem, StringComparison.Ordinal);

        // Sizes that aren't positive numbers are no size.
        var odd = Root("d:DesignWidth=\"Auto\" d:DesignHeight=\"-5\"");
        Assert.Null(odd.DesignWidth);
        Assert.Null(odd.DesignHeight);

        // A start tag that isn't well-formed: nothing (the loader then reports the error with its position).
        Assert.Same(DesignXaml.Empty, DesignXaml.Read("<Window x:Class=\"A\" Title=\"unterminated>"));
        Assert.Same(DesignXaml.Empty, DesignXaml.Read(string.Empty));
    }

    [Fact]
    public void Clr_namespaces_name_their_assembly_or_the_xamls_own()
    {
        Assert.Equal(("WpfApp.ViewModels", (string?)null), DesignXaml.ClrNamespace("clr-namespace:WpfApp.ViewModels"));
        Assert.Equal(("App.Controls", "App.Controls.Library"), DesignXaml.ClrNamespace("clr-namespace:App.Controls;assembly=App.Controls.Library"));
        Assert.Equal(("System", "mscorlib"), DesignXaml.ClrNamespace("clr-namespace:System; assembly=mscorlib"));
        Assert.Null(DesignXaml.ClrNamespace(DesignXaml.Presentation));
    }

    // ------------------------------------------------------------------ the designer host's messages

    [Fact]
    public void The_wpf_designer_hosts_messages_round_trip_through_the_codec()
    {
        static PreviewMessage RoundTrip(PreviewMessage message)
        {
            var (type, body) = RemoteProtocol.Encode(message);
            return RemoteProtocol.Decode(type, body);
        }

        Assert.Equal(new StartDesignerSessionMessage("abc"), RoundTrip(new StartDesignerSessionMessage("abc")));

        // A loader's error with its position, as the WPF host sends it (no handle).
        var error = (UpdateXamlResultMessage)RoundTrip(new UpdateXamlResultMessage("The member \"Colour\" is not recognized.", null,
            new PreviewExceptionDetails("XamlParseException", "The member \"Colour\" is not recognized.", 12, 9)));
        Assert.Equal(12, error.Exception!.LineNumber);
        Assert.Equal(9, error.Exception.LinePosition);
        Assert.Equal("XamlParseException", error.Exception.ExceptionType);
        Assert.Null(((UpdateXamlResultMessage)RoundTrip(new UpdateXamlResultMessage(null, null, null))).Error);

        // RenderTargetBitmap's Pbgra32 pixels as BGRA, or swapped to the RGBA the IDE asks for.
        var frame = (FrameMessage)RoundTrip(new FrameMessage(7, PreviewPixelFormat.Bgra8888, [1, 2, 3, 255, 4, 5, 6, 255], 2, 1, 8, 192, 192));
        Assert.Equal((PreviewPixelFormat.Bgra8888, 2, 1, 8, 192d), (frame.Format, frame.Width, frame.Height, frame.Stride, frame.DpiX));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, frame.Data);
    }
}
