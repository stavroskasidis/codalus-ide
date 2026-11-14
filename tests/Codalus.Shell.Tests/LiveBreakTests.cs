using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Codalus.Previewer.Agent;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell.Tests;

/// <summary>A user control whose XAML the breakpoint tests load (with its file and lines, as a Debug build has them).</summary>
public sealed class LiveBreakView : UserControl
{
    // Named by the XAML's Click (an event: a breakpoint on it can't stop).
    private void OnGo(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Tag = "clicked";
}

/// <summary>
/// XAML breakpoints, Break When Value Changes, break at a failed binding and the style explanation in the live agent
/// (M64b, ADR 0103), in-process against a headless window as <see cref="LiveXamlTests"/>: the XAML is compiled at run
/// time with its source information (Avalonia's <c>XamlSourceInfo</c>, as a Debug build's), and the agent's stops are
/// recorded instead of breaking into a debugger. The breakpoints' targets come from the IDE's own reading of the XAML
/// (<see cref="XamlBreakpoints"/>), so the two sides are checked together.
/// </summary>
public sealed class LiveBreakTests
{
    private const string Header = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    // Line 3: the TextBlock's start tag; 4: its Text attribute; 5: a binding; 7: an event; 9: a property element.
    private const string View = $"<UserControl {Header} x:Class=\"Codalus.Shell.Tests.LiveBreakView\">\n"
        + "  <StackPanel>\n"
        + "    <TextBlock x:Name=\"Title\"\n"
        + "               Text=\"Hello\"\n"
        + "               Tag=\"{Binding Message}\" />\n"
        + "    <Button Content=\"Go\"\n"
        + "            Click=\"OnGo\" />\n"
        + "    <ItemsControl>\n"
        + "      <ItemsControl.ItemsPanel>\n"
        + "        <ItemsPanelTemplate><StackPanel /></ItemsPanelTemplate>\n"
        + "      </ItemsControl.ItemsPanel>\n"
        + "    </ItemsControl>\n"
        + "  </StackPanel>\n"
        + "</UserControl>";

    private sealed record Stop(XamlStop What, object? Element, string? TextAtStop);

    /// <summary>The agent as under a debugger, its stops recorded; put back when disposed.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly Action<XamlStop, object?> _breaker = LiveBreak.Breaker;
        private readonly Func<bool> _attached = LiveBreak.Attached;
        private readonly List<Stop> _stops = [];

        public Recorder()
        {
            LiveBreak.Attached = () => true;
            LiveBreak.Breaker = (stop, element) =>
            {
                lock (_stops)
                {
                    _stops.Add(new Stop(stop, element, (element as TextBlock)?.Text));
                }
            };
            LiveBreak.Install();
            LiveBreak.Connect(_ => { });
        }

        public List<Stop> Stops
        {
            get
            {
                lock (_stops)
                {
                    return [.. _stops];
                }
            }
        }

        public void Dispose()
        {
            LiveBreak.Configure("bindings\t0\n");
            LiveBreak.Breaker = _breaker;
            LiveBreak.Attached = _attached;
        }
    }

    private static string Breakpoints(string file, bool bindings, params int[] lines) =>
        XamlBreakpoints.Encode(lines.Select(l => new XamlBreakpoints.AgentBreakpoint("bp" + l, file, XamlBreakpoints.Resolve(View, l))), bindings);

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    [Fact]
    public void The_ide_reads_a_lines_breakpoint_as_the_element_or_attribute_there()
    {
        var element = XamlBreakpoints.Resolve(View, 3);
        Assert.Equal((XamlBreakpointKind.Element, 3, 6, "TextBlock"), (element.Kind, element.Line, element.Column, element.Element));
        var attribute = XamlBreakpoints.Resolve(View, 4);
        Assert.Equal((XamlBreakpointKind.Attribute, 3, 6, "Text", false), (attribute.Kind, attribute.Line, attribute.Column, attribute.Property, attribute.Binding));
        Assert.True(XamlBreakpoints.Resolve(View, 5) is { Kind: XamlBreakpointKind.Attribute, Property: "Tag", Binding: true });
        var propertyElement = XamlBreakpoints.Resolve(View, 9);
        Assert.Equal((XamlBreakpointKind.Attribute, 8, "ItemsControl", "ItemsPanel"), (propertyElement.Kind, propertyElement.Line, propertyElement.Element, propertyElement.Property));
        Assert.True(XamlBreakpoints.Resolve(View, 7) is { Kind: XamlBreakpointKind.Attribute, Property: "Click", Element: "Button" });
        Assert.Equal(XamlBreakpointKind.None, XamlBreakpoints.Resolve(View, 13).Kind);
        Assert.NotNull(XamlBreakpoints.Resolve(View, 13).Problem);
        Assert.True(XamlBreakpoints.Resolve($"<Window {Header}\n        x:Class=\"A.B\"\n        x:Name=\"Root\" />", 2) is { Kind: XamlBreakpointKind.None, Problem: not null });
        Assert.True(XamlBreakpoints.Resolve($"<Window {Header}\n        x:Class=\"A.B\"\n        x:Name=\"Root\" />", 3) is { Kind: XamlBreakpointKind.Attribute, Property: "Name" });
        Assert.True(XamlBreakpoints.Resolve("<Window>\n  <!-- <Button /> -->\n  <Grid\n    DockPanel.Dock=\"Top\" />\n</Window>", 4) is { Kind: XamlBreakpointKind.Attribute, Property: "DockPanel.Dock", Line: 3 });
        Assert.Equal(XamlBreakpointKind.None, XamlBreakpoints.Resolve("<Window>\n  <!-- <Button /> -->\n</Window>", 2).Kind);
        Assert.True(XamlBreakpoints.IsXamlFile("a/MainWindow.axaml") && XamlBreakpoints.IsXamlFile("b/App.xaml") && !XamlBreakpoints.IsXamlFile("c/App.cs"));
    }

    [AvaloniaFact]
    public async Task An_element_breakpoint_stops_at_its_creation_and_an_attribute_one_when_xaml_sets_it()
    {
        using var recorder = new Recorder();
        var file = Path.Combine(Path.GetTempPath(), "codalus-live-break", Guid.NewGuid().ToString("N"), "LiveBreakView.axaml");
        var states = LiveBreak.Set(Breakpoints(file, false, 3, 4, 5, 7));
        Assert.All(states.Split(LiveAgent.Record), s => Assert.Equal("1", s.Split(LiveAgent.Field)[1]));

        var view = new LiveBreakView { DataContext = new LiveReloadModel { Message = "bound" } };
        var window = new Window { Width = 300, Height = 200, Content = view };
        window.Show();
        Pump();
        var agent = new LiveAgent(_ => { }, () => [window]);
        var assembly = typeof(LiveBreakView).Assembly.GetName().Name!;
        var reply = await Task.Run(() => agent.Handle(["1", "reload", LiveXamlTests.DesignerHost(), assembly, "avares://" + assembly + "/LiveBreakView.axaml", "0", Base64(View), file]));
        Assert.Equal("applied\t1", reply);
        Pump();

        var stops = recorder.Stops.Where(s => s.What.File == file).ToList();
        var created = Assert.Single(stops, s => s.What.Kind == "element");
        Assert.Equal(("bp3", 3, 6), (created.What.BreakpointId, created.What.Line, created.What.Column));
        Assert.Equal(typeof(TextBlock).FullName, created.What.ElementType);

        // Created, not yet set by its XAML: no text, no name.
        Assert.IsType<TextBlock>(created.Element);
        Assert.True(string.IsNullOrEmpty(created.TextAtStop), created.TextAtStop);

        var set = Assert.Single(stops, s => s.What is { Kind: "attribute", Property: "Text" });
        Assert.Equal(("bp4", "Hello"), (set.What.BreakpointId, set.What.NewValue));
        Assert.Same(created.Element, set.Element);

        // A binding's attribute stops at the value it sets, after XAML's initialization too.
        Assert.Contains(stops, s => s.What is { Kind: "attribute", Property: "Tag", NewValue: "bound" });

        // An event isn't a property: the agent says so, and never stops for it.
        var problems = LiveBreak.States().Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field)).ToDictionary(r => r[0], r => (r[1], r[2]));
        Assert.Equal("0", problems["bp7"].Item1);
        Assert.Contains("Click", problems["bp7"].Item2, StringComparison.Ordinal);
        Assert.Equal("1", problems["bp3"].Item1);

        // A breakpoint taken away doesn't stop the next reload.
        LiveBreak.Set(Breakpoints(file, false, 4));
        var before = recorder.Stops.Count(s => s.What.File == file);
        Assert.Equal("applied\t1", await Task.Run(() => agent.Handle(["2", "reload", LiveXamlTests.DesignerHost(), assembly, "avares://" + assembly + "/LiveBreakView.axaml", "0", Base64(View), file])));
        Pump();
        var after = recorder.Stops.Where(s => s.What.File == file).Skip(before).ToList();
        Assert.DoesNotContain(after, s => s.What.Kind == "element");
        Assert.Single(after, s => s.What is { Kind: "attribute", Property: "Text" });
        window.Close();
    }

    [AvaloniaFact]
    public async Task Break_when_value_changes_stops_on_a_change_until_it_is_turned_off()
    {
        using var recorder = new Recorder();
        var text = new TextBlock { Name = "Counter", Text = "0" };
        var window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { text } } };
        window.Show();
        Pump();
        var agent = new LiveAgent(_ => { }, () => [window]);
        var windowId = (await Task.Run(() => agent.Handle(["1", "windows"]))).Split(LiveAgent.Field)[0];
        var id = (await Task.Run(() => agent.Handle(["2", "tree", windowId, "logical"]))).Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field)).Single(r => r[3] == "Counter")[0];
        Assert.Equal("ok", await Task.Run(() => agent.Handle(["3", "watch", id, "Text", "on"])));

        text.Text = "1";
        var changed = Assert.Single(recorder.Stops, s => s.What.Kind == "value");
        Assert.Equal(("Text", "0", "1", "Counter"), (changed.What.Property, changed.What.OldValue, changed.What.NewValue, changed.What.ElementName));
        Assert.Same(text, changed.Element);

        // Another property of it doesn't stop; off, the watched one doesn't either.
        text.Tag = "other";
        Assert.Single(recorder.Stops, s => s.What.Kind == "value");
        Assert.Equal("ok", await Task.Run(() => agent.Handle(["4", "watch", id, "Text", "off"])));
        text.Text = "2";
        Assert.Single(recorder.Stops, s => s.What.Kind == "value");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => agent.Handle(["5", "watch", id, "NoSuchProperty", "on"])));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_binding_stops_with_the_setting_on_and_not_with_it_off()
    {
        using var recorder = new Recorder();
        LiveBindings.Install(null);
        LiveBindings.Hook();
        Assert.True(LiveBindings.Hooked, "Avalonia's logger isn't hooked.");
        var on = "Missing" + Guid.NewGuid().ToString("N");
        var off = "Missing" + Guid.NewGuid().ToString("N");

        LiveBreak.Configure("bindings\t1\n");
        var first = new TextBlock { Name = "Failing" };
        first.Bind(TextBlock.TextProperty, new Binding(on));
        var window = new Window { Width = 300, Height = 200, DataContext = new LiveReloadModel(), Content = new StackPanel { Children = { first } } };
        window.Show();
        Pump();
        for (var i = 0; i < 100 && !recorder.Stops.Any(s => s.What.BindingPath == on); i++)
        {
            await Task.Delay(20);
            Pump();
        }

        var stop = Assert.Single(recorder.Stops, s => s.What.BindingPath == on);
        Assert.Equal(("binding", "Text", "Failing"), (stop.What.Kind, stop.What.Property, stop.What.ElementName));
        Assert.Contains(on, stop.What.Message, StringComparison.Ordinal);

        // Off: the failure is still kept for the Error List, and nothing stops.
        LiveBreak.Configure("bindings\t0\n");
        var second = new TextBlock();
        second.Bind(TextBlock.TextProperty, new Binding(off));
        ((StackPanel)window.Content!).Children.Add(second);
        Pump();
        var agent = new LiveAgent(_ => { }, () => [window]);
        var listed = false;
        for (var i = 0; i < 100 && !listed; i++)
        {
            await Task.Delay(20);
            Pump();
            listed = (await Task.Run(() => agent.Handle(["1", "bindings"]))).Contains(off, StringComparison.Ordinal);
        }

        Assert.True(listed, "The failure with the setting off wasn't kept.");
        Assert.DoesNotContain(recorder.Stops, s => s.What.BindingPath == off);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_style_explanation_names_what_applies_and_the_selector_part_that_fails()
    {
        var text = new TextBlock { Name = "Heading", Text = "Hi", Classes = { "title" }, FontSize = 30 };
        var button = new Button { Name = "Go", Content = "Go" };
        var window = new Window
        {
            Width = 300,
            Height = 200,
            Content = new DockPanel { Children = { text, button } },
            Styles =
            {
                new Style(x => x.OfType<TextBlock>().Class("title")) { Setters = { new Setter(TextBlock.FontSizeProperty, 20d), new Setter(TextBlock.FontWeightProperty, Avalonia.Media.FontWeight.Bold) } },
                new Style(x => x.OfType<TextBlock>().Class("task")) { Setters = { new Setter(TextBlock.FontStyleProperty, Avalonia.Media.FontStyle.Italic) } },
                new Style(x => x.OfType<StackPanel>().Child().OfType<TextBlock>()) { Setters = { new Setter(TextBlock.MarginProperty, new Avalonia.Thickness(4)) } },
                new Style(x => x.OfType<TextBlock>().Name("Other")) { Setters = { new Setter(TextBlock.OpacityProperty, 0.5) } },
                new Style(x => x.OfType<Button>()) { Setters = { new Setter(Button.OpacityProperty, 0.5) } },
            },
        };
        window.Show();
        Pump();
        var agent = new LiveAgent(_ => { }, () => [window]);
        var windowId = (await Task.Run(() => agent.Handle(["1", "windows"]))).Split(LiveAgent.Field)[0];
        var id = (await Task.Run(() => agent.Handle(["2", "tree", windowId, "logical"]))).Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field)).Single(r => r[3] == "Heading")[0];
        var styles = (await Task.Run(() => agent.Handle(["3", "styles", id]))).Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field)).ToList();
        string[] Style(string selector) => Assert.Single(styles, s => s[1] == selector);

        // Applies, with the setter a local value overrides.
        var title = Style("TextBlock.title");
        Assert.Equal(("style", "applies", string.Empty), (title[0], title[2], title[3]));
        Assert.Contains("FontSize = 20 (overridden: Local)", title[4].Split('|'));
        Assert.Contains("FontWeight = Bold", title[4], StringComparison.Ordinal);
        Assert.DoesNotContain("FontWeight = Bold (overridden", title[4], StringComparison.Ordinal);

        // The class it lacks (its type matches: Avalonia has the style, inactive).
        var task = Style("TextBlock.task");
        Assert.Equal("inactive", task[2]);
        Assert.Contains("'.task': it has no class task (its classes: title)", task[3], StringComparison.Ordinal);

        // A parent of another type, and another name.
        var child = Style("StackPanel > TextBlock");
        Assert.Equal("no", child[2]);
        Assert.Contains("its parent is a DockPanel", child[3], StringComparison.Ordinal);
        var named = Style("TextBlock#Other");
        Assert.NotEqual("applies", named[2]);
        Assert.Contains("'#Other': its name is Heading", named[3], StringComparison.Ordinal);

        // A style of another type isn't about this element; a TextBlock has no control theme.
        Assert.DoesNotContain(styles, s => s[1] == "Button");
        Assert.DoesNotContain(styles, s => s[0] == "theme");

        // A button: its style, its control theme, and the theme's nested styles of a state it isn't in now.
        var buttonId = (await Task.Run(() => agent.Handle(["4", "tree", windowId, "logical"]))).Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field)).Single(r => r[3] == "Go")[0];
        var buttonStyles = (await Task.Run(() => agent.Handle(["5", "styles", buttonId]))).Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field)).ToList();
        Assert.Contains(buttonStyles, s => s[1] == "Button" && s[2] == "applies");
        Assert.Contains(buttonStyles, s => s[0] == "theme" && s[1] == "ControlTheme Button" && s[2] == "applies");
        Assert.Contains(buttonStyles, s => s[0] == "nested" && s[2] == "inactive"
            && (s[3].Contains("it has no class", StringComparison.Ordinal) || s[3].Contains(" now.", StringComparison.Ordinal)));
        window.Close();
    }

    [Theory]
    [InlineData("Button.primary:pointerover /template/ ContentPresenter#PART_ContentPresenter", 2)]
    [InlineData("StackPanel > TextBlock, Border TextBlock.a", 2)]
    [InlineData(":is(Control)[IsEnabled=False]:not(.x)", 1)]
    public void Selectors_read_into_their_parts(string selector, int firstSteps)
    {
        var alternatives = SelectorParser.Parse(selector);
        Assert.Equal(firstSteps, alternatives[0].Count);
        var last = alternatives[0][^1].Compound;
        if (selector.StartsWith("Button", StringComparison.Ordinal))
        {
            Assert.Equal(Combinator.Template, alternatives[0][1].Combinator);
            Assert.Equal(("ContentPresenter", "PART_ContentPresenter"), (last.Type, last.Name));
            Assert.Equal(["primary", ":pointerover"], alternatives[0][0].Compound.Classes);
        }
        else if (selector.StartsWith(":is", StringComparison.Ordinal))
        {
            Assert.True(last is { Type: "Control", IsOrBase: true });
            Assert.Equal([("IsEnabled", "False")], last.Properties);
            Assert.Equal([".x"], last.Nots);
        }
        else
        {
            Assert.Equal(2, alternatives.Count);
            Assert.Equal(Combinator.Child, alternatives[0][1].Combinator);
            Assert.Equal(Combinator.Descendant, alternatives[1][1].Combinator);
            Assert.Equal(["a"], alternatives[1][1].Compound.Classes);
        }
    }
}
