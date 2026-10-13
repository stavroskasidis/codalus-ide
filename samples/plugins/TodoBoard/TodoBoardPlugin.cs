using System.Text.Json;
using Codalus.Plugins;

namespace TodoBoard;

/// <summary>
/// A sample of declarative views (plugin API 1.0): <c>*.todos.json</c> files open in a board (a table of items, a check
/// box for the selected one, and a box to add one), and the Todo Summary tool window counts what's open. The plugin
/// ships no UI code: it describes its views, and the IDE renders them.
/// </summary>
public sealed class TodoBoardPlugin : ICodalusPlugin, ICustomEditorProvider
{
    private const string SummaryView = "todoboard.summary";
    private readonly Dictionary<string, Board> _boards = new(StringComparer.Ordinal);
    private IPluginContext _context = null!;

    private sealed class Board(string path, List<Item> items)
    {
        public string Path { get; } = path;

        public List<Item> Items { get; } = items;

        public int Selected { get; set; } = -1;
    }

    private sealed record Item(string Title, bool Done);

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Subscriptions.Add(context.Window.RegisterCustomEditor("todoboard.board", this));
        UpdateSummary();
        return Task.CompletedTask;
    }

    public Task OpenAsync(CustomEditorDocument document, CancellationToken cancellationToken)
    {
        var board = new Board(document.Path, Parse(document.Text));
        lock (_boards)
        {
            _boards[document.ViewId] = board;
        }

        _context.Subscriptions.Add(_context.Window.OnViewEvent(document.ViewId, (e, _) =>
        {
            OnEvent(document.ViewId, board, e);
            return Task.CompletedTask;
        }));
        Render(document.ViewId, board);
        UpdateSummary();
        return Task.CompletedTask;
    }

    public Task CloseAsync(string viewId, CancellationToken cancellationToken)
    {
        lock (_boards)
        {
            _boards.Remove(viewId);
        }

        UpdateSummary();
        return Task.CompletedTask;
    }

    private void OnEvent(string viewId, Board board, ViewEvent e)
    {
        switch (e.ElementId, e.Kind)
        {
            case ("items", "select"):
                board.Selected = e.Row ?? -1;
                break;
            case ("done", "change") when board.Selected >= 0 && board.Selected < board.Items.Count:
                board.Items[board.Selected] = board.Items[board.Selected] with { Done = e.Value == "true" };
                Changed(viewId, board);
                break;
            case ("new", "change") when !string.IsNullOrWhiteSpace(e.Value):
                board.Items.Add(new Item(e.Value!.Trim(), false));
                Changed(viewId, board);
                break;
            case ("clear", "click"):
                board.Items.RemoveAll(i => i.Done);
                board.Selected = -1;
                Changed(viewId, board);
                break;
            default:
                return;
        }

        Render(viewId, board);
    }

    /// <summary>The file's new text goes to the IDE, which marks it modified until the user saves.</summary>
    private void Changed(string viewId, Board board)
    {
        _context.Window.SetCustomEditorText(viewId, JsonSerializer.Serialize(board.Items.Select(i => new Dictionary<string, object> { ["title"] = i.Title, ["done"] = i.Done }), new JsonSerializerOptions { WriteIndented = true }) + "\n");
        UpdateSummary();
    }

    private void Render(string viewId, Board board)
    {
        var selected = board.Selected >= 0 && board.Selected < board.Items.Count ? board.Items[board.Selected] : null;
        _context.Window.SetView(viewId, ViewNode.Stack(
            ViewNode.Heading(System.IO.Path.GetFileName(board.Path)),
            ViewNode.Label($"{board.Items.Count(i => i.Done)} of {board.Items.Count} done", "muted"),
            ViewNode.Table("items", ["Item", "Done"], [.. board.Items.Select(i => (IReadOnlyList<string>)[i.Title, i.Done ? "yes" : ""])]),
            ViewNode.CheckBox("done", selected is null ? "Done (select an item)" : $"Done: {selected.Title}", selected?.Done == true) with { Disabled = selected is null },
            ViewNode.Separator(),
            ViewNode.Row(ViewNode.TextBox("new", placeholder: "New item (Enter adds it)"), ViewNode.Button("clear", "Remove done items"))));
    }

    private void UpdateSummary()
    {
        List<(string Name, int Done, int Total)> boards;
        lock (_boards)
        {
            boards = [.. _boards.Values.Select(b => (System.IO.Path.GetFileName(b.Path), b.Items.Count(i => i.Done), b.Items.Count))];
        }

        _context.Window.SetView(SummaryView, boards.Count == 0
            ? ViewNode.Stack(ViewNode.Heading("Todo boards"), ViewNode.Label("Open a *.todos.json file to see it here.", "muted"))
            : ViewNode.Stack(ViewNode.Heading("Todo boards"), ViewNode.Table("boards", ["Board", "Done"], [.. boards.Select(b => (IReadOnlyList<string>)[b.Name, $"{b.Done}/{b.Total}"])])));
    }

    private static List<Item> Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text.Length == 0 ? "[]" : text);
            return [.. document.RootElement.EnumerateArray().Select(e => new Item(
                e.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                e.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True))];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
