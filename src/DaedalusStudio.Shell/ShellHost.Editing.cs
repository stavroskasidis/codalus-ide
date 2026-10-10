using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Snippets;

namespace DaedalusStudio.Shell;

/// <summary>
/// Editing depth (M12b): snippets (built in, from plugins, the user's; Insert Snippet, Surround With, Visual Studio
/// <c>.snippet</c> import).
/// </summary>
public sealed partial class ShellHost
{
    private string UserSnippetsDirectory => Path.Combine(Path.GetDirectoryName(Workbench.Paths.SettingsFile)!, "snippets");

    SnippetRegistry IEditorServices.Snippets => Workbench.Snippets;

    /// <summary>Loads the built-in snippets (embedded) and the user's (<c>config/snippets/&lt;language&gt;.json</c>).</summary>
    private void LoadSnippets()
    {
        var builtIn = new List<Snippet>();
        var assembly = typeof(ShellHost).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("DaedalusStudio.Shell.Snippets.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var language = Path.GetFileNameWithoutExtension(name["DaedalusStudio.Shell.Snippets.".Length..]);
            builtIn.AddRange(SnippetRegistry.ParseJson(reader.ReadToEnd(), language, "builtin"));
        }

        Workbench.Snippets.Set("builtin", builtIn);
        LoadUserSnippets();
    }

    private void LoadUserSnippets()
    {
        var user = new List<Snippet>();
        if (Directory.Exists(UserSnippetsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(UserSnippetsDirectory, "*.json"))
            {
                try
                {
                    user.AddRange(SnippetRegistry.ParseJson(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file), "user"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    Workbench.Log.AppendLine($"[snippets] {file}: {ex.Message}");
                }
            }
        }

        Workbench.Snippets.Set("user", user);
    }

    /// <summary>Ctrl+K Ctrl+X: picks one of the language's snippets and inserts it at the caret.</summary>
    public Task InsertSnippetAsync() => PickSnippetAsync(surround: false);

    /// <summary>Ctrl+K Ctrl+Shift+S: picks a snippet that uses the selection (if, try, region…) and wraps the selection in it.</summary>
    public Task SurroundWithAsync() => PickSnippetAsync(surround: true);

    private async Task PickSnippetAsync(bool surround)
    {
        if (ActiveEditor is not { Controller: { } controller } editor || editor.Model.IsReadOnly)
        {
            return;
        }

        var snippets = Workbench.Snippets.For(editor.Model.LanguageId).Where(s => !surround || s.IsSurround).ToList();
        if (snippets.Count == 0)
        {
            ShowMessage(surround ? "No Surround With snippets for this language." : "No snippets for this language.");
            return;
        }

        var items = snippets.Select(s => new QuickPickItem(s.Name)
        {
            Detail = string.Join(", ", s.Prefixes) + (s.Description is { Length: > 0 } d ? " — " + d : string.Empty),
            Tag = s,
        }).ToList();
        if (await Palette.ShowAsync(items, surround ? "Surround With" : "Insert Snippet").ConfigureAwait(true) is not { Tag: Snippet snippet })
        {
            return;
        }

        controller.InsertSnippet(snippet.Body, surround);
    }

    /// <summary>Opens the user snippet file of the active document's language, creating it with an example first.</summary>
    public async Task OpenUserSnippetsAsync()
    {
        var language = ActiveEditor?.Model.LanguageId is { Length: > 0 } id && id != DaedalusStudio.Workbench.Languages.LanguageRegistry.PlainText ? id : "csharp";
        Directory.CreateDirectory(UserSnippetsDirectory);
        var file = Path.Combine(UserSnippetsDirectory, language + ".json");
        if (!File.Exists(file))
        {
            await File.WriteAllTextAsync(file, $$"""
                {
                  // Snippets for {{language}}, in VS Code's format. $1, $2… are tab stops, ${1:text} has a default,
                  // $0 is where the caret ends, $TM_SELECTED_TEXT makes it a Surround With snippet.
                  // "Print": { "prefix": "pr", "body": ["Console.WriteLine($1);$0"], "description": "Print a line" }
                }

                """).ConfigureAwait(true);
        }

        await OpenFileAsync(file).ConfigureAwait(true);
    }

    /// <summary>Imports Visual Studio <c>.snippet</c> files into the user's snippets (by language).</summary>
    public async Task ImportVisualStudioSnippetsAsync()
    {
        if (Window is null || await Window.PickFileAsync("Import Snippets", "Code snippets (.snippet)", ["*.snippet"]).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        IReadOnlyList<Snippet> imported;
        try
        {
            imported = SnippetRegistry.ParseVisualStudio(await File.ReadAllTextAsync(path).ConfigureAwait(true), "user");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            ShowMessage("Could not read the snippet file: " + ex.Message);
            return;
        }

        ImportSnippets(imported);
        ShowMessage(imported.Count == 1 ? $"Imported the snippet '{imported[0].Name}'." : $"Imported {imported.Count} snippets.");
    }

    /// <summary>Merges snippets into the user's snippet files (tests and the import command).</summary>
    internal void ImportSnippets(IEnumerable<Snippet> snippets)
    {
        Directory.CreateDirectory(UserSnippetsDirectory);
        foreach (var group in snippets.GroupBy(s => s.Language))
        {
            var file = Path.Combine(UserSnippetsDirectory, group.Key + ".json");
            var existing = File.Exists(file)
                ? SnippetRegistry.ParseJson(File.ReadAllText(file), group.Key, "user").ToDictionary(s => s.Name)
                : null;
            File.WriteAllText(file, SnippetRegistry.ToJson(group, existing));
        }

        LoadUserSnippets();
    }
}
