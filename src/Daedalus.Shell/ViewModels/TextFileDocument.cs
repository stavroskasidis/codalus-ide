using System.ComponentModel;
using System.Text;
using AvaloniaEdit.Document;
using Daedalus.Shell.Docking;
using Daedalus.Shell.Infrastructure;

namespace Daedalus.Shell.ViewModels;

/// <summary>
/// A plain-text file in a document tab (settings.json, keybindings.json). Saving is atomic. This is a minimal
/// editor; the language-aware editor arrives with M3.
/// </summary>
public sealed class TextFileDocument : ShellDocument
{
    private readonly string _name;

    private TextFileDocument(string path, string text, EditorFont font)
    {
        Font = font;
        FilePath = path;
        _name = Path.GetFileName(path);
        Id = "file:" + path;
        Title = _name;
        Text = new TextDocument(text);
        Text.UndoStack.MarkAsOriginalFile();
        Text.UndoStack.PropertyChanged += OnUndoStackChanged;
    }

    public override string Kind => "textFile";

    public string FilePath { get; }

    public EditorFont Font { get; }

    public TextDocument Text { get; }

    /// <summary>Opens <paramref name="path"/>, creating it with <paramref name="initialContent"/> if it doesn't exist.</summary>
    public static async Task<TextFileDocument> OpenAsync(string path, string initialContent, EditorFont font, CancellationToken cancellationToken)
    {
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true) : initialContent;
        return new TextFileDocument(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), font);
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        await File.WriteAllTextAsync(temp, Text.Text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(true);
        File.Move(temp, FilePath, overwrite: true);
        Text.UndoStack.MarkAsOriginalFile();
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
        {
            IsModified = !Text.UndoStack.IsOriginalFile;
            Title = IsModified ? _name + " ●" : _name;
        }
    }
}
