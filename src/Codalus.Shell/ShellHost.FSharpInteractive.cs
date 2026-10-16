using System.Text;
using Codalus.Workbench.Setup;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell;

/// <summary>
/// F# Interactive (M36b, ADR 0068): <c>dotnet fsi</c> in a terminal of the integrated terminal, which shows its
/// answers (<c>val x: int = 3</c>) in its own colors. Send to F# Interactive writes the selection, or the line, with a
/// line directive so errors name the file and line, and ends it with <c>;;</c>; Send File loads a script with
/// <c>#load</c>; Send References adds a built project and its references with <c>#r</c>; Reset starts it again.
/// </summary>
public sealed partial class ShellHost
{
    public const string FSharpInteractiveName = "F# Interactive";

    private TerminalSession? _fsi;

    /// <summary>The running F# Interactive session, or null (none started, or it has ended).</summary>
    public TerminalSession? FSharpInteractive => _fsi is { ExitCode: null } session && Terminals.Sessions.Contains(session) ? session : null;

    /// <summary>Shows F# Interactive, starting it if it isn't running.</summary>
    public TerminalSession ShowFSharpInteractive()
    {
        if (FSharpInteractive is not { } session)
        {
            var directory = Solutions.Solution?.Directory
                ?? (ActiveEditor?.Model.Path is { } file && Path.GetDirectoryName(file) is { Length: > 0 } folder ? folder : null)
                ?? Environment.CurrentDirectory;
            var extra = Workbench.Settings.GetString("fsharp.fsiExtraParameters", string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Without fsi's own line editor: it queries and moves the cursor, which programmatic input doesn't need.
            session = Terminals.Run(FSharpInteractiveName, EnvironmentCheck.FindDotnet() ?? "dotnet", ["fsi", "--readline-", .. extra], directory, null);
            _fsi = session;
        }

        Layout.ShowToolWindow(ViewModels.TerminalViewModel.ToolWindowId, focus: false);
        TerminalWindow.Show(session);
        return session;
    }

    /// <summary>Reset F# Interactive: ends the session and starts a new one (everything defined is gone).</summary>
    public TerminalSession ResetFSharpInteractive()
    {
        if (FSharpInteractive is { } session)
        {
            Terminals.Close(session);
        }

        _fsi = null;
        return ShowFSharpInteractive();
    }

    /// <summary>
    /// Send to F# Interactive: the selection, or the current line (<paramref name="lineOnly"/>: always the line), from
    /// an F# editor.
    /// </summary>
    public void SendToFSharpInteractive(bool lineOnly = false)
    {
        if (ActiveEditor?.Controller?.Editor is not { } editor)
        {
            return;
        }

        var selection = !lineOnly && editor.SelectionLength > 0;
        var line = editor.Document.GetLineByOffset(selection ? editor.SelectionStart : editor.CaretOffset);
        var text = selection ? editor.SelectedText : editor.Document.GetText(line);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Submit(FSharpSubmission(text, ActiveEditor.Model.Path, line.LineNumber));
    }

    /// <summary>Send File to F# Interactive: a script with <c>#load</c> (its own <c>#r</c> and <c>#load</c> resolve), other files' whole text.</summary>
    public void SendFileToFSharpInteractive()
    {
        if (ActiveEditor is not { Model: { } model })
        {
            return;
        }

        var isScript = model.Path is { } path && Path.GetExtension(path).ToLowerInvariant() is ".fsx" or ".fsscript";
        if (isScript && !model.IsModified && File.Exists(model.Path))
        {
            Submit($"#load {Verbatim(model.Path!)}\n;;\n");
        }
        else
        {
            Submit(FSharpSubmission(model.Text.Text, model.Path, 1));
        }
    }

    /// <summary>Run Script: <c>dotnet fsi</c> runs the saved script in a terminal of its own.</summary>
    public Workbench.Terminal.TerminalSession? RunFSharpScript()
    {
        if (ActiveEditor?.Model is not { Path: { } path } model || Path.GetExtension(path).ToLowerInvariant() is not (".fsx" or ".fsscript"))
        {
            StatusBar.ShowMessage("Open an F# script (.fsx) to run it.", TimeSpan.FromSeconds(4));
            return null;
        }

        if (model.IsModified)
        {
            StatusBar.ShowMessage("Save the script first: dotnet fsi runs the file on disk.", TimeSpan.FromSeconds(5));
            return null;
        }

        var session = Terminals.Run("fsi: " + Path.GetFileName(path), EnvironmentCheck.FindDotnet() ?? "dotnet", ["fsi", path], Path.GetDirectoryName(path)!, null);
        Layout.ShowToolWindow(ViewModels.TerminalViewModel.ToolWindowId, focus: false);
        TerminalWindow.Show(session);
        return session;
    }

    /// <summary>
    /// Send References to F# Interactive: builds the project, then adds its output and its references with <c>#r</c>
    /// (not the framework's reference assemblies: F# Interactive has the runtime's own).
    /// </summary>
    public async Task SendReferencesToFSharpInteractiveAsync(string? argument = null)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage("Select a project to send to F# Interactive.", TimeSpan.FromSeconds(4));
            return;
        }

        if (!await BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, project).ConfigureAwait(true))
        {
            StatusBar.ShowMessage($"{Path.GetFileNameWithoutExtension(project)} didn't build; nothing was sent to F# Interactive.", TimeSpan.FromSeconds(5));
            return;
        }

        if (!Solutions.CompilerInputs.TryGetValue(project, out var inputs) || inputs.OutputPath is not { } output)
        {
            StatusBar.ShowMessage($"{Path.GetFileNameWithoutExtension(project)} has no compiler information yet (its design-time build hasn't finished).", TimeSpan.FromSeconds(5));
            return;
        }

        Submit(FSharpReferences(inputs.Arguments, output));
    }

    /// <summary>The <c>#r</c> lines for a project's output and its compile references (from <c>-r:</c>/<c>/reference:</c>), without reference packs.</summary>
    public static string FSharpReferences(IEnumerable<string> arguments, string output)
    {
        var references = arguments
            .Select(a => a.StartsWith("-r:", StringComparison.Ordinal) ? a[3..] : a.StartsWith("/reference:", StringComparison.OrdinalIgnoreCase) ? a["/reference:".Length..] : null)
            .OfType<string>()
            .Select(r => r.Trim('"'))
            .Where(r => !IsReferencePack(r))
            .Append(output)
            .Distinct(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var reference in references)
        {
            text.Append("#r ").Append(Verbatim(reference)).Append('\n');
        }

        return text.Append(";;\n").ToString();

        static bool IsReferencePack(string path)
        {
            var normalized = path.Replace('\\', '/');
            return normalized.Contains("/packs/", StringComparison.OrdinalIgnoreCase) && normalized.Contains(".Ref/", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Code for F# Interactive: a line directive (errors then name the file and line), the code, and <c>;;</c>.</summary>
    public static string FSharpSubmission(string code, string? path, int line)
    {
        var text = new StringBuilder();
        if (path is not null)
        {
            text.Append("# ").Append(line).Append(' ').Append(Verbatim(path)).Append('\n');
        }

        text.Append(code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n', ' ', '\t'));
        return text.Append("\n;;\n").ToString();
    }

    private static string Verbatim(string path) => "@\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private void Submit(string text)
    {
        // Lines end with Enter, a carriage return, as Run Selection in the terminal sends them: ConPTY reads a line feed
        // as Ctrl+J, which doesn't end the line.
        var session = ShowFSharpInteractive();
        session.Write(text.Replace('\n', '\r'));
    }
}
