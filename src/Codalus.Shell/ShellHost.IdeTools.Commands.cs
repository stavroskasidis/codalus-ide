using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell;

/// <summary>
/// M60's tools for agents: the shell's side of <see cref="IdeShellTools"/>. Commands run as their menu items do, under
/// their own enablement, never the ones agents don't get (<see cref="IdeShellTools.DeniedReason"/>, and the commands
/// the command sweep marks as leaving the machine), with any dialog or picker they open cancelled; settings are read
/// with secrets masked; a picture of the main window (or a tool window or document in it) is refused while a file
/// outside the workspace or excluded from AI shows; and text is typed into the integrated terminal the user sees.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>How long run_command waits for a command before it says it is still running.</summary>
    private static readonly TimeSpan s_agentCommandWait = TimeSpan.FromSeconds(60);

    /// <summary>The window that answers Cancel to file pickers while agents' commands run, and how many run.</summary>
    private AgentCommandWindow? _agentCommandWindow;
    private int _agentCommandRuns;

    /// <summary>The most commands list_commands shows with their titles, key bindings and enablement.</summary>
    private const int MaxDetailedCommands = 150;

    private async Task<IdeToolResult> ShellToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken) => tool switch
    {
        "list_commands" => new(CommandsText(Str(args, "filter"))),
        "run_command" => await RunCommandToolAsync(args, cancellationToken).ConfigureAwait(true),
        "get_settings" => new(SettingsText(args)),
        "ide_screenshot" => await IdeScreenshotToolAsync(Str(args, "window"), root, cancellationToken).ConfigureAwait(true),
        "terminal_send" => await TerminalSendToolAsync(args, cancellationToken).ConfigureAwait(true),
        _ => throw new Codalus.Workbench.Ai.Mcp.McpException($"There is no tool '{tool}'."),
    };

    /// <summary>Why agents never run <paramref name="id"/>: denied (D52), or a command the sweep skips for leaving the machine.</summary>
    internal static string? AgentCommandRefusal(string id) =>
        IdeShellTools.DeniedReason(id) ?? (SmokeTest.SkipReason(id) is { } reason ? "it " + reason : null);

    // ------------------------------------------------------------------ commands

    private string CommandsText(string? filter)
    {
        var commands = Workbench.Commands;
        var all = commands.Descriptors.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        var denied = all.Count(d => AgentCommandRefusal(d.Id) is not null);
        var listed = all
            .Where(d => AgentCommandRefusal(d.Id) is null)
            .Where(d => filter is not { Length: > 0 } || d.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || d.DisplayTitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (listed.Count == 0)
        {
            return filter is { Length: > 0 } ? $"No command agents can run matches '{filter}'." : "No commands.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{listed.Count} command(s){(filter is { Length: > 0 } ? $" matching '{filter}'" : "")}; run one with run_command. Agents don't get {denied} others (exit, settings sync, plugins, sign-ins, updates, trust, AI settings, external pages).\n");
        if (filter is not { Length: > 0 } || listed.Count > MaxDetailedCommands)
        {
            // Hundreds of commands with their titles would pass the answer's size: their ids by area, those disabled
            // now marked, and a filter shows the details.
            text.Append(filter is { Length: > 0 } ? "Too many to show in full: " : "").Append("their ids by area ((off): disabled now). Give a filter (part of an id or title) for titles, key bindings and when each runs.\n");
            foreach (var area in listed.GroupBy(d => d.Id.Split('.')[0], StringComparer.Ordinal))
            {
                text.Append(area.Key).Append(": ").AppendJoin(", ", area.Select(d => d.Id + (commands.IsEnabled(d.Id) ? "" : " (off)"))).Append('\n');
            }

            return text.ToString().TrimEnd();
        }

        foreach (var command in listed)
        {
            text.Append(command.Id).Append(" — ").Append(command.DisplayTitle);
            if (Workbench.Keybindings.GetBinding(command.Id)?.ToLabel(Workbench.Platform) is { Length: > 0 } key)
            {
                text.Append(" (").Append(key).Append(')');
            }

            if (!commands.IsEnabled(command.Id))
            {
                text.Append(" [disabled now: ").Append(command.Enablement.Text).Append(']');
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Runs a command as its menu item does, under its enablement; cancels the dialogs and pickers it opens (an agent
    /// can't answer them) and reports what it showed: its result, those dialogs, and its notifications.
    /// </summary>
    private async Task<IdeToolResult> RunCommandToolAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var id = Str(args, "id") is { Length: > 0 } given ? given : throw new IdeToolException("A command id is needed: list_commands lists them.");
        if (AgentCommandRefusal(id) is { } why)
        {
            return IdeToolResult.Error($"'{id}' isn't run for agents: {why}. Ask the user to run it if it is needed.");
        }

        var commands = Workbench.Commands;
        var descriptor = commands.Find(id);
        var title = descriptor?.DisplayTitle ?? id;
        if (descriptor is null && !commands.IsEnabled(id))
        {
            return IdeToolResult.Error($"There is no command '{id}'. list_commands lists them.");
        }

        if (!commands.IsEnabled(id))
        {
            return IdeToolResult.Error($"'{title}' ({id}) is disabled right now: it runs only when '{descriptor!.Enablement.Text}'.");
        }

        List<JsonElement> arguments = [];
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var given2))
        {
            if (given2.ValueKind == JsonValueKind.Array)
            {
                arguments.AddRange(given2.EnumerateArray().Select(a => a.Clone()));
            }
            else if (given2.ValueKind != JsonValueKind.Null)
            {
                arguments.Add(given2.Clone());
            }
        }

        var shown = Workbench.Notifications.Items.ToHashSet();
        var dialogBefore = Dialogs.Request;
        var paletteBefore = Palette.IsOpen;
        var feedbackBefore = Feedback.IsOpen;
        var cancelled = new List<string>();
        Task<JsonElement?> running;
        BeginAgentCommand();
        try
        {
            running = commands.ExecuteAsync(id, arguments, cancellationToken);
            var deadline = DateTime.UtcNow + s_agentCommandWait;
            while (true)
            {
                CancelAgentCommandOverlays(dialogBefore, paletteBefore, feedbackBefore, cancelled);
                if (running.IsCompleted || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.WhenAny(running, Task.Delay(100, cancellationToken)).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            cancelled.AddRange(EndAgentCommand());
        }

        var text = new StringBuilder();
        var failed = false;
        if (!running.IsCompleted)
        {
            text.Append(CultureInfo.InvariantCulture, $"'{title}' ({id}) is still running after {s_agentCommandWait.TotalSeconds:0} s.");
        }
        else if (running.IsCanceled)
        {
            text.Append(CultureInfo.InvariantCulture, $"'{title}' ({id}) was cancelled.");
        }
        else if (running.Exception?.GetBaseException() is { } error)
        {
            failed = true;
            text.Append(error is CommandNotEnabledException
                ? $"'{title}' ({id}) is disabled right now."
                : $"'{title}' ({id}) failed: {error.Message}");
            Workbench.Log.AppendLine($"[command] {id} (an AI agent) failed: {error}");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture, $"Ran '{title}' ({id}).");
            if (running.Result is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } result)
            {
                var json = result.GetRawText();
                text.Append(" It returned: ").Append(json.Length > 4000 ? json[..4000] + "…" : json);
            }
        }

        foreach (var what in cancelled)
        {
            text.Append("\nIt opened ").Append(what).Append(", which an agent can't answer: it was cancelled. Pass what it asks for as the command's args, or ask the user.");
        }

        foreach (var notification in Workbench.Notifications.Items.Where(n => !shown.Contains(n)))
        {
            text.Append(CultureInfo.InvariantCulture, $"\nNotification ({notification.Severity.ToString().ToLowerInvariant()}): {notification.Message}");
        }

        if (StatusBar.Message is { Length: > 0 } status)
        {
            text.Append("\nStatus bar: ").Append(status);
        }

        return new IdeToolResult(text.ToString(), failed);
    }

    /// <summary>Cancels what a command opened that nobody would answer: a dialog, a picker, the feedback form.</summary>
    private void CancelAgentCommandOverlays(DialogRequest? dialogBefore, bool paletteBefore, bool feedbackBefore, List<string> cancelled)
    {
        if (Dialogs.Request is { } request && !ReferenceEquals(request, dialogBefore))
        {
            cancelled.Add($"a dialog ('{request.Title}': {request.Message})");
            Dialogs.Cancel();
        }

        if (!paletteBefore && Palette.IsOpen)
        {
            cancelled.Add($"a picker ({Palette.Placeholder})");
            Palette.Cancel();
        }

        if (!feedbackBefore && Feedback.IsOpen)
        {
            cancelled.Add($"a dialog ('{Feedback.Title}')");
            Feedback.Cancel();
        }
    }

    /// <summary>While agents' commands run, the window answers Cancel to file and folder pickers.</summary>
    private void BeginAgentCommand()
    {
        if (_agentCommandRuns++ == 0 && Window is { } window and not AgentCommandWindow)
        {
            Window = _agentCommandWindow = new AgentCommandWindow(window);
        }
    }

    /// <summary>The pickers refused since the last call, and the window as it was once no agent's command runs.</summary>
    private List<string> EndAgentCommand()
    {
        var refused = _agentCommandWindow?.TakeRefused() ?? [];
        if (--_agentCommandRuns == 0 && _agentCommandWindow is { } ours)
        {
            if (ReferenceEquals(Window, ours))
            {
                Window = ours.Inner;
            }

            _agentCommandWindow = null;
        }

        return refused;
    }

    /// <summary>The main window, but file and folder pickers answer Cancel: an agent's command can't make the user pick.</summary>
    private sealed class AgentCommandWindow(IShellWindow inner) : IShellWindow
    {
        private readonly List<string> _refused = [];

        public IShellWindow Inner => inner;

        public List<string> TakeRefused()
        {
            lock (_refused)
            {
                var taken = _refused.ToList();
                _refused.Clear();
                return taken;
            }
        }

        private Task<T> Refuse<T>(string what, T cancelled)
        {
            lock (_refused)
            {
                _refused.Add(what);
            }

            return Task.FromResult(cancelled);
        }

        public Avalonia.Input.Platform.IClipboard? Clipboard => inner.Clipboard;

        public bool IsFullScreen
        {
            get => inner.IsFullScreen;
            set => inner.IsFullScreen = value;
        }

        public void FocusPane(bool backwards) => inner.FocusPane(backwards);

        public void RequestClose() => _ = Refuse("closing the window", true);

        public void BringToFront() => inner.BringToFront();

        public Task<string?> PickSolutionAsync() => Refuse<string?>("a file picker (a solution)", null);

        public Task<string?> PickProjectAsync() => Refuse<string?>("a file picker (a project)", null);

        public Task<string?> PickFileAsync(string title, string filterName, string[] patterns) => Refuse<string?>($"a file picker ({title})", null);

        public Task<string?> SaveFileAsync(string title, string suggestedName, string filterName, string[] patterns) => Refuse<string?>($"a save picker ({title})", null);

        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Refuse<IReadOnlyList<string>>($"a file picker ({title})", []);

        public Task<string?> PickFolderAsync(string title) => Refuse<string?>($"a folder picker ({title})", null);
    }

    // ------------------------------------------------------------------ settings

    private string SettingsText(JsonElement args)
    {
        var settings = Workbench.Settings;
        var effective = settings.Snapshot();
        List<string> keys;
        var named = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("keys", out var given) && given.ValueKind == JsonValueKind.Array
            ? [.. given.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!).Where(k => k.Length > 0)]
            : new List<string>();
        if (named.Count == 0)
        {
            keys = [.. effective.Keys.Where(settings.IsModified).Order(StringComparer.Ordinal)];
            if (keys.Count == 0)
            {
                return "Every setting has its default value.";
            }
        }
        else
        {
            var known = effective.Keys.Concat(settings.Descriptors.Select(d => d.Key)).Distinct(StringComparer.Ordinal).ToList();
            keys = [.. named.SelectMany(k => k.EndsWith('.') ? known.Where(c => c.StartsWith(k, StringComparison.Ordinal)).Order(StringComparer.Ordinal) : (IEnumerable<string>)[k]).Distinct(StringComparer.Ordinal)];
        }

        var text = new StringBuilder(named.Count == 0 ? "The settings set to something other than their default:\n" : "");
        foreach (var key in keys)
        {
            var value = settings.GetValue(key);
            var descriptor = settings.Find(key);
            if (value.ValueKind == JsonValueKind.Undefined && descriptor is null)
            {
                text.Append(key).Append(": no such setting\n");
                continue;
            }

            var where = settings.GetLayerValue(key, SettingScope.Workspace) is not null ? "the workspace's settings"
                : settings.GetLayerValue(key, SettingScope.User) is not null ? "the user's settings"
                : "the default";
            text.Append(key).Append(" = ").Append(MaskedSettingJson(key, value)).Append(" (from ").Append(where);
            if (descriptor is not null && where != "the default")
            {
                text.Append("; default ").Append(MaskedSettingJson(key, descriptor.Default));
            }

            text.Append(')');
            if (descriptor?.Description is { Length: > 0 } description)
            {
                text.Append(" — ").Append(description.Length > 200 ? description[..200] + "…" : description);
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>A setting's value as JSON, with every value whose name says it is a secret (a token, a key, a password) masked.</summary>
    internal static string MaskedSettingJson(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return "(unset)";
        }

        if (IsSecretName(key))
        {
            return "\"(hidden)\"";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        if (IsSecretName(property.Name) && property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
                        {
                            writer.WriteStringValue("(hidden)");
                        }
                        else
                        {
                            Write(writer, property.Value);
                        }
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(writer, item);
                    }

                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }

    /// <summary>Whether a setting's or a member's name says its value is a secret.</summary>
    private static bool IsSecretName(string name)
    {
        var plain = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var last = plain[(plain.LastIndexOf('.') + 1)..];
        return last.Contains("token", StringComparison.Ordinal) && !last.EndsWith("scopes", StringComparison.Ordinal)
            || last.Contains("password", StringComparison.Ordinal)
            || last.Contains("secret", StringComparison.Ordinal)
            || last.Contains("apikey", StringComparison.Ordinal)
            || last.Contains("credential", StringComparison.Ordinal)
            || last is "key" or "pat" or "authorization" or "auth";
    }

    // ------------------------------------------------------------------ a picture of the IDE

    private async Task<IdeToolResult> IdeScreenshotToolAsync(string? name, string? root, CancellationToken cancellationToken)
    {
        if (MainTopLevel() is not { } top)
        {
            return IdeToolResult.Error("Codalus IDE has no window to take a picture of.");
        }

        // A picture shows whatever the editors show: never a file excluded from AI, or one outside the workspace.
        foreach (var document in Layout.Documents.Where(d => d.Owner is Dock.Model.Core.IDock { ActiveDockable: var active } && ReferenceEquals(active, d)))
        {
            if (document.FilePath is { Length: > 0 } path && Path.IsPathFullyQualified(path)
                && (!IsInWorkspace(path, root) || await IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true)))
            {
                return IdeToolResult.Error($"A document showing in the IDE ({Path.GetFileName(path)}) is outside the workspace or excluded from AI, so no picture is taken.");
            }
        }

        Visual target = top;
        var what = "Codalus IDE's main window";
        if (name is { Length: > 0 })
        {
            object? dockable = Layout.OpenToolWindows.FirstOrDefault(t => t.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || t.Title.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? (object?)Layout.Documents.FirstOrDefault(d => d.Title.Equals(name, StringComparison.OrdinalIgnoreCase) || (d.FilePath is { } file && Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase)));
            if (dockable is null)
            {
                return IdeToolResult.Error($"No tool window or document named '{name}' is open. Open tool windows: {string.Join(", ", Layout.OpenToolWindows.Select(t => $"{t.Title} ({t.Id})"))}.");
            }

            var content = dockable is Docking.ToolWindowDockable tool ? tool.Content : dockable;
            if (top.GetVisualDescendants().OfType<Control>()
                    .Where(c => (ReferenceEquals(c.DataContext, content) || ReferenceEquals(c.DataContext, dockable)) && c.IsEffectivelyVisible && c.Bounds.Width >= 1 && c.Bounds.Height >= 1)
                    .MaxBy(c => c.Bounds.Width * c.Bounds.Height) is not { } shown)
            {
                return IdeToolResult.Error($"'{name}' is open but not showing (another tab is in front of it, or its panel is hidden).");
            }

            target = shown;
            what = $"'{name}' in Codalus IDE";
        }

        var png = RenderPng(target, top.RenderScaling);
        return new IdeToolResult($"A picture of {what}.") { Images = [new IdeToolImage(Convert.ToBase64String(png), "image/png")] };
    }

    /// <summary>The main window as a top level (its own, during the command sweep and an agent's command too).</summary>
    private TopLevel? MainTopLevel()
    {
        var window = Window;
        while (window is AgentCommandWindow wrapped)
        {
            window = wrapped.Inner;
        }

        return window as TopLevel
            ?? (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main } ? main : null);
    }

    private static byte[] RenderPng(Visual visual, double scaling)
    {
        // The visual root includes the window decorations Avalonia draws, as the smoke test's pictures do.
        if (visual is TopLevel)
        {
            while (visual.GetVisualParent() is { } parent)
            {
                visual = parent;
            }
        }

        var size = new PixelSize(Math.Max(1, (int)(visual.Bounds.Width * scaling)), Math.Max(1, (int)(visual.Bounds.Height * scaling)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(visual);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    // ------------------------------------------------------------------ the integrated terminal

    private async Task<IdeToolResult> TerminalSendToolAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var text = Str(args, "text") ?? throw new IdeToolException("The text to type is needed.");
        RequireTrustedForAgentActions();
        var wait = TimeSpan.FromSeconds(Math.Clamp(Int(args, "waitSeconds") ?? 3, 1, 60));
        Layout.ShowToolWindow(TerminalViewModel.ToolWindowId, focus: false);
        TerminalSession? session = null;
        var fresh = Bool(args, "newTerminal") == true || TerminalWindow.Selected is not { Exited: false };
        if (!fresh)
        {
            session = TerminalWindow.Selected!.Primary;
        }
        else if (TerminalWindow.NewTerminal() is { } started)
        {
            session = started;
            await started.WhenStartedAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(true);
            await TerminalSettledAsync(started, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
        }

        if (session is null)
        {
            return IdeToolResult.Error("No terminal could start: " + (TerminalWindow.Error ?? "no shell was found."));
        }

        var typed = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (Bool(args, "enter") != false && !typed.EndsWith('\r'))
        {
            typed += "\r";
        }

        session.Write(typed);
        await TerminalSettledAsync(session, TimeSpan.FromMilliseconds(700), wait, cancellationToken).ConfigureAwait(true);
        var screen = string.Join('\n', session.Terminal.ScreenText().Split('\n').Select(l => l.TrimEnd())).TrimEnd();
        return new($"Typed into the terminal '{session.Title}'{(fresh ? " (a new one)" : "")}, in {session.CurrentDirectory}{(session.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $"; its shell has exited with code {code}") : "")}. Its screen:\n{screen}");
    }

    /// <summary>Waits until the terminal's screen stays the same for <paramref name="quiet"/>, at most <paramref name="wait"/>.</summary>
    private static async Task TerminalSettledAsync(TerminalSession session, TimeSpan quiet, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        var last = session.Terminal.ScreenText();
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline && session.ExitCode is null)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            var now = session.Terminal.ScreenText();
            if (!string.Equals(now, last, StringComparison.Ordinal))
            {
                last = now;
                since = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - since >= quiet)
            {
                return;
            }
        }
    }
}
