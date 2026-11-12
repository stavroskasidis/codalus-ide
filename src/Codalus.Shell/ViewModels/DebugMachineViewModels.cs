using System.Globalization;
using System.Text;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>A register of the selected frame; <see cref="IsChanged"/>: its value differs from the last stop's.</summary>
public sealed record RegisterRow(string Name, string Value, bool IsChanged)
{
    public string AccessibleName => IsChanged ? $"{Name} {Value}, changed" : $"{Name} {Value}";
}

/// <summary>
/// The Registers window (M58, ADR 0094): the selected frame's registers under the Codalus debugger, read at a stop
/// while the window is shown; those that changed since the last stop are marked.
/// </summary>
public sealed class RegistersViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.registers";

    private readonly DebugService _debugger;
    private readonly ShownRefresh _refresh;
    private Dictionary<string, string> _last = new(StringComparer.Ordinal);
    private string? _message;
    private int _generation;

    public RegistersViewModel(DebugService debugger)
    {
        _debugger = debugger;
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(_refresh.Request);
        debugger.FrameChanged += (_, _) => Ui.Post(_refresh.Request);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                _generation++;
                Rows.Reset([]);
                Message = null;
                OnPropertyChanged(nameof(IsEmpty));
            }

            if (debugger.State == DebugState.Idle)
            {
                _last.Clear();
            }
        });
    }

    public ResettableCollection<RegisterRow> Rows { get; } = [];

    void IShownAware.SetViewShown(object view, bool shown) => _refresh.SetShown(view, shown);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Why there are no registers while the program is stopped (netcoredbg, the browser); null otherwise.</summary>
    public string? Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public async Task RefreshAsync()
    {
        _refresh.Fresh();
        var generation = ++_generation;
        if (_debugger.State != DebugState.Stopped)
        {
            return;
        }

        if (!_debugger.CanShowRegisters)
        {
            Rows.Reset([]);
            Message = Localizer.Get("registers.needsCodalus", "Registers are shown by the Codalus debugger (Options › Debugging › Debugger for .NET programs).");
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        var registers = await _debugger.RegistersAsync().ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        Rows.Reset([.. registers.Select(r => new RegisterRow(r.Name, r.Value, _last.TryGetValue(r.Name, out var before) && before != r.Value))]);
        _last = registers.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);
        Message = registers.Count == 0 ? Localizer.Get("registers.noneHere", "This frame has no registers to show (it isn't running .NET code).") : null;
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>
/// A row of the Disassembly window: an instruction (<see cref="Address"/>, <see cref="Bytes"/>, <see cref="Text"/>), or a
/// heading above the first instruction of a source line (<see cref="Heading"/>: the method, or the file and line).
/// </summary>
public sealed record DisassemblyRow(string Address, string Bytes, string Text, bool IsCurrent, string? Heading = null)
{
    public bool IsHeading => Heading is not null;

    public bool IsInstruction => Heading is null;

    public string Marker => IsCurrent ? "▶" : string.Empty;

    public string AccessibleName => Heading ?? $"{Address} {Text}{(IsCurrent ? ", current" : string.Empty)}";
}

/// <summary>
/// The Disassembly window (M58, ADR 0094): the machine code of the selected frame's method under the Codalus debugger,
/// each source line's instructions under its line, the one the frame is at marked. Step Over and Step Into here move
/// one instruction.
/// </summary>
public sealed class DisassemblyViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.disassembly";

    /// <summary>The most instructions of a method shown.</summary>
    public const int MaxInstructions = 4000;

    private readonly DebugService _debugger;
    private readonly ShownRefresh _refresh;
    private string? _message;
    private int _currentIndex = -1;
    private int _generation;

    public DisassemblyViewModel(DebugService debugger)
    {
        _debugger = debugger;
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(_refresh.Request);
        debugger.FrameChanged += (_, _) => Ui.Post(_refresh.Request);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            OnPropertyChanged(nameof(CanStep));
            if (debugger.State == DebugState.Idle)
            {
                _generation++;
                Rows.Reset([]);
                Message = null;
                CurrentIndex = -1;
                OnPropertyChanged(nameof(IsEmpty));
            }
        });
    }

    public ResettableCollection<DisassemblyRow> Rows { get; } = [];

    void IShownAware.SetViewShown(object view, bool shown) => _refresh.SetShown(view, shown);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Why there's no code while the program is stopped; null otherwise.</summary>
    public string? Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    /// <summary>The row of the instruction the frame is at (the view scrolls to it); -1 when there's none.</summary>
    public int CurrentIndex
    {
        get => _currentIndex;
        private set => SetField(ref _currentIndex, value);
    }

    public bool CanStep => _debugger.State == DebugState.Stopped && _debugger.CanDisassemble;

    public Task StepOverAsync() => _debugger.StepInstructionAsync(into: false);

    public Task StepIntoAsync() => _debugger.StepInstructionAsync(into: true);

    public async Task RefreshAsync()
    {
        _refresh.Fresh();
        var generation = ++_generation;
        OnPropertyChanged(nameof(CanStep));
        if (_debugger.State != DebugState.Stopped)
        {
            return;
        }

        if (!_debugger.CanDisassemble)
        {
            Show([], -1, Localizer.Get("disassembly.needsCodalus", "Machine code is shown by the Codalus debugger (Options › Debugging › Debugger for .NET programs), on x64 and arm64."));
            return;
        }

        if (_debugger.SelectedFrame?.InstructionPointerReference is not { } ip)
        {
            Show([], -1, Localizer.Get("disassembly.noneHere", "This frame has no machine code to show (it isn't running .NET code)."));
            return;
        }

        // The whole method, from its start: the frame's instruction is somewhere in it.
        var instructions = await _debugger.DisassembleAsync(ip, -MaxInstructions, MaxInstructions).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        var rows = new List<DisassemblyRow>(instructions.Count + 16);
        var current = -1;
        foreach (var instruction in instructions)
        {
            if (instruction.Symbol is { } symbol)
            {
                rows.Add(new DisassemblyRow(string.Empty, string.Empty, string.Empty, false, symbol));
            }

            if (instruction.Line is { } line && instruction.Location is { } location)
            {
                rows.Add(new DisassemblyRow(string.Empty, string.Empty, string.Empty, false, string.Create(CultureInfo.InvariantCulture, $"{location.Name ?? Path.GetFileName(location.Path)}:{line}")));
            }

            var isCurrent = string.Equals(instruction.Address, ip, StringComparison.OrdinalIgnoreCase);
            if (isCurrent)
            {
                current = rows.Count;
            }

            rows.Add(new DisassemblyRow(instruction.Address, Spaced(instruction.InstructionBytes), instruction.Instruction, isCurrent));
        }

        Show(rows, current, rows.Count == 0 ? Localizer.Get("disassembly.unreadable", "The frame's machine code couldn't be read.") : null);
    }

    private void Show(IReadOnlyList<DisassemblyRow> rows, int current, string? message)
    {
        Rows.Reset(rows);
        Message = message;
        _currentIndex = -1;
        CurrentIndex = current;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>An instruction's bytes, two hex digits each with a space between.</summary>
    internal static string Spaced(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return string.Empty;
        }

        var text = new StringBuilder(hex.Length + (hex.Length / 2));
        for (var i = 0; i + 1 < hex.Length; i += 2)
        {
            text.Append(i == 0 ? string.Empty : " ").Append(hex, i, 2);
        }

        return text.ToString();
    }
}

/// <summary>Sixteen bytes of the Memory window: their address, in hex, and as text.</summary>
public sealed record MemoryRow(string Address, string Hex, string Text)
{
    public string AccessibleName => $"{Address}: {Hex}";
}

/// <summary>
/// The Memory window (M58, ADR 0094): the program's memory from an address under the Codalus debugger, sixteen bytes a
/// row. <see cref="AddressText"/> is an address (<c>0x…</c>) or an expression whose value is an object (its place in
/// memory) or a number (taken as an address). Bytes typed into <see cref="WriteText"/> are written at the address.
/// </summary>
public sealed class MemoryViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.memory";

    public const int BytesPerRow = 16;

    public const int RowsPerPage = 32;

    private readonly DebugService _debugger;
    private readonly ShownRefresh _refresh;
    private string _addressText = string.Empty;
    private string _writeText = string.Empty;
    private string? _message;
    private string? _reference;
    private long _offset;
    private int _generation;

    public MemoryViewModel(DebugService debugger)
    {
        _debugger = debugger;
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(_refresh.Request);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                _generation++;
                Rows.Reset([]);
                Message = null;
                OnPropertyChanged(nameof(IsEmpty));
            }
        });
    }

    public ResettableCollection<MemoryRow> Rows { get; } = [];

    void IShownAware.SetViewShown(object view, bool shown) => _refresh.SetShown(view, shown);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>An address or an expression: Enter reads the memory there.</summary>
    public string AddressText
    {
        get => _addressText;
        set => SetField(ref _addressText, value ?? string.Empty);
    }

    /// <summary>Hex bytes to write at the address shown (<c>5a 00</c>).</summary>
    public string WriteText
    {
        get => _writeText;
        set => SetField(ref _writeText, value ?? string.Empty);
    }

    public string? Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    /// <summary>Reads the memory at <see cref="AddressText"/>: an address as it is, an expression evaluated in the selected frame.</summary>
    public async Task GoAsync()
    {
        _offset = 0;
        _reference = null;
        await RefreshAsync().ConfigureAwait(true);
    }

    public Task NextPageAsync() => MoveAsync(BytesPerRow * RowsPerPage);

    public Task PreviousPageAsync() => MoveAsync(-BytesPerRow * RowsPerPage);

    private async Task MoveAsync(long by)
    {
        if (_reference is null)
        {
            return;
        }

        _offset += by;
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        _refresh.Fresh();
        var generation = ++_generation;
        if (_debugger.State != DebugState.Stopped || string.IsNullOrWhiteSpace(AddressText))
        {
            return;
        }

        if (!_debugger.CanReadMemory)
        {
            Show([], Localizer.Get("memory.needsCodalus", "Memory is shown by the Codalus debugger (Options › Debugging › Debugger for .NET programs)."));
            return;
        }

        // An expression is evaluated at each stop: the object it names may be another, or have moved.
        var reference = _reference is not null && IsAddress(AddressText) ? _reference : await ResolveAsync(AddressText.Trim()).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        if (reference is null)
        {
            Show([], Localizer.Format("memory.notAnAddress", "'{0}' isn't an address, nor an expression whose value is an object or a number.", AddressText.Trim()));
            return;
        }

        _reference = reference;
        var read = await _debugger.ReadMemoryAsync(reference, _offset, BytesPerRow * RowsPerPage).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        if (read?.Data is not { } data || !TryParse(read.Address, out var start))
        {
            Show([], Localizer.Format("memory.unreadable", "The memory at {0} can't be read.", read?.Address ?? reference));
            return;
        }

        var bytes = Convert.FromBase64String(data);
        var rows = new List<MemoryRow>((bytes.Length + BytesPerRow - 1) / BytesPerRow);
        for (var at = 0; at < bytes.Length; at += BytesPerRow)
        {
            var row = bytes.AsSpan(at, Math.Min(BytesPerRow, bytes.Length - at));
            var hex = new StringBuilder(BytesPerRow * 3);
            var text = new StringBuilder(BytesPerRow);
            foreach (var b in row)
            {
                hex.Append(b.ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
                text.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            }

            rows.Add(new MemoryRow("0x" + (start + (ulong)at).ToString("x16", CultureInfo.InvariantCulture), hex.ToString().TrimEnd(), text.ToString()));
        }

        Show(rows, null);
    }

    /// <summary>Writes <see cref="WriteText"/>'s bytes at the address shown, and reads the memory again.</summary>
    public async Task WriteAsync()
    {
        if (_reference is null || Rows.Count == 0)
        {
            Message = Localizer.Get("memory.goFirst", "Show the memory at an address first.");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(WriteText.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase));
        }
        catch (FormatException)
        {
            Message = Localizer.Get("memory.notHex", "Bytes are written as hex digits, two for each: 5a 00.");
            return;
        }

        if (bytes.Length == 0)
        {
            return;
        }

        if (await _debugger.WriteMemoryAsync(_reference, _offset, bytes).ConfigureAwait(true) is { } problem)
        {
            Message = problem;
            return;
        }

        WriteText = string.Empty;
        await RefreshAsync().ConfigureAwait(true);
    }

    private void Show(IReadOnlyList<MemoryRow> rows, string? message)
    {
        Rows.Reset(rows);
        Message = message;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static bool IsAddress(string text) => TryParse(text.Trim(), out _);

    private static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        return text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    /// <summary>An address as it is; an expression's object (where it is in memory) or its number.</summary>
    private async Task<string?> ResolveAsync(string text)
    {
        if (TryParse(text, out var address))
        {
            return "0x" + address.ToString("x", CultureInfo.InvariantCulture);
        }

        try
        {
            var value = await _debugger.EvaluateAsync(text, "watch").ConfigureAwait(true);
            if (value.MemoryReference is { } reference)
            {
                return reference;
            }

            return ulong.TryParse(value.Result, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? "0x" + number.ToString("x", CultureInfo.InvariantCulture)
                : TryParse(value.Result, out var hex) ? "0x" + hex.ToString("x", CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
