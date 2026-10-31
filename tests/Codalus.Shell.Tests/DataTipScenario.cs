using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Codalus.Debugging;
using Codalus.Shell.Editor;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.Tests;

/// <summary>
/// A program paused in a file open in the editor, for the data tip and variable window tests (D44): the scripted adapter
/// (<c>Codalus.LspConformance.Server --dap</c>) answers from <c>inspect</c>'s stops, as netcoredbg does. The pointer
/// moves as a person's would: onto the text, then onto the tips, in whichever window each one is (its own, or the
/// main window's overlay).
/// </summary>
internal sealed class DataTipScenario : IAsyncDisposable
{
    private DataTipScenario(ShellFixture fixture, string path, CodeEditorController editor)
    {
        F = fixture;
        Path = path;
        Editor = editor;
    }

    public ShellFixture F { get; }

    public string Path { get; }

    public CodeEditorController Editor { get; }

    public TextView View => Editor.Editor.TextArea.TextView;

    public static string ScriptedServer => System.IO.Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.LspConformance.Server", "bin",
        typeof(DataTipScenario).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusConfiguration").Value!, Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    /// <summary>
    /// Opens <paramref name="code"/> (as <c>Program.cs</c>) and starts the program, paused at <paramref name="line"/> with
    /// <paramref name="stops"/> (the inspection script's <c>stops</c>).
    /// </summary>
    /// <param name="requestLog">A file the scripted adapter writes each request to, a line each: its command and arguments.</param>
    /// <param name="processId">The process id the scripted adapter reports as the program's.</param>
    /// <param name="options">More of the script's <c>inspect</c>: <c>threads</c>, <c>stackTraceDelay</c>, <c>ignoresPaging</c>.</param>
    public static async Task<DataTipScenario> StartAsync(string code, int line, object[] stops, Action<ShellFixture>? before = null, string? requestLog = null, int? processId = null, IReadOnlyDictionary<string, object>? options = null)
    {
        var f = ShellFixture.Create();
        try
        {
            var folder = System.IO.Path.Combine(f.UserData, "Paused");
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, "Program.cs");
            await File.WriteAllTextAsync(path, code, TestContext.Current.CancellationToken);
            f.Window.Width = 1800; // room for a tip's 400 px values beside the tool windows

            // The C# service's navigation bar (shown above the text once the file's symbols come) and CodeLens rows (over
            // the class and its members) come at no set time and push the text down, from under a resting pointer: the
            // tests go without them (one moves the text under an open tip on purpose).
            await f.Shell.Workbench.Settings.SetAsync(EditorOptions.NavigationBarKey, false, Codalus.Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
            await f.Shell.Workbench.Settings.SetAsync(EditorOptions.CodeLensKey, false, Codalus.Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
            before?.Invoke(f);
            await f.Shell.OpenFileAsync(path);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
            var inspect = new Dictionary<string, object> { ["path"] = path, ["line"] = line, ["stops"] = stops };
            if (processId is not null)
            {
                inspect["processId"] = processId;
            }

            foreach (var (key, value) in options ?? new Dictionary<string, object>())
            {
                inspect[key] = value;
            }

            var configuration = new Dictionary<string, object> { ["inspect"] = inspect };
            if (requestLog is not null)
            {
                configuration["requestLog"] = requestLog;
            }

            var launch = JsonSerializer.SerializeToElement(configuration);
            await f.Shell.Debugger.LaunchAdapterAsync("Scripted", new DebugAdapterStartInfo("dotnet", [ScriptedServer, "--dap"], AppContext.BaseDirectory), launch, ShellFixture.RequestTimeout(60));
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.ActiveEditor?.Controller?.CurrentDebugLine() == line, seconds: 60,
                describe: () => $"debugger {f.Shell.Debugger.State}, line {f.Shell.ActiveEditor?.Controller?.CurrentDebugLine()}");
            var scenario = new DataTipScenario(f, path, f.Shell.ActiveEditor!.Controller!);
            await scenario.SettleAsync();
            return scenario;
        }
        catch
        {
            await f.DisposeAsync();
            throw;
        }
    }

    /// <summary>A variable as netcoredbg sends it (<paramref name="named"/>: how many children it counts, an array's elements).</summary>
    public static Dictionary<string, object?> Var(string name, string value, string? type = null, int reference = 0, string? evaluateName = null, int? named = null)
    {
        var variable = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["value"] = value,
            ["type"] = type,
            ["variablesReference"] = reference,
            ["evaluateName"] = evaluateName,
        };
        if (named is not null)
        {
            variable["namedVariables"] = named;
        }

        return variable;
    }

    /// <summary>An evaluation's answer.</summary>
    public static Dictionary<string, object?> Result(string result, string? type, int reference = 0, int? named = null)
    {
        var answer = new Dictionary<string, object?>
        {
            ["result"] = result,
            ["type"] = type,
            ["variablesReference"] = reference,
        };
        if (named is not null)
        {
            answer["namedVariables"] = named;
        }

        return answer;
    }

    /// <summary>An array's elements, too many to list: the scripted adapter makes them up as it's asked for them.</summary>
    public static Dictionary<string, object?> Generated(int count, string evaluateName) => new()
    {
        ["generate"] = count,
        ["name"] = "[{0}]",
        ["value"] = "{0}",
        ["type"] = "int",
        ["evaluateName"] = evaluateName + "[{0}]",
    };

    /// <summary>
    /// The requests in a request log (<see cref="StartAsync"/>'s): each command and its arguments. The adapter goes on
    /// writing it: it's read as shared, and a line it hasn't finished is left for the next read.
    /// </summary>
    public static IReadOnlyList<(string Command, JsonElement Arguments)> Requests(string log)
    {
        if (!File.Exists(log))
        {
            return [];
        }

        string text;
        using (var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            text = reader.ReadToEnd();
        }

        return [.. text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => (l[..l.IndexOf(' ', StringComparison.Ordinal)], JsonElement.Parse(l[(l.IndexOf(' ', StringComparison.Ordinal) + 1)..])))];
    }

    /// <summary>Waits until the text stays put (the tool windows can still be arriving).</summary>
    public async Task SettleAsync()
    {
        var placed = View.TranslatePoint(default, F.Window);
        var still = System.Diagnostics.Stopwatch.StartNew();
        await ShellFixture.PumpUntil(() =>
        {
            var now = View.TranslatePoint(default, F.Window);
            if (now != placed)
            {
                placed = now;
                still.Restart();
            }

            return still.Elapsed >= TimeSpan.FromSeconds(1);
        });
    }

    /// <summary>The offset of the first <paramref name="text"/> on a 1-based line, plus <paramref name="delta"/>.</summary>
    public int OffsetOf(int line, string text, int delta = 0)
    {
        var document = Editor.Editor.Document;
        var l = document.GetLineByNumber(line);
        var at = document.GetText(l).IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' isn't on line {line}");
        return l.Offset + at + delta;
    }

    /// <summary>A point of the text view at an offset's character, in the middle of its line.</summary>
    public Point InView(int offset)
    {
        View.EnsureVisualLines();
        var position = new TextViewPosition(Editor.Editor.Document.GetLocation(offset));
        var top = View.GetVisualPosition(position, VisualYPosition.LineTop) - View.ScrollOffset;
        var bottom = View.GetVisualPosition(position, VisualYPosition.LineBottom) - View.ScrollOffset;
        return new Point(top.X + 3, (top.Y + bottom.Y) / 2);
    }

    /// <summary>The same point in the window.</summary>
    public Point At(int offset) => View.TranslatePoint(InView(offset), F.Window)!.Value;

    /// <summary>Rests the pointer on the expression at <paramref name="offset"/> until its data tip opens.</summary>
    public async Task<DataTipCascade> HoverAsync(int offset)
    {
        F.Window.MouseMove(new Point(2, 2));
        ShellFixture.Pump();
        ShellFixture.RenderFrame(F.Window);
        F.Window.MouseMove(At(offset));
        await ShellFixture.PumpUntil(() => Editor.DataTip is { Header.Bounds.Width: > 0 }, describe: () => $"the data tip: {Editor.QuickInfoTrace}");
        ShellFixture.RenderFrame(F.Window);
        return Editor.DataTip!;
    }

    /// <summary>The window a control is in: a popup's own, or the main one (its overlay).</summary>
    public static TopLevel HostOf(Visual control) => TopLevel.GetTopLevel(control)!;

    /// <summary>A point of <paramref name="control"/> in its window's coordinates.</summary>
    public static Point InHost(Visual control, Point point) => control.TranslatePoint(point, HostOf(control))!.Value;

    /// <summary>A control's bounds in the text view's coordinates.</summary>
    public Rect InTextView(Visual control) => new(View.PointToClient(control.PointToScreen(default)), control.Bounds.Size);

    public void MoveOnto(Visual control, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var host = HostOf(control);
        ShellFixture.RenderFrame(host);
        host.MouseMove(InHost(control, point ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)), modifiers);
        ShellFixture.Pump();
    }

    public void Click(Visual control, Point? point = null, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var host = HostOf(control);
        CloseToolTips(host);
        ShellFixture.RenderFrame(host);
        var at = InHost(control, point ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        host.MouseMove(at, modifiers);
        host.MouseDown(at, MouseButton.Left, modifiers);
        host.MouseUp(at, MouseButton.Left, modifiers);
        ShellFixture.Pump();
    }

    /// <summary>Closes the tooltips a slow machine has had time to open: a click aims at what's under them.</summary>
    private static void CloseToolTips(TopLevel host)
    {
        foreach (var control in host.GetVisualDescendants().OfType<Control>().Where(ToolTip.GetIsOpen).ToList())
        {
            ToolTip.SetIsOpen(control, false);
        }
    }

    /// <summary>Every text shown under a control (its rows' names and values…).</summary>
    public static IReadOnlyList<string> Texts(Visual control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (F.Shell.Debugger.State != DebugState.Idle)
            {
                await F.Shell.Debugger.StopAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DapException or DapConnectionLostException or OperationCanceledException)
        {
            // Ended already.
        }

        await F.DisposeAsync();
    }
}
