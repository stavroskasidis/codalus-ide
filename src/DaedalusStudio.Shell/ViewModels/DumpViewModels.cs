using System.Collections.ObjectModel;
using System.Globalization;
using DaedalusStudio.Debugging.Companion;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A thread of a dump, with its exception if it has one.</summary>
public sealed class DumpThreadRow(DumpThread thread)
{
    public DumpThread Thread { get; } = thread;

    public string Title => $"#{Thread.ManagedId} (OS {Thread.OsId}){(Thread.IsFinalizer ? " finalizer" : string.Empty)}";

    public string? Exception => Thread.Exception;

    public bool HasException => Thread.Exception is not null;

    public string TopFrame => Thread.Frames.Count > 0 ? Thread.Frames[0].Method : "(no managed frames)";

    public string AccessibleName => $"Thread {Title}{(HasException ? ", exception " + Exception : string.Empty)}, {TopFrame}";
}

public sealed class DumpFrameRow(DumpFrame frame)
{
    public DumpFrame Frame { get; } = frame;

    public string Method => Frame.Method;

    public string Location => Frame.File is { } file
        ? $"{Path.GetFileName(file)}:{Frame.Line}"
        : Frame.IlOffset >= 0 ? $"{Frame.Module} + IL_{Frame.IlOffset.ToString("x4", CultureInfo.InvariantCulture)}" : Frame.Module;

    public bool HasSource => Frame.File is not null && File.Exists(Frame.File);
}

public sealed class DumpHeapRow(DumpHeapType type)
{
    public DumpHeapType Type { get; } = type;

    public string Name => Type.Type;

    public string Count => Type.Count.ToString("N0", CultureInfo.InvariantCulture);

    public string Size => Bytes(Type.Size);

    internal static string Bytes(long size) => size switch
    {
        >= 1 << 30 => $"{size / (double)(1 << 30):0.0} GB",
        >= 1 << 20 => $"{size / (double)(1 << 20):0.0} MB",
        >= 1 << 10 => $"{size / (double)(1 << 10):0.0} KB",
        _ => $"{size} B",
    };
}

public sealed class DumpFieldRow(DumpField field)
{
    public DumpField Field { get; } = field;

    public string Name => Field.Name;

    public string Type => Field.Type;

    public string Value => Field.Value;

    /// <summary>A reference: clicking it inspects that object.</summary>
    public bool IsObject => Field.Address != 0;
}

/// <summary>
/// A dump session (M14b): read-only, from the debug companion host — threads and managed stacks (opening sources with
/// line numbers when the PDBs are here), the exceptions, heap statistics, a type's objects and an object's fields.
/// </summary>
public sealed class DumpViewModel : ObservableObject
{
    private readonly DebugCompanion _companion;
    private readonly Func<string, int, Task> _openSource;
    private readonly Stack<ulong> _back = new();
    private DumpThreadRow? _thread;
    private DumpHeapRow? _heapType;
    private string _status = string.Empty;
    private string? _objectTitle;

    public DumpViewModel(DumpSummary summary, DebugCompanion companion, Func<string, int, Task> openSource)
    {
        Summary = summary;
        _companion = companion;
        _openSource = openSource;
        foreach (var thread in summary.Threads.OrderByDescending(t => t.Exception is not null).ThenBy(t => t.ManagedId))
        {
            Threads.Add(new DumpThreadRow(thread));
        }

        // The thread that threw, else the first with managed code (the finalizer and idle threads have none).
        SelectedThread = Threads.FirstOrDefault(t => t.HasException) ?? Threads.FirstOrDefault(t => t.Thread.Frames.Count > 0) ?? Threads.FirstOrDefault();
    }

    public DumpSummary Summary { get; }

    public string Title => "Dump: " + Path.GetFileName(Summary.Path);

    public string Header => $"{Summary.Path} · {Summary.Runtime} · {Summary.Architecture}{(Summary.ProcessId is { } pid ? $" · process {pid}" : string.Empty)} · {Summary.Threads.Count} threads, {Summary.Modules.Count} modules";

    /// <summary>The first exception in the dump: usually why it was taken.</summary>
    public string? Exception => Summary.Threads.FirstOrDefault(t => t.Exception is not null) is { } thread ? $"Thread #{thread.ManagedId}: {thread.Exception}" : null;

    public bool HasException => Exception is not null;

    public ObservableCollection<DumpThreadRow> Threads { get; } = [];

    public ObservableCollection<DumpFrameRow> Frames { get; } = [];

    public ObservableCollection<DumpHeapRow> Heap { get; } = [];

    public ObservableCollection<string> Instances { get; } = [];

    public ObservableCollection<DumpFieldRow> Fields { get; } = [];

    public DumpThreadRow? SelectedThread
    {
        get => _thread;
        set
        {
            if (SetField(ref _thread, value))
            {
                Frames.Clear();
                foreach (var frame in value?.Thread.Frames ?? [])
                {
                    Frames.Add(new DumpFrameRow(frame));
                }
            }
        }
    }

    public DumpHeapRow? SelectedHeapType
    {
        get => _heapType;
        set
        {
            if (SetField(ref _heapType, value) && value is not null)
            {
                _ = LoadInstancesAsync(value);
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? ObjectTitle
    {
        get => _objectTitle;
        private set => SetField(ref _objectTitle, value);
    }

    public bool CanGoBack => _back.Count > 1;

    public Task OpenFrameAsync(DumpFrameRow frame) =>
        frame is { HasSource: true, Frame: { File: { } file, Line: { } line } } ? _openSource(file, line) : Task.CompletedTask;

    /// <summary>The heap's types by total size (enumerating a large heap takes a while: it loads on demand).</summary>
    public async Task LoadHeapAsync()
    {
        if (Heap.Count > 0)
        {
            return;
        }

        Status = "Reading the heap…";
        try
        {
            var heap = await _companion.DumpHeapAsync(Summary.Id, 500, CancellationToken.None).ConfigureAwait(true);
            foreach (var type in heap.Types)
            {
                Heap.Add(new DumpHeapRow(type));
            }

            Status = $"{heap.TotalObjects.ToString("N0", CultureInfo.InvariantCulture)} objects, {DumpHeapRow.Bytes(heap.TotalSize)}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = "Couldn't read the heap: " + ex.Message;
        }
    }

    private async Task LoadInstancesAsync(DumpHeapRow type)
    {
        Instances.Clear();
        try
        {
            foreach (var address in await _companion.DumpInstancesAsync(Summary.Id, type.Type.MethodTable, 1000, CancellationToken.None).ConfigureAwait(true))
            {
                Instances.Add($"0x{address:x}");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = "Couldn't list the objects: " + ex.Message;
        }
    }

    /// <summary>Shows an object's fields; a reference field opens its object (Back returns).</summary>
    public async Task InspectAsync(ulong address, bool remember = true)
    {
        try
        {
            var obj = await _companion.DumpObjectAsync(Summary.Id, address, CancellationToken.None).ConfigureAwait(true);
            if (remember)
            {
                _back.Push(address);
            }

            ObjectTitle = $"{obj.Type} at 0x{obj.Address:x} ({DumpHeapRow.Bytes(obj.Size)}){(obj.Value is { } value ? $" = \"{value}\"" : string.Empty)}";
            Fields.Clear();
            foreach (var field in obj.Fields)
            {
                Fields.Add(new DumpFieldRow(field));
            }

            OnPropertyChanged(nameof(CanGoBack));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = "Couldn't read the object: " + ex.Message;
        }
    }

    public Task InspectAsync(string address) =>
        ulong.TryParse(address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? InspectAsync(value) : Task.CompletedTask;

    public Task BackAsync()
    {
        if (_back.Count > 1)
        {
            _back.Pop();
            return InspectAsync(_back.Peek(), remember: false);
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync() => _companion.CloseDumpAsync(Summary.Id, CancellationToken.None);
}

/// <summary>A dump session's document tab.</summary>
public sealed class DumpDocument : ShellDocument
{
    public DumpDocument(DumpViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = "dump:" + viewModel.Summary.Path;
        Title = viewModel.Title;
    }

    public override string Kind => "dump";

    public DumpViewModel ViewModel { get; }
}
