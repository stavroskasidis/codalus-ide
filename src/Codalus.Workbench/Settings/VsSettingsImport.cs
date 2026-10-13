using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Settings;

/// <summary>What a <c>.vssettings</c> file gives: settings (the color customizations among them) and key bindings.</summary>
public sealed record VsSettingsResult(ImportResult<(string Key, JsonElement Value)> Settings, ImportResult<JsonElement> Keybindings);

/// <summary>
/// Importing the relevant parts of a classic IDE's exported <c>.vssettings</c> (M23a): the text editor's font and its
/// colors (as <c>editor.tokenColorCustomizations</c> and <c>workbench.colorCustomizations</c>), the text editor's tab and
/// display options, and the user's own keyboard shortcuts whose commands have an equivalent here. The rest of the file
/// (tool window layouts, other packages' options) has no counterpart and is skipped, each with why.
/// </summary>
public static class VsSettingsImport
{
    private const string TextEditorCategory = "{A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0}";

    /// <summary>Text editor display items, as VS Code's token color shorthands.</summary>
    private static readonly Dictionary<string, string> s_tokenItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Comment"] = "comments",
        ["Keyword"] = "keywords",
        ["String"] = "strings",
        ["Number"] = "numbers",
        ["class name"] = "types",
        ["User Types - Classes"] = "types",
        ["method name"] = "functions",
        ["User Members - Methods"] = "functions",
        ["local name"] = "variables",
        ["User Members - Locals"] = "variables",
    };

    /// <summary>Text editor display items, as the editor's color ids, and whether the item's background is the color.</summary>
    private static readonly Dictionary<string, (string Id, bool Background)> s_elementItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Selected Text"] = ("editor.selectionBackground", true),
        ["CurrentLineActiveFormat"] = ("editor.lineHighlightBackground", true),
        ["Brace Matching (Rectangle)"] = ("editorBracketMatch.background", true),
        ["Highlighted Reference"] = ("editor.wordHighlightBackground", true),
        ["Highlighted Written Reference"] = ("editor.wordHighlightStrongBackground", true),
        ["Find Match Highlight"] = ("editor.findMatchHighlightBackground", true),
        ["Syntax Error"] = ("editorError.foreground", false),
        ["Warning"] = ("editorWarning.foreground", false),
    };

    /// <summary>Text editor options, as our settings.</summary>
    private static readonly Dictionary<string, (string Key, bool Negate)> s_editorOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TabSize"] = ("editor.tabSize", false),
        ["InsertTabs"] = ("editor.insertSpaces", true),
        ["WordWrap"] = ("editor.wordWrap", false),
        ["ShowLineNumbers"] = ("editor.lineNumbers", false),
    };

    /// <summary>The classic IDE's command names, as ours.</summary>
    public static IReadOnlyDictionary<string, string> Commands { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Edit.Duplicate"] = "editor.action.duplicateSelection",
        ["Edit.CommentSelection"] = "editor.action.addCommentLine",
        ["Edit.UncommentSelection"] = "editor.action.removeCommentLine",
        ["Edit.ToggleLineComment"] = "editor.action.commentLine",
        ["Edit.FormatDocument"] = "editor.action.formatDocument",
        ["Edit.FormatSelection"] = "editor.action.formatSelection",
        ["Edit.GoToAll"] = "workbench.action.gotoAll",
        ["Edit.GoTo"] = "editor.action.gotoLine",
        ["Edit.Find"] = "editor.action.find",
        ["Edit.Replace"] = "editor.action.replace",
        ["Edit.FindinFiles"] = "workbench.action.findInFiles",
        ["Edit.FindNext"] = "editor.action.findNext",
        ["Edit.FindPrevious"] = "editor.action.findPrevious",
        ["Edit.GoToDefinition"] = "editor.action.revealDefinition",
        ["Edit.GoToImplementation"] = "editor.action.goToImplementation",
        ["Edit.FindAllReferences"] = "editor.action.referenceSearch",
        ["Edit.PeekDefinition"] = "editor.action.peekDefinition",
        ["Refactor.Rename"] = "editor.action.rename",
        ["View.QuickActions"] = "editor.action.quickFix",
        ["View.QuickActionsForPosition"] = "editor.action.quickFix",
        ["Edit.ListMembers"] = "editor.action.triggerSuggest",
        ["Edit.ParameterInfo"] = "editor.action.triggerParameterHints",
        ["Edit.QuickInfo"] = "editor.action.showHover",
        ["Edit.CompleteWord"] = "editor.action.triggerWordCompletion",
        ["Edit.MoveSelectedLinesUp"] = "editor.action.moveLinesUpAction",
        ["Edit.MoveSelectedLinesDown"] = "editor.action.moveLinesDownAction",
        ["Edit.ExpandSelection"] = "editor.action.smartSelect.expand",
        ["Edit.ContractSelection"] = "editor.action.smartSelect.shrink",
        ["Edit.ToggleBookmark"] = "bookmarks.toggle",
        ["Edit.NextBookmark"] = "bookmarks.next",
        ["Edit.PreviousBookmark"] = "bookmarks.previous",
        ["Edit.ClearBookmarks"] = "bookmarks.clearAll",
        ["Edit.ToggleOutliningExpansion"] = "editor.action.toggleFold",
        ["Edit.CollapsetoDefinitions"] = "editor.action.foldAll",
        ["Edit.SurroundWith"] = "editor.action.surroundWithSnippet",
        ["Edit.InsertSnippet"] = "editor.action.insertSnippet",
        ["Edit.CycleClipboardRing"] = "editor.action.pasteFromClipboardRing",
        ["Edit.InsertCaretsatAllMatching"] = "editor.action.selectHighlights",
        ["Edit.InsertNextMatchingCaret"] = "editor.action.addSelectionToNextFindMatch",
        ["Edit.Undo"] = "edit.undo",
        ["Edit.Redo"] = "edit.redo",
        ["Edit.SelectAll"] = "edit.selectAll",
        ["File.SaveSelectedItems"] = "file.save",
        ["File.SaveAll"] = "file.saveAll",
        ["Window.CloseDocumentWindow"] = "workbench.closeActiveDocument",
        ["Window.NextDocumentWindow"] = "workbench.nextDocument",
        ["Window.PreviousDocumentWindow"] = "workbench.previousDocument",
        ["View.NavigateBackward"] = "workbench.navigateBack",
        ["View.NavigateForward"] = "workbench.navigateForward",
        ["View.SolutionExplorer"] = "workbench.view.solutionExplorer",
        ["View.Output"] = "workbench.view.output",
        ["View.ErrorList"] = "workbench.view.errorList",
        ["View.Terminal"] = "workbench.view.terminal",
        ["Tools.Options"] = "workbench.openOptions",
        ["Build.BuildSolution"] = "build.buildSolution",
        ["Build.RebuildSolution"] = "build.rebuildSolution",
        ["Build.Cancel"] = "build.cancel",
        ["Debug.Start"] = "debug.start",
        ["Debug.StartWithoutDebugging"] = "run.startWithoutDebugging",
        ["Debug.StopDebugging"] = "debug.stop",
        ["Debug.Restart"] = "debug.restart",
        ["Debug.StepOver"] = "debug.stepOver",
        ["Debug.StepInto"] = "debug.stepInto",
        ["Debug.StepOut"] = "debug.stepOut",
        ["Debug.ToggleBreakpoint"] = "debug.toggleBreakpoint",
        ["Debug.RunToCursor"] = "debug.runToCursor",
        ["Debug.QuickWatch"] = "debug.quickWatch",
    };

    private static readonly Dictionary<string, string> s_keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "primary", ["Shift"] = "shift", ["Alt"] = "alt", ["Bkspce"] = "backspace", ["Del"] = "delete", ["Ins"] = "insert",
        ["PgUp"] = "pageup", ["PgDn"] = "pagedown", ["Esc"] = "escape", ["Up Arrow"] = "up", ["Down Arrow"] = "down",
        ["Left Arrow"] = "left", ["Right Arrow"] = "right", ["Break"] = "pause",
    };

    /// <summary>Reads <paramref name="xml"/>, a <c>.vssettings</c> file's text.</summary>
    public static VsSettingsResult Read(string xml, Func<string, SettingDescriptor?> find, Func<string, bool> commandExists)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            return new(new([], [("the file", $"isn't XML ({ex.Message})")]), new([], []));
        }

        var settings = new List<(string, JsonElement)>();
        var skipped = new List<(string, string)>();

        void Add(string key, JsonNode value)
        {
            var element = Element(value);
            if (find(key) is not { } descriptor)
            {
                skipped.Add((key, "not a Codalus IDE setting"));
            }
            else if (descriptor.Validate(element) is { } error)
            {
                skipped.Add((key, error));
            }
            else
            {
                settings.Add((key, element));
            }
        }

        ReadFontsAndColors(document, Add, skipped);
        ReadEditorOptions(document, Add);
        var keybindings = ReadShortcuts(document, commandExists);
        return new(new(settings, skipped), keybindings);
    }

    private static void ReadFontsAndColors(XDocument document, Action<string, JsonNode> add, List<(string, string)> skipped)
    {
        var editor = document.Descendants("Category")
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("GUID"), TextEditorCategory, StringComparison.OrdinalIgnoreCase));
        if (editor is null)
        {
            return;
        }

        if ((string?)editor.Attribute("FontName") is { Length: > 0 } font && (string?)editor.Attribute("FontIsDefault") != "Yes")
        {
            add("editor.fontFamily", JsonValue.Create($"{font}, monospace"));
        }

        if (double.TryParse((string?)editor.Attribute("FontSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var points) && points > 0)
        {
            add("editor.fontSize", JsonValue.Create(Math.Round(points * 96 / 72))); // points, as the editor's pixels
        }

        var tokens = new JsonObject();
        var elements = new JsonObject();
        foreach (var item in editor.Descendants("Item"))
        {
            var name = (string?)item.Attribute("Name") ?? string.Empty;
            if (s_tokenItems.TryGetValue(name, out var shorthand))
            {
                if (Color((string?)item.Attribute("Foreground")) is { } color && !tokens.ContainsKey(shorthand))
                {
                    tokens[shorthand] = color;
                }
            }
            else if (s_elementItems.TryGetValue(name, out var element))
            {
                if (Color((string?)item.Attribute(element.Background ? "Background" : "Foreground")) is { } color)
                {
                    elements[element.Id] = color;
                }
            }
            else if (Color((string?)item.Attribute("Foreground")) is not null || Color((string?)item.Attribute("Background")) is not null)
            {
                skipped.Add(($"the color of \"{name}\"", "no counterpart here"));
            }
        }

        if (tokens.Count > 0)
        {
            add(ColorKeys.Tokens, tokens);
        }

        if (elements.Count > 0)
        {
            add(ColorKeys.Workbench, elements);
        }
    }

    private static void ReadEditorOptions(XDocument document, Action<string, JsonNode> add)
    {
        // All Languages, or C# when the file has no All Languages page.
        var pages = document.Descendants("ToolsOptionsCategory").Where(c => (string?)c.Attribute("name") == "TextEditor")
            .Elements("ToolsOptionsSubCategory").ToList();
        var page = pages.FirstOrDefault(p => (string?)p.Attribute("name") == "AllLanguages") ?? pages.FirstOrDefault(p => (string?)p.Attribute("name") == "CSharp");
        foreach (var property in page?.Elements("PropertyValue") ?? [])
        {
            if (!s_editorOptions.TryGetValue((string?)property.Attribute("name") ?? string.Empty, out var option))
            {
                continue;
            }

            var text = property.Value.Trim();
            if (bool.TryParse(text, out var flag))
            {
                add(option.Key, JsonValue.Create(option.Negate ? !flag : flag));
            }
            else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                add(option.Key, JsonValue.Create(number));
            }
        }
    }

    private static ImportResult<JsonElement> ReadShortcuts(XDocument document, Func<string, bool> commandExists)
    {
        var imported = new List<JsonElement>();
        var skipped = new List<(string, string)>();
        var user = document.Descendants("UserShortcuts").FirstOrDefault();
        foreach (var shortcut in user?.Elements() ?? [])
        {
            var vsCommand = (string?)shortcut.Attribute("Command") ?? string.Empty;
            var label = $"{vsCommand} ({shortcut.Value})";
            if (!Commands.TryGetValue(vsCommand, out var command) || !commandExists(command))
            {
                skipped.Add((label, "no equivalent command here"));
                continue;
            }

            if (Keys(shortcut.Value) is not { } keys)
            {
                skipped.Add((label, "keys this importer doesn't read"));
                continue;
            }

            var entry = new JsonObject
            {
                ["key"] = keys,
                ["command"] = shortcut.Name.LocalName == "RemoveShortcut" ? "-" + command : command,
            };
            if ((string?)shortcut.Attribute("Scope") == "Text Editor")
            {
                entry["when"] = "editorTextFocus";
            }

            imported.Add(Element(entry));
        }

        return new(imported, skipped);
    }

    /// <summary><c>Ctrl+K, Ctrl+D</c> as <c>primary+k primary+d</c>; null for keys it can't name.</summary>
    public static string? Keys(string text)
    {
        var chords = new List<string>();
        foreach (var chord in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "Ctrl++" ends with the key "+".
            var parts = chord.EndsWith("++", StringComparison.Ordinal) ? [.. chord[..^2].Split('+'), "+"] : chord.Split('+');
            var names = new List<string>();
            foreach (var part in parts.Select(p => p.Trim()))
            {
                if (s_keys.TryGetValue(part, out var name))
                {
                    names.Add(name);
                }
                else if (part.Length == 1 || (part.Length <= 3 && part[0] is 'F' or 'f' && int.TryParse(part.AsSpan(1), out _)) || part is "Enter" or "Tab" or "Space" or "Home" or "End")
                {
                    names.Add(part.ToLowerInvariant());
                }
                else
                {
                    return null;
                }
            }

            chords.Add(string.Join('+', names));
        }

        return chords.Count > 0 ? string.Join(' ', chords) : null;
    }

    /// <summary>A <c>0x00BBGGRR</c> color as <c>#RRGGBB</c>; null for the "automatic" and "default" markers.</summary>
    public static string? Color(string? value)
    {
        if (value is null || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bgr) || bgr >> 24 != 0)
        {
            return null;
        }

        return $"#{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}";
    }

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>The two color settings' keys (the shell's <c>ColorCustomizations</c> owns them).</summary>
    public static class ColorKeys
    {
        public const string Workbench = "workbench.colorCustomizations";
        public const string Tokens = "editor.tokenColorCustomizations";
    }
}
