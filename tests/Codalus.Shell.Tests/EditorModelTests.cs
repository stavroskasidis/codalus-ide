using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

public class TextFilesTests
{
    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, "UTF-8 with BOM", true)]
    [InlineData(new byte[] { (byte)'a', 0xC3, 0xA9 }, "UTF-8", false)]
    [InlineData(new byte[] { 0xFF, 0xFE, (byte)'a', 0 }, "UTF-16 LE", true)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0, (byte)'a' }, "UTF-16 BE", true)]
    [InlineData(new byte[] { (byte)'a', 0xE9 }, "Latin-1", false)]
    public void Detects_the_encoding_and_the_bom(byte[] bytes, string encoding, bool bom)
    {
        var (_, format) = TextFiles.Decode(bytes);
        Assert.Equal(encoding, format.EncodingName);
        Assert.Equal(bom, format.HasBom);
    }

    [Theory]
    [InlineData("a\r\nb\nc", "\r\n")]
    [InlineData("a\nb\r\n", "\n")]
    [InlineData("a\rb", "\r")]
    public void The_first_line_ending_wins(string text, string expected) => Assert.Equal(expected, TextFiles.DetectLineEnding(text));

    [Fact]
    public void Round_trips_bytes_exactly()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("über\r\nmixed\nendings")];
        var (text, format) = TextFiles.Decode(original);
        Assert.Equal(original, TextFiles.Encode(text, format));

        byte[] latin = [(byte)'c', (byte)'a', (byte)'f', 0xE9];
        var (latinText, latinFormat) = TextFiles.Decode(latin);
        Assert.Equal("café", latinText);
        Assert.Equal(latin, TextFiles.Encode(latinText, latinFormat));
    }
}

public class CaretEditsTests
{
    [Fact]
    public void Typing_at_several_carets_shifts_later_carets()
    {
        var (edits, carets) = CaretEdits.Replace([CaretSelection.At(0), CaretSelection.At(5), CaretSelection.At(10)], () => "ab");

        Assert.Equal([(10, 0, "ab"), (5, 0, "ab"), (0, 0, "ab")], edits);
        Assert.Equal([2, 9, 16], carets);
    }

    [Fact]
    public void Selections_are_replaced_and_overlaps_merge()
    {
        var (edits, carets) = CaretEdits.Replace([new CaretSelection(2, 6), new CaretSelection(4, 8), new CaretSelection(10, 12)], () => "x");

        Assert.Equal([(10, 2, "x"), (2, 6, "x")], edits);
        Assert.Equal([3, 6], carets);
    }

    [Fact]
    public void Backspace_widens_empty_selections_and_merges_neighbours()
    {
        // Carets at 3 and 4: each deletes the previous character; the ranges [2,3) and [3,4) don't overlap.
        var (edits, carets) = CaretEdits.Replace([CaretSelection.At(3), CaretSelection.At(4)], () => string.Empty, caret => (caret - 1, caret));

        Assert.Equal([(3, 1, string.Empty), (2, 1, string.Empty)], edits);
        Assert.Equal([2, 2], carets);
    }

    [Fact]
    public void Duplicate_carets_collapse() =>
        Assert.Single(CaretEdits.Normalize([CaretSelection.At(4), CaretSelection.At(4)]));
}

public class MarkdownRendererTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Backslash_escapes_are_literal()
    {
        var control = Codalus.Shell.Controls.MarkdownRenderer.Render(@"a \*b\* \_c\_ **d**", _ => { });
        var block = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(control).OfType<Avalonia.Controls.TextBlock>().FirstOrDefault()
                    ?? control as Avalonia.Controls.TextBlock
                    ?? ((Avalonia.Controls.Panel)control).Children.OfType<Avalonia.Controls.TextBlock>().First();
        var text = string.Concat(block.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal("a *b* _c_ d", text);
    }
}

public class CodeDocumentDiskTests
{
    /// <summary>
    /// The watcher's check of the file a save is writing runs before the save remembers the file's new time (the smoke
    /// test saves at Background priority, so its continuation waited behind the check at Default): it reported the
    /// document's own save as "changed on disk, and you have unsaved changes". The write is held open here, so the check
    /// runs during it on every OS (a local write can finish at once, as on Windows, leaving no window to test).
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Its_own_save_is_not_a_change_on_disk_when_the_watcher_check_runs_first()
    {
        var files = new HeldWrites();
        var root = Path.Combine(Path.GetTempPath(), "codalus-held-" + Guid.NewGuid().ToString("N"));
        using var mount = Workbench.FileSystem.FileSystems.Mount(root, files);
        var path = Path.Combine(root, "File.cs");
        using var document = await CodeDocument.OpenAsync(path, CancellationToken.None);
        await PumpAsync(TimeSpan.FromMilliseconds(300));
        var conflicts = 0;
        document.ConflictDetected += (_, _) => conflicts++;

        document.Text.Insert(0, "// edited\n");
        Assert.True(document.IsModified);
        var save = document.SaveAsync(CancellationToken.None);
        Assert.False(save.IsCompleted, "the save waits for its write");

        // The file has its new time; the watcher's check of it runs while the save still writes.
        var stats = files.Stats;
        files.Notify(path);
        await PumpAsync(TimeSpan.FromSeconds(10), () => files.Stats > stats);
        await PumpAsync(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, conflicts);

        files.Release.SetResult();
        await save;
        await PumpAsync(TimeSpan.FromMilliseconds(600));
        Assert.Equal(0, conflicts);
        Assert.False(document.IsModified);
        Assert.Equal("// edited\nclass A { }\n", files.Text);

        // A change from outside while the document has unsaved changes is still one.
        document.Text.Insert(0, "// mine\n");
        files.Write("class B { }\n");
        files.Notify(path);
        await PumpAsync(TimeSpan.FromSeconds(10), () => conflicts > 0);
        Assert.Equal(1, conflicts);
    }

    private static async Task PumpAsync(TimeSpan limit, Func<bool>? until = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < limit && until?.Invoke() != true)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A remote file system whose writes land at once but finish only when <see cref="Release"/> is set.</summary>
    private sealed class HeldWrites : Workbench.FileSystem.IFileSystemProvider
    {
        private readonly Lock _gate = new();
        private byte[] _content = Encoding.UTF8.GetBytes("class A { }\n");
        private DateTime _modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private Action<IReadOnlyList<Workbench.FileSystem.FileChangeEvent>>? _changed;
        private int _stats;

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Stats => Volatile.Read(ref _stats);

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return Encoding.UTF8.GetString(_content);
                }
            }
        }

        public bool IsRemote => true;

        public void Write(string text) => Store(Encoding.UTF8.GetBytes(text));

        public void Notify(string path) => _changed?.Invoke([new(path, "changed")]);

        public Task<Workbench.FileSystem.FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stats);
            lock (_gate)
            {
                return Task.FromResult(new Workbench.FileSystem.FileEntryInfo(true, false, _content.Length, _modified));
            }
        }

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(_content);
            }
        }

        public async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            Store(content);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public IDisposable Watch(string path, bool recursive, Action<IReadOnlyList<Workbench.FileSystem.FileChangeEvent>> changed)
        {
            _changed = changed;
            return new Unwatch(this);
        }

        public Task<IReadOnlyList<Workbench.FileSystem.FileSystemEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        private void Store(byte[] content)
        {
            lock (_gate)
            {
                (_content, _modified) = (content, _modified.AddSeconds(1));
            }
        }

        private sealed class Unwatch(HeldWrites files) : IDisposable
        {
            public void Dispose() => files._changed = null;
        }
    }
}
