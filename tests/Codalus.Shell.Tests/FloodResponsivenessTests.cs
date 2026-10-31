using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Terminal;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Output;
using Codalus.Workbench.Testing;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// Floods and large inputs keep the UI answering (M56, the maintainer's requirement: the UI never hangs): output channels
/// that pass their cap, a build's stream of diagnostics, a solution's files listed for a search, a picker of tens of
/// thousands of items, a test's megabytes of output and a terminal's long scrollback. Each test counts what the UI thread
/// did (reloads, resets, slices) rather than timing it.
/// </summary>
public sealed class FloodResponsivenessTests
{
    private static async Task WaitAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until, seconds: 120);
    }

    [AvaloniaFact]
    public async Task A_flooded_output_channel_drops_its_oldest_lines_without_reloading()
    {
        // Past 4M characters each trim of the channel decoded all of it again and reloaded the editor, 3M characters for
        // each 1M appended; and showing the Output window again read the whole channel again.
        await using var f = ShellFixture.Create();
        var channel = f.Shell.Workbench.Output.GetOrCreate("test.flood", "Flood");
        var view = new OutputView { DataContext = f.Shell.Output };
        f.Shell.Output.Selected = channel;
        var host = new Window { Width = 700, Height = 300, Content = view };
        host.Show();
        await ShellFixture.PumpUntil(() => view.Coloring.Channel == channel);
        var loads = view.Coloring.Loads;

        // 12M characters from another thread, in lines of 100, as a build writes them: the window shows each batch before
        // the next (more than the editor keeps in one batch replaces its text, as all of it would go anyway).
        var line = new string('x', 99) + "\n";
        var chunk = string.Concat(Enumerable.Repeat(line, 1000));
        for (var i = 0; i < 120; i++)
        {
            await Task.Run(() => channel.Append(chunk), TestContext.Current.CancellationToken);
            ShellFixture.Pump();
        }

        await Task.Run(() =>
        {
            channel.AppendErrorLine("the last error");
            channel.AppendLine("done");
        }, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => view.Text.EndsWith("the last error\ndone\n", StringComparison.Ordinal), seconds: 60);

        Assert.Equal(loads, view.Coloring.Loads); // the editor dropped its oldest lines as more came: it never reloaded
        Assert.InRange(view.Text.Length, OutputPresenter.MaxLength - 100, OutputPresenter.MaxLength);
        Assert.Equal(line, view.Text[..100]); // from a line's start
        var error = view.Text.IndexOf("the last error", StringComparison.Ordinal);
        Assert.Contains(view.Coloring.Runs, r => r.Start == error && (r.Style.Attributes & OutputAttributes.Error) != 0);

        // Shown again unchanged, the channel isn't read again; changed meanwhile, once.
        host.Content = null;
        ShellFixture.Pump();
        host.Content = view;
        ShellFixture.Pump();
        Assert.Equal(loads, view.Coloring.Loads);
        Assert.EndsWith("done\n", view.Text, StringComparison.Ordinal);
        host.Content = null;
        ShellFixture.Pump();
        channel.AppendLine("while away");
        host.Content = view;
        await ShellFixture.PumpUntil(() => view.Text.EndsWith("while away\n", StringComparison.Ordinal));
        Assert.Equal(loads + 1, view.Coloring.Loads);
        host.Close();
    }
}
