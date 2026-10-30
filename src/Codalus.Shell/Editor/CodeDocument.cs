using System.ComponentModel;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Editor;

/// <summary>Edits of one document version, as LSP content changes (positions in the text before each change).</summary>
public sealed class CodeDocumentChangedEventArgs(int version, TextDocumentContentChangeEvent[] changes) : EventArgs
{
    public int Version { get; } = version;

    public TextDocumentContentChangeEvent[] Changes { get; } = changes;
}

/// <summary>
/// The text model of a file, shared by every view of it (tabs, split views). It keeps the file's encoding, BOM and
/// line endings, saves atomically, and notices changes on disk: an unmodified document reloads, a modified one raises
/// <see cref="ConflictDetected"/>. Edits are reported as LSP content changes, one <see cref="Version"/> per document
/// update, for the language service. UI thread only.
/// </summary>
public sealed class CodeDocument : ObservableObject, IDisposable
{
    /// <summary>The characters past which a file opens in large-file mode (VS Code's limit, 20 MB).</summary>
    public const int LargeFileCharacters = 20 * 1024 * 1024;

    /// <summary>The lines past which a file opens in large-file mode (VS Code's limit).</summary>
    public const int LargeFileLines = 300_000;

    private readonly List<TextDocumentContentChangeEvent> _pending = [];
    private FileSystemWatcher? _watcher;
    private DateTime _lastWrite;
    private long _lastLength;
    private bool _isDeletedOnDisk;
    private bool _isModified;
    private int _changeCheckScheduled;
    private int _saving;
    private bool _checkAfterSave;

    private CodeDocument(string path, string? uri, string text, TextFileFormat format, bool isReadOnly)
    {
        Path = path;
        Uri = uri ?? DocumentUris.FromPath(path);
        Name = System.IO.Path.GetFileName(path);
        Format = format;
        IsReadOnly = isReadOnly;
        Text = new TextDocument(text) { FileName = path };
        IsLarge = text.Length > LargeFileCharacters || Text.LineCount > LargeFileLines;
        Text.UndoStack.MarkAsOriginalFile();
        Text.UndoStack.PropertyChanged += OnUndoStackChanged;
        Text.Changing += OnChanging;
        Text.Changed += OnChanged;
        Text.UpdateFinished += (_, _) => Flush();
    }

    /// <summary>The file path (for a source-generated document: its virtual path).</summary>
    public string Path { get; }

    /// <summary>The LSP document URI.</summary>
    public string Uri { get; }

    public string Name { get; }

    public TextDocument Text { get; }

    public TextFileFormat Format { get; private set; }

    /// <summary>Generated documents are read-only.</summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// The file had more than <see cref="LargeFileCharacters"/> characters or <see cref="LargeFileLines"/> lines when it
    /// opened (M56, VS Code's limits): its editors have no colors, folding or CodeLens, and no language service gets it,
    /// which kept the window busy for seconds on such a file. Decided once, when it opens.
    /// </summary>
    public bool IsLarge { get; }

    /// <summary>The language service that owns this document (<c>csharp</c>), or null.</summary>
    public string? LanguageId
    {
        get => _languageId;
        set => SetField(ref _languageId, value);
    }

    private string? _languageId;

    /// <summary>Incremented once per document update; the language service sees the same numbers.</summary>
    public int Version { get; private set; } = 1;

    public bool IsModified
    {
        get => _isModified;
        private set => SetField(ref _isModified, value);
    }

    /// <summary>The file was deleted (or moved) outside the editor while the document is open.</summary>
    public bool IsDeletedOnDisk
    {
        get => _isDeletedOnDisk;
        private set => SetField(ref _isDeletedOnDisk, value);
    }

    /// <summary>Raised after each document update with the edits it made.</summary>
    public event EventHandler<CodeDocumentChangedEventArgs>? ContentChanged;

    /// <summary>The file changed on disk while the document has unsaved changes.</summary>
    public event EventHandler? ConflictDetected;

    /// <summary>The document was reloaded from disk.</summary>
    public event EventHandler? Reloaded;

    public event EventHandler? Saved;

    /// <summary>
    /// Reads a file into a new document. Opened from the UI thread, the text is decoded and its lines indexed off it (a
    /// 20 MB file took a few hundred milliseconds there), and the document is handed to the UI thread when it's ready.
    /// </summary>
    public static async Task<CodeDocument> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            return await ReadAsync(path, owner: null, cancellationToken).ConfigureAwait(true);
        }

        var owner = Thread.CurrentThread;
        return await Task.Run(() => ReadAsync(path, owner, cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    private static async Task<CodeDocument> ReadAsync(string path, Thread? owner, CancellationToken cancellationToken)
    {
        var (text, format) = await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(owner is null);
        var document = new CodeDocument(path, null, text, format, isReadOnly: false);
        document.RememberFileState();
        document.Watch();
        if (owner is not null)
        {
            document.Text.SetOwnerThread(owner); // AvaloniaEdit checks every access against the thread that built it
        }

        return document;
    }

    /// <summary>A file that doesn't exist yet (e.g. settings.json), saved on first save.</summary>
    public static CodeDocument Create(string path, string text) => new(path, null, text, TextFileFormat.Default, isReadOnly: false);

    /// <summary>A read-only document with text from somewhere else (a source generator, a decompiled assembly).</summary>
    public static CodeDocument ReadOnly(string path, string uri, string text, string label) =>
        new(path, uri, text, TextFileFormat.Default, isReadOnly: true) { ReadOnlyLabel = label };

    /// <summary>Why a read-only document is read-only, shown in its tab (<c>generated</c>, <c>decompiled</c>).</summary>
    public string? ReadOnlyLabel { get; private init; }

    /// <summary>
    /// Applies LSP edits (positions in the current text) as one undoable change. Returns false when they overlap.
    /// </summary>
    public bool ApplyEdits(IEnumerable<LanguageServices.Protocol.TextEdit> edits)
    {
        var spans = edits
            .Select(e => (Start: OffsetAt(e.Range.Start), End: OffsetAt(e.Range.End), e.NewText))
            .OrderByDescending(e => e.Start)
            .ThenByDescending(e => e.End)
            .ToList();
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].End > spans[i - 1].Start)
            {
                return false;
            }
        }

        using (Text.RunUpdate())
        {
            foreach (var (start, end, text) in spans)
            {
                Text.Replace(start, end - start, text);
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ edits → LSP changes

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        var start = PositionAt(e.Offset);
        var end = e.RemovalLength == 0 ? start : PositionAt(e.Offset + e.RemovalLength);
        _pending.Add(new TextDocumentContentChangeEvent(new Range(start, end), e.InsertedText.Text));
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (!Text.IsInUpdate)
        {
            Flush();
        }
    }

    private void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var changes = _pending.ToArray();
        _pending.Clear();
        Version++;
        ContentChanged?.Invoke(this, new CodeDocumentChangedEventArgs(Version, changes));
    }

    /// <summary>The LSP position (zero-based line, UTF-16 character) of an offset.</summary>
    public Position PositionAt(int offset)
    {
        var line = Text.GetLineByOffset(Math.Clamp(offset, 0, Text.TextLength));
        return new Position(line.LineNumber - 1, offset - line.Offset);
    }

    /// <summary>The offset of an LSP position, clamped to the document.</summary>
    public int OffsetAt(Position position)
    {
        if (position.Line >= Text.LineCount)
        {
            return Text.TextLength;
        }

        var line = Text.GetLineByNumber(Math.Max(1, position.Line + 1));
        return line.Offset + Math.Clamp(position.Character, 0, line.Length);
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
        {
            IsModified = !Text.UndoStack.IsOriginalFile;
        }
    }

    // ------------------------------------------------------------------ saving and reloading

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            return;
        }

        // While the file is written the watcher sees the new time before it is remembered; its check waits for the save
        // (a save awaited at a low priority, as the smoke test's, could otherwise be reported as a change on disk).
        _saving++;
        try
        {
            await TextFiles.WriteAsync(Path, Text.Text, Format, cancellationToken).ConfigureAwait(true);
            RememberFileState();
            Text.UndoStack.MarkAsOriginalFile();
            IsDeletedOnDisk = false;
        }
        finally
        {
            if (--_saving == 0 && _checkAfterSave)
            {
                _checkAfterSave = false;
                ScheduleCheck(); // a change from elsewhere during the save is still seen
            }
        }

        if (_watcher is null)
        {
            Watch();
        }

        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the text with the file's (one undoable change, so the language service sees an edit): only the part that
    /// differs, found off the UI thread. A log being written is reloaded at each write, and replacing all of it rebuilt
    /// every editor's lines and sent the language service the whole text each time (M56); now that's what was appended.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var version = Version;
        var snapshot = Text.CreateSnapshot();
        var (text, format) = await TextFiles.ReadAsync(Path, cancellationToken).ConfigureAwait(true);
        var (start, removed, inserted) = await Task.Run(() => Difference(snapshot.Text, text), cancellationToken).ConfigureAwait(true);
        Format = format;
        OnPropertyChanged(nameof(Format));
        if (Version != version)
        {
            (start, removed, inserted) = (0, Text.TextLength, text); // edited while the file was read: all of it, as asked
        }

        if (removed > 0 || inserted.Length > 0)
        {
            Text.Replace(start, removed, inserted);
        }

        RememberFileState();
        Text.UndoStack.MarkAsOriginalFile();
        IsDeletedOnDisk = false;
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What turns <paramref name="before"/> into <paramref name="after"/>: the text between their common start and end.</summary>
    internal static (int Start, int Removed, string Inserted) Difference(string before, string after)
    {
        var start = before.AsSpan().CommonPrefixLength(after);
        var end = 0;
        var most = Math.Min(before.Length, after.Length) - start;
        while (end < most && before[before.Length - end - 1] == after[after.Length - end - 1])
        {
            end++;
        }

        return (start, before.Length - start - end, after[start..(after.Length - end)]);
    }

    public void SetLineEnding(string lineEnding)
    {
        Format = Format with { LineEnding = lineEnding };
        OnPropertyChanged(nameof(Format));
    }

    private void RememberFileState()
    {
        if (Remote is { } remote)
        {
            // A remote window's file (M21): the server's time and size, once they arrive.
            _ = remote.StatAsync(Path, CancellationToken.None).ContinueWith(
                t => Ui.Post(() => (_lastWrite, _lastLength) = t.IsCompletedSuccessfully && t.Result.Exists ? (t.Result.ModifiedUtc, t.Result.Size) : (default, -1)),
                TaskScheduler.Default);
            return;
        }

        try
        {
            var info = new FileInfo(Path);
            (_lastWrite, _lastLength) = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The remote file system of a remote window's file (M21), or null for a file here.</summary>
    private Workbench.FileSystem.IFileSystemProvider? Remote =>
        Workbench.FileSystem.FileSystems.For(Path) is { IsRemote: true } remote ? remote : null;

    private IDisposable? _remoteWatch;

    private void Watch()
    {
        if (!IsReadOnly && Remote is { } remote)
        {
            _remoteWatch ??= remote.Watch(Path, recursive: false, _ => ScheduleCheck());
            return;
        }

        if (IsReadOnly || System.IO.Path.GetDirectoryName(Path) is not { } directory || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(Path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, _) => ScheduleCheck();
        _watcher.Created += (_, _) => ScheduleCheck();
        _watcher.Deleted += (_, _) => ScheduleCheck();
        _watcher.Renamed += (_, _) => ScheduleCheck();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Watcher events come in bursts on other threads; check once, on the UI thread, after they settle.</summary>
    private void ScheduleCheck()
    {
        if (Interlocked.Exchange(ref _changeCheckScheduled, 1) == 0)
        {
            _ = Task.Delay(200).ContinueWith(_ => Ui.Post(CheckFile), TaskScheduler.Default);
        }
    }

    private void CheckFile()
    {
        Interlocked.Exchange(ref _changeCheckScheduled, 0);
        if (Remote is { } remote)
        {
            _ = remote.StatAsync(Path, CancellationToken.None).ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        Ui.Post(() => OnFileState(t.Result.Exists, t.Result.ModifiedUtc, t.Result.Size));
                    }
                },
                TaskScheduler.Default);
            return;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(Path);
        }
        catch (IOException)
        {
            return;
        }

        OnFileState(info.Exists, info.Exists ? info.LastWriteTimeUtc : default, info.Exists ? info.Length : -1);
    }

    private void OnFileState(bool exists, DateTime lastWrite, long length)
    {
        if (_saving > 0)
        {
            _checkAfterSave = true; // our own save is writing the file: checked again once its state is remembered
            return;
        }

        if (!exists)
        {
            IsDeletedOnDisk = true;
            return;
        }

        IsDeletedOnDisk = false;
        if (lastWrite == _lastWrite && length == _lastLength)
        {
            return; // our own save, or a touch
        }

        if (IsModified)
        {
            RememberFileState();
            ConflictDetected?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _ = ReloadSafelyAsync();
        }
    }

    private async Task ReloadSafelyAsync()
    {
        try
        {
            await ReloadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being written; the next change event retries.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _remoteWatch?.Dispose();
        _remoteWatch = null;
    }
}
