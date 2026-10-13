using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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

    [Theory]
    [InlineData(typeof(double), "1.5", 1.5)]
    [InlineData(typeof(int), "7", 7)]
    [InlineData(typeof(bool), "True", true)]
    [InlineData(typeof(string), "text", "text")]
    public void Values_parse_as_xaml_reads_them(Type type, string text, object expected) => Assert.Equal(expected, LiveAgent.Parse(type, text));

    private static void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
