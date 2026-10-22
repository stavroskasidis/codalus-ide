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
    public static async Task<DataTipScenario> StartAsync(string code, int line, object[] stops, Action<ShellFixture>? before = null)
    {
        var f = ShellFixture.Create();
        try
        {
            var folder = System.IO.Path.Combine(f.UserData, "Paused");
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, "Program.cs");
            await File.WriteAllTextAsync(path, code, TestContext.Current.CancellationToken);
            f.Window.Width = 1800; // room for a tip's 400 px values beside the tool windows
            before?.Invoke(f);
            await f.Shell.OpenFileAsync(path);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
            var launch = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["inspect"] = new Dictionary<string, object> { ["path"] = path, ["line"] = line, ["stops"] = stops },
            });
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

    /// <summary>A variable as netcoredbg sends it.</summary>
    public static Dictionary<string, object?> Var(string name, string value, string? type = null, int reference = 0, string? evaluateName = null) => new()
    {
        ["name"] = name,
        ["value"] = value,
        ["type"] = type,
        ["variablesReference"] = reference,
        ["evaluateName"] = evaluateName,
    };

    /// <summary>An evaluation's answer.</summary>
    public static Dictionary<string, object?> Result(string result, string? type, int reference = 0) => new()
    {
        ["result"] = result,
        ["type"] = type,
        ["variablesReference"] = reference,
    };

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
        ShellFixture.RenderFrame(host);
        var at = InHost(control, point ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        host.MouseMove(at, modifiers);
        host.MouseDown(at, MouseButton.Left, modifiers);
        host.MouseUp(at, MouseButton.Left, modifiers);
        ShellFixture.Pump();
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
