using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Previewer.Agent;

namespace Codalus.Shell.Tests;

/// <summary>
/// The live agent (M19b, ADR 0052) in-process against a headless window: what the Live Visual Tree and Live Property
/// Explorer ask a running app. The agent waits for the UI thread, so the requests come from another thread, as from
/// its socket.
/// </summary>
public sealed class LiveAgentTests
{
    private static List<string[]> Records(string reply) =>
        reply.Length == 0 ? [] : [.. reply.Split(LiveAgent.Record).Select(r => r.Split(LiveAgent.Field))];

    [AvaloniaFact]
    public async Task The_agent_lists_windows_trees_and_properties_sets_values_highlights_and_picks()
    {
        var button = new Button { Name = "Go", Content = "Hi" };
        var panel = new StackPanel { Children = { button, new TextBlock { Text = "Label" } } };
        var window = new Window { Title = "Live", Width = 300, Height = 200, Content = panel };
        window.Show();
        Pump();

        var events = new List<string>();
        var agent = new LiveAgent(line => { lock (events) { events.Add(line); } }, () => [window]); // a headless app has no desktop lifetime
        Task<string> Ask(params string[] request) => Task.Run(() => agent.Handle(["1", .. request]));

        // Windows: this one, by its class.
        var windows = Records(await Ask("windows"));
        var row = Assert.Single(windows, w => w[1] == "Live");
        Assert.Equal(typeof(Window).FullName, row[2]);
        var windowId = row[0];

        // The logical tree has the XAML's elements; the visual tree adds the templates' parts.
        var logical = Records(await Ask("tree", windowId, "logical"));
        var go = Assert.Single(logical, n => n[2] == "Button" && n[3] == "Go");
        Assert.Contains(logical, n => n[2] == "TextBlock");
        Assert.DoesNotContain(logical, n => n[4] == "1");
        var visual = Records(await Ask("tree", windowId, "visual"));
        Assert.True(visual.Count > logical.Count, $"{visual.Count} visual nodes, {logical.Count} logical");
        Assert.Contains(visual, n => n[2] == "ContentPresenter" && n[4] == "1");

        // Properties: values and where they come from.
        var props = Records(await Ask("props", go[0]));
        var content = Assert.Single(props, p => p[0] == "Content");
        Assert.Equal(("Hi", "Local"), (content[1], content[3]));
        var margin = Assert.Single(props, p => p[0] == "Margin");
        Assert.Equal("Thickness", margin[2]);
        Assert.Contains(props, p => p[0] == "IsPointerOver" && p[4] == "1");

        // Setting values as XAML reads them.
        Assert.Equal("ok", await Ask("set", go[0], "Margin", "4,2"));
        Assert.Equal(new Thickness(4, 2), button.Margin);
        Assert.Equal("ok", await Ask("set", go[0], "Background", "Red"));
        Assert.Equal(Colors.Red, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
        Assert.Equal("ok", await Ask("set", go[0], "HorizontalAlignment", "right"));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, button.HorizontalAlignment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ask("set", go[0], "IsPointerOver", "true"));

        // The highlight is an adorner over the element, gone with "none".
        var layer = AdornerLayer.GetAdornerLayer(button)!;
        var before = layer.Children.Count;
        Assert.Equal("ok", await Ask("highlight", go[0]));
        Assert.Equal(before + 1, layer.Children.Count);
        Assert.Equal("ok", await Ask("highlight", "none"));
        Assert.Equal(before, layer.Children.Count);

        // The path the IDE resolves in the window's XAML.
        Assert.Equal(typeof(Window).FullName + "\tWindow:0:/StackPanel:0:/Button:0:Go", await Ask("path", go[0]));

        // Pick mode: the next click selects its element (the templated parent of what was hit) and doesn't click it.
        var clicked = false;
        button.Click += (_, _) => clicked = true;
        Assert.Equal("ok", await Ask("pick", "on"));
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Pump();
        string picked;
        lock (events)
        {
            picked = Assert.Single(events);
        }

        Assert.StartsWith("event\tpicked\t", picked, StringComparison.Ordinal);
        // The button, or the text its string content made (a logical element of its own).
        var pickedId = picked.Split('\t')[3];
        var tree = Records(await Ask("tree", windowId, "logical"));
        var node = Assert.Single(tree, n => n[0] == pickedId);
        Assert.True(node[0] == go[0] || node[1] == go[0], $"picked {node[2]} {node[0]} under {node[1]}, not the button {go[0]}");

        Assert.False(clicked);

        // An unknown element says so.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ask("props", "99999"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_agent_lists_controls_takes_a_picture_and_sends_input_inside_the_app()
    {
        var clicks = 0;
        var go = new Button { Name = "Go", Content = "Hi" };
        go.Click += (_, _) => clicks++;
        var off = new Button { Name = "Off", Content = "Disabled", IsEnabled = false };
        var check = new CheckBox { Name = "Check", Content = "Done" };
        var box = new TextBox { Name = "Box", Text = "ab" };
        var list = new ListBox { Name = "Items", Height = 80, ItemsSource = Enumerable.Range(1, 60).Select(i => "Item " + i).ToList() };
        var panel = new StackPanel { Children = { go, off, check, box, list } };
        var window = new Window { Title = "App", Width = 300, Height = 400, Content = panel };
        var keys = new List<string>();
        window.KeyDown += (_, e) => keys.Add(e.KeyModifiers == KeyModifiers.None ? e.Key.ToString() : e.KeyModifiers + "+" + e.Key);
        window.Show();
        Pump();

        var agent = new LiveAgent(_ => { }, () => [window]);
        Task<string> Ask(params string[] request) => Task.Run(() => agent.Handle(["1", .. request]));
        string Text(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

        // Windows carry their size; controls their text, state and bounds in the window.
        var row = Assert.Single(Records(await Ask("windows")));
        Assert.Equal(["300", "400"], row[3..5]);
        var controls = Records(await Ask("controls", row[0]));
        Assert.All(controls, c => Assert.Equal(10, c.Length));
        var goRow = Assert.Single(controls, c => c[3] == "Go");
        Assert.Equal(("Button", "Hi"), (goRow[2], goRow[4]));
        Assert.Equal("0", goRow[6]); // x: the panel's left edge
        Assert.True(double.Parse(goRow[8], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Contains("disabled", Assert.Single(controls, c => c[3] == "Off")[5].Split(' '));
        Assert.Contains("unchecked", Assert.Single(controls, c => c[3] == "Check")[5].Split(' '));
        Assert.Equal("ab", Assert.Single(controls, c => c[3] == "Box")[4]);

        // A picture of the window: a PNG of its size.
        var picture = (await Ask("picture", row[0])).Split(LiveAgent.Field);
        var png = Convert.FromBase64String(picture[0]);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal(["300", "400"], picture[1..3]);

        // A click invokes the button, through the text its content made too; a check box toggles; a disabled one refuses.
        Assert.StartsWith("Clicked Button 'Go': invoked", await Ask("input", goRow[0], "click"), StringComparison.Ordinal);
        Assert.Equal(1, clicks);
        var content = Records(await Ask("controls", goRow[0])).FirstOrDefault(c => c[2] == "TextBlock");
        if (content is not null)
        {
            Assert.Contains("around TextBlock", await Ask("input", content[0], "click"), StringComparison.Ordinal);
            Assert.Equal(2, clicks);
        }

        var checkRow = Assert.Single(controls, c => c[3] == "Check");
        Assert.Contains("toggled", await Ask("input", checkRow[0], "click"), StringComparison.Ordinal);
        Assert.True(check.IsChecked);
        Assert.Contains("checked", Assert.Single(Records(await Ask("controls", row[0])), c => c[3] == "Check")[5].Split(' '));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ask("input", Assert.Single(controls, c => c[3] == "Off")[0], "click"));

        // Text goes at the end of the box's text, or in place of it.
        var boxRow = Assert.Single(controls, c => c[3] == "Box");
        Assert.Contains("Its text is now: abcd", await Ask("input", boxRow[0], "text", Text("cd"), "append"), StringComparison.Ordinal);
        Assert.Equal("abcd", box.Text);
        await Ask("input", boxRow[0], "text", Text("new text"), "replace");
        Assert.Equal("new text", box.Text);

        // Keys are pressed on the control and bubble as the keyboard's would: the button takes Enter (a click), the
        // window gets Ctrl+A; an unknown one says so.
        var before = clicks;
        Assert.Contains("(the app handled them)", await Ask("input", goRow[0], "keys", "Ctrl+A Enter"), StringComparison.Ordinal);
        Assert.Equal(["Control+A"], keys);
        Assert.Equal(before + 1, clicks);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ask("input", goRow[0], "keys", "Hyper+Q"));

        // A scroll moves the list's scroll viewer, inside its template.
        var listRow = Assert.Single(controls, c => c[3] == "Items");
        Assert.Contains("offset is now 0,", await Ask("input", listRow[0], "scroll", "down", "4"), StringComparison.Ordinal);
        Pump();
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(viewer.Offset.Y > 0, $"offset {viewer.Offset}");
        await Ask("input", listRow[0], "scroll", "top", "0");
        Assert.Equal(0, viewer.Offset.Y);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ask("input", listRow[0], "scroll", "sideways", "1"));
        window.Close();
    }

    [Theory]
    [InlineData(typeof(double), "1.5", 1.5)]
    [InlineData(typeof(int), "7", 7)]
    [InlineData(typeof(bool), "True", true)]
    [InlineData(typeof(string), "text", "text")]
    public void Values_parse_as_xaml_reads_them(Type type, string text, object expected) => Assert.Equal(expected, LiveAgent.Parse(type, text));

    private static void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
