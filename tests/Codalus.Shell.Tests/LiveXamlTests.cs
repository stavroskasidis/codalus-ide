using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Codalus.Previewer.Agent;

namespace Codalus.Shell.Tests;

/// <summary>A user control whose XAML the tests load again (it has none of its own: XAML Hot Reload gives it its content).</summary>
public sealed class LiveReloadView : UserControl
{
#pragma warning disable CS0649, SA1401 // set by the reload, as Avalonia's name generator's fields are by InitializeComponent
    internal TextBlock? Greeting;
#pragma warning restore CS0649, SA1401

    public int Clicks { get; private set; }

    // Named by the reloaded XAML's Click: a private handler, as a code-behind's.
    private void OnGo(object? sender, RoutedEventArgs e) => Clicks++;
}

/// <summary>The data context the reload keeps.</summary>
public sealed class LiveReloadModel
{
    public string Message { get; set; } = "Hello";
}

/// <summary>
/// XAML Hot Reload and failed bindings in the live agent (M64, ADR 0103), in-process against a headless window as
/// <see cref="LiveAgentTests"/>: the reload goes through Avalonia's runtime XAML compiler from Avalonia's own designer
/// host (the package's <c>Avalonia.Designer.HostApp.dll</c>, as the IDE passes it), and the failures through Avalonia's
/// logger.
/// </summary>
public sealed class LiveXamlTests
{
    private const string Header = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static string View(string body, string extra = "x:Class=\"Codalus.Shell.Tests.LiveReloadView\"") =>
        $"<UserControl {Header} {extra} Background=\"Red\">\n  <StackPanel>\n{body}\n  </StackPanel>\n</UserControl>";

    /// <summary>The designer host of the Avalonia these tests run (its package in the NuGet cache).</summary>
    internal static string DesignerHost()
    {
        var version = typeof(Window).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(Window).Assembly.GetName().Version!.ToString(3);
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var tools = Path.Combine(packages, "avalonia", version, "tools");
        var host = Directory.Exists(tools)
            ? Directory.GetDirectories(tools).Where(d => !Path.GetFileName(d).StartsWith("netstandard", StringComparison.Ordinal)).OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "designer", "Avalonia.Designer.HostApp.dll")).FirstOrDefault(File.Exists)
            : null;
        Assert.True(host is not null, $"No Avalonia designer host under {tools}.");
        return host;
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [AvaloniaFact]
    public async Task Hot_reload_loads_the_xaml_into_the_live_control_and_keeps_its_state()
    {
        var view = new LiveReloadView { DataContext = new LiveReloadModel(), Tag = "kept" };
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { view } } };
        window.Show();
        Pump();
        var agent = new LiveAgent(_ => { }, () => [window]);
        var host = DesignerHost();
        var assembly = typeof(LiveReloadView).Assembly.GetName().Name!;
        Task<string> Reload(string xaml) => Task.Run(() => agent.Handle(["1", "reload", host, assembly, "avares://" + assembly + "/LiveReloadView.axaml", "0", Base64(xaml)]));

        // The content, a property, a named field, a binding to the kept data context, a handler of the running class.
        Assert.Equal("applied\t1", await Reload(View("    <TextBlock x:Name=\"Greeting\" Text=\"{Binding Message}\" />\n    <Button x:Name=\"Go\" Content=\"Go\" Click=\"OnGo\" />")));
        Pump();
        var panel = Assert.IsType<StackPanel>(view.Content);
        Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color);
        Assert.Same(panel.Children[0], view.Greeting);
        Assert.Equal("Hello", view.Greeting!.Text);
        Assert.Equal("kept", view.Tag);
        Assert.IsType<LiveReloadModel>(view.DataContext);
        ((Button)panel.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, view.Clicks);

        // Again: the names go to a new scope, the field to the new element.
        var first = view.Greeting;
        Assert.Equal("applied\t1", await Reload(View("    <TextBlock x:Name=\"Greeting\" Text=\"Again\" />")));
        Pump();
        Assert.NotSame(first, view.Greeting);
        Assert.Equal("Again", view.Greeting!.Text);
        var applied = view.Content;

        // What can't be loaded leaves the control as it was, with the line.
        var broken = await Reload(View("    <TextBlock Text=\"Broken\">"));
        Assert.StartsWith("failed\t", broken, StringComparison.Ordinal);
        Assert.Same(applied, view.Content);
        Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(view.Background).Color);
        var missingHandler = await Reload(View("    <Button Content=\"Go\" Click=\"OnMissing\" />"));
        Assert.StartsWith("failed\t", missingHandler, StringComparison.Ordinal);
        Assert.StartsWith("failed\t3\t", missingHandler, StringComparison.Ordinal);
        Assert.Same(applied, view.Content);
        var unknownType = await Reload(View("    <Bogus />"));
        Assert.StartsWith("failed\t", unknownType, StringComparison.Ordinal);
        Assert.Matches(@"^failed\t[1-9]\d*\t", unknownType);
        Assert.Same(applied, view.Content);

        // A class the running app doesn't have: a restart; a file without x:Class (styles): nothing to reload live.
        Assert.StartsWith("failed\t0\t0\tThe running app has no class Codalus.Shell.Tests.NotThere", await Reload(View(string.Empty, "x:Class=\"Codalus.Shell.Tests.NotThere\"")), StringComparison.Ordinal);
        Assert.Equal("none", await Reload($"<Styles {Header} />"));
        window.Close();
    }

    /// <param name="othersFirst">
    /// The list already full of other windows' failures, as in a run of the whole project (2026-11-19, the 1.0 release
    /// candidate's Windows run): the test took them and found none of its own.
    /// </param>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_bindings_come_with_their_element_and_its_ancestors(bool othersFirst)
    {
        if (othersFirst)
        {
            for (var i = 0; i < 600; i++)
            {
                LiveBindings.Line($"[Binding]An error occurred binding 'Text' to 'Other{i}' at 'Other{i}': 'Value is null.' (TextBlock #{1_000_000 + i})");
            }
        }

        var events = new List<string>();
        LiveBindings.Install(line => { lock (events) { events.Add(line); } });
        LiveBindings.Hook();
        Assert.True(LiveBindings.Hooked, "Avalonia's logger isn't hooked.");

        // The hook is the process's: in a run of the whole project, other tests' windows have failed bindings too, and
        // nothing takes them. Taken now, they can neither be the event this test waits for nor fill the list to its
        // MaxFailures, past which this test's own failures were dropped.
        LiveBindings.Take([]);
        lock (events)
        {
            events.Clear();
        }

        // A failure is reported once per element name, property and path for the process's life: a name of this run's
        // own, or the theory's second case (or a repeat) would find its failure already seen.
        var name = "Status" + Guid.NewGuid().ToString("N")[..8];
        var text = new TextBlock { Name = name };
        text.Bind(TextBlock.TextProperty, new Binding("Missing"));
        var other = new TextBlock();
        other.Bind(TextBlock.TextProperty, new Binding("AlsoMissing"));
        var view = new LiveReloadView { DataContext = new LiveReloadModel(), Content = new StackPanel { Children = { text, other } } };
        var window = new Window { Width = 300, Height = 200, Content = view };
        window.Show();
        Pump();
        bool Told()
        {
            lock (events)
            {
                return events.Contains("event\tbindings");
            }
        }

        for (var i = 0; i < 100 && !Told(); i++)
        {
            await Task.Delay(20);
            Pump();
        }

        Assert.True(Told(), "No event for the failed binding.");

        // Taken until both of this test's failures came: another test's failure can be the event, before them.
        var agent = new LiveAgent(_ => { }, () => [window]);
        var records = new List<string[]>();
        bool Has(string path) => records.Any(r => r.Length > 1 && r[1] == path);
        for (var i = 0; i < 100 && !(Has("Missing") && Has("AlsoMissing")); i++)
        {
            var taken = await Task.Run(() => agent.Handle([(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "bindings"]));
            records.AddRange(taken.Split(LiveAgent.Record).Where(r => r.Length > 0).Select(r => r.Split(LiveAgent.Field)));
            await Task.Delay(20);
            Pump();
        }

        var failure = Assert.Single(records, r => r[1] == "Missing");
        Assert.Equal("Text", failure[0]);
        Assert.True(typeof(TextBlock).FullName == failure[3], string.Join(" | ", failure));
        Assert.Equal(name, failure[4]);
        Assert.StartsWith(typeof(StackPanel).FullName + "|" + typeof(LiveReloadView).FullName + "|", failure[5], StringComparison.Ordinal);
        Assert.Contains(typeof(Window).FullName!, failure[5], StringComparison.Ordinal);
        Assert.Contains("Missing", failure[2], StringComparison.Ordinal);

        // An element without a name: Avalonia's log names it by its hash code.
        var unnamed = Assert.Single(records, r => r[1] == "AlsoMissing");
        Assert.True(typeof(TextBlock).FullName == unnamed[3] && unnamed[4].Length == 0 && unnamed[5].Contains(typeof(LiveReloadView).FullName!, StringComparison.Ordinal), string.Join(" | ", unnamed));

        // Taken: the next request has only new ones.
        Assert.DoesNotContain("Missing", await Task.Run(() => agent.Handle(["101", "bindings"])), StringComparison.Ordinal);
        window.Close();
    }

    [Theory]
    [InlineData("[Binding]An error occurred binding 'Text' to 'Missing' at 'Missing': 'Could not find a matching property accessor for 'Missing' on 'Model'.' (TextBlock #4321)", "Text", "Missing", "TextBlock", 4321, "")]
    [InlineData("[Binding]An error occurred binding 'Text' to 'Missing' at 'Missing': 'Could not find a matching property accessor for 'Missing' on 'Model'.' (TextBlock #Status)", "Text", "Missing", "TextBlock", 0, "Status")]
    [InlineData("[Binding] An error occurred binding Text to Items[0].Title at Items: Value is null. (TextBlock #-7)", "Text", "Items[0].Title", "TextBlock", -7, "")]
    public void Binding_log_lines_parse(string line, string property, string path, string type, int hash, string name)
    {
        var parsed = LiveBindings.Parse(line);
        Assert.NotNull(parsed);
        Assert.Equal((property, path, type, hash, name), (parsed.Property, parsed.Path, parsed.TypeName, parsed.Hash, parsed.ElementName));
        Assert.Null(LiveBindings.Parse("[Layout]Measure requested 1,1"));
    }

    private static void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
