using System.Text;
using System.Text.Json;
using DaedalusStudio.Platform;
using DaedalusStudio.Workbench.Commands;

namespace DaedalusStudio.Workbench.Settings;

public sealed class SettingsChangedEventArgs(IReadOnlySet<string> keys, IReadOnlySet<string>? registered = null) : EventArgs
{
    public IReadOnlySet<string> Keys { get; } = keys;

    /// <summary>
    /// The subset of <see cref="Keys"/> that only appeared because their setting was registered (e.g. a plugin
    /// was added). Their values did not change, so they must not trigger <c>onSettingChanged</c> activation.
    /// </summary>
    public IReadOnlySet<string> Registered { get; } = registered ?? new HashSet<string>();

    /// <summary>True if <paramref name="keyOrSection"/> or any key below it (<c>section.</c>) changed.</summary>
    public bool Affects(string keyOrSection) =>
        Keys.Contains(keyOrSection) || Keys.Any(k => k.StartsWith(keyOrSection + ".", StringComparison.Ordinal));
}

/// <summary>
/// Layered settings: schema defaults &lt; user <c>settings.json</c> &lt; workspace <c>.daedalus/settings.json</c>.
/// Files are JSONC with flat dotted keys. Invalid or unknown values are ignored (and reported in
/// <see cref="Diagnostics"/>), so a typo never breaks the IDE. External edits are picked up live when
/// watching is enabled. Writes go through <see cref="JsoncEditor"/> and keep comments and formatting.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SettingDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly bool _watch;
    private readonly FileWatcher? _userWatcher;
    private FileWatcher? _workspaceWatcher;
    private Dictionary<string, JsonElement> _user = new(StringComparer.Ordinal);
    private Dictionary<string, JsonElement> _workspace = new(StringComparer.Ordinal);
    private Dictionary<string, JsonElement> _effective = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _diagnostics = [];

    public SettingsService(string userFile, string? workspaceFile = null, bool watch = false)
    {
        UserFile = userFile;
        WorkspaceFile = workspaceFile;
        _watch = watch;
        Reload();
        if (watch)
        {
            _userWatcher = new FileWatcher(userFile);
            _userWatcher.Changed += (_, _) => Reload();
            _workspaceWatcher = Watch(workspaceFile);
        }
    }

    public string UserFile { get; }

    /// <summary>The workspace layer (<c>.daedalus/settings.json</c> of the open solution), or null.</summary>
    public string? WorkspaceFile { get; private set; }

    /// <summary>Switches the workspace layer when a solution opens or closes; raises <see cref="Changed"/> for affected keys.</summary>
    public void SetWorkspaceFile(string? file)
    {
        _workspaceWatcher?.Dispose();
        WorkspaceFile = file;
        _workspaceWatcher = _watch ? Watch(file) : null;
        Reload();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The workspace layer appeared, disappeared or moved (a solution opened or closed).</summary>
    public event EventHandler? WorkspaceChanged;

    /// <summary>Watches <paramref name="file"/> if its folder exists (opening a solution must not create <c>.daedalus/</c>).</summary>
    private FileWatcher? Watch(string? file)
    {
        if (file is null || !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(file))))
        {
            return null;
        }

        var watcher = new FileWatcher(file);
        watcher.Changed += (_, _) => Reload();
        return watcher;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    public IReadOnlyList<SettingDescriptor> Descriptors
    {
        get
        {
            lock (_gate)
            {
                return [.. _descriptors.Values.OrderBy(d => d.DisplayCategory, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Order).ThenBy(d => d.Key, StringComparer.Ordinal)];
            }
        }
    }

    public SettingDescriptor? Find(string key)
    {
        lock (_gate)
        {
            return _descriptors.GetValueOrDefault(key);
        }
    }

    public void Register(IEnumerable<SettingDescriptor> descriptors) => Recompute(() =>
    {
        foreach (var descriptor in descriptors)
        {
            _descriptors[descriptor.Key] = descriptor;
        }
    });

    public void Unregister(ContributionSource source) => Recompute(() =>
    {
        foreach (var key in _descriptors.Where(d => d.Value.Source == source).Select(d => d.Key).ToList())
        {
            _descriptors.Remove(key);
        }
    });

    // ------------------------------------------------------------------ reading

    /// <summary>The effective value, or the schema default; <c>Undefined</c> for unknown keys.</summary>
    public JsonElement GetValue(string key)
    {
        lock (_gate)
        {
            return _effective.TryGetValue(key, out var value) ? value : default;
        }
    }

    /// <summary>The value set in one layer, if any.</summary>
    public JsonElement? GetLayerValue(string key, SettingScope scope)
    {
        lock (_gate)
        {
            var layer = scope == SettingScope.User ? _user : _workspace;
            return layer.TryGetValue(key, out var value) ? value : null;
        }
    }

    public bool IsModified(string key) => GetLayerValue(key, SettingScope.User) is not null || GetLayerValue(key, SettingScope.Workspace) is not null;

    public string GetString(string key, string fallback = "") =>
        GetValue(key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : fallback;

    public bool GetBoolean(string key, bool fallback = false) =>
        GetValue(key) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : fallback;

    public int GetInt32(string key, int fallback = 0) =>
        GetValue(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        GetValue(key) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : fallback;

    public IReadOnlyList<string> GetStrings(string key) =>
        GetValue(key) is { ValueKind: JsonValueKind.Array } v ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : [];

    /// <summary>All effective values (used to seed plugin hosts).</summary>
    public IReadOnlyDictionary<string, JsonElement> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, JsonElement>(_effective, StringComparer.Ordinal);
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Writes <paramref name="key"/> in the given layer (null removes it, i.e. resets to the inherited value).
    /// Throws <see cref="ArgumentException"/> if the value is invalid for the schema.
    /// </summary>
    public async Task SetAsync(string key, JsonElement? value, SettingScope scope, CancellationToken cancellationToken)
    {
        if (Find(key) is { } descriptor && value is { } v && descriptor.Validate(v) is { } error)
        {
            throw new ArgumentException($"{key}: {error}", nameof(value));
        }

        var file = scope == SettingScope.User ? UserFile : WorkspaceFile ?? throw new InvalidOperationException("No workspace is open.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : string.Empty;
            var updated = JsoncEditor.SetProperty(text, key, value is { } written ? Readable(written) : null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await File.WriteAllTextAsync(file, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        if (_watch && scope == SettingScope.Workspace)
        {
            _workspaceWatcher ??= Watch(WorkspaceFile);
        }

        Reload();
    }

    public Task SetAsync(string key, string value, SettingScope scope, CancellationToken cancellationToken) =>
        SetAsync(key, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.String), scope, cancellationToken);

    public Task SetAsync(string key, bool value, SettingScope scope, CancellationToken cancellationToken) =>
        SetAsync(key, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Boolean), scope, cancellationToken);

    public Task SetAsync(string key, double value, SettingScope scope, CancellationToken cancellationToken) =>
        SetAsync(key, JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Double), scope, cancellationToken);

    /// <summary>JSON for a file people edit: "a+b" stays "a+b" rather than "a+b".</summary>
    private static string Readable(JsonElement value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Re-reads both files and raises <see cref="Changed"/> for keys whose effective value changed.</summary>
    public void Reload()
    {
        var diagnostics = new List<string>();
        var user = ReadLayer(UserFile, diagnostics);
        var workspace = WorkspaceFile is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : ReadLayer(WorkspaceFile, diagnostics);
        Recompute(() =>
        {
            _user = user;
            _workspace = workspace;
        }, diagnostics);
    }

    private void Recompute(Action change, List<string>? fileDiagnostics = null)
    {
        HashSet<string> changed;
        HashSet<string> registered;
        lock (_gate)
        {
            change();
            var diagnostics = fileDiagnostics ?? _diagnostics.Where(d => d.StartsWith("[file]", StringComparison.Ordinal)).ToList();
            var effective = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, descriptor) in _descriptors)
            {
                effective[key] = descriptor.Default;
                foreach (var (layer, name) in new[] { (_user, "user"), (_workspace, "workspace") })
                {
                    if (!layer.TryGetValue(key, out var value))
                    {
                        continue;
                    }

                    if (layer == _workspace && descriptor.Scope == SettingScope.User)
                    {
                        diagnostics.Add($"{key}: can only be set in user settings.");
                    }
                    else if (descriptor.Validate(value) is { } error)
                    {
                        diagnostics.Add($"{key} ({name} settings): {error} Using the default.");
                    }
                    else
                    {
                        effective[key] = value;
                    }
                }
            }

            changed = [.. effective.Keys.Where(k => !_effective.TryGetValue(k, out var old) || old.GetRawText() != effective[k].GetRawText())];
            changed.UnionWith(_effective.Keys.Where(k => !effective.ContainsKey(k)));
            registered = [.. changed.Where(k => !_effective.ContainsKey(k))];
            _effective = effective;
            _diagnostics = diagnostics;
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(changed, registered));
        }
    }

    private static Dictionary<string, JsonElement> ReadLayer(string file, List<string> diagnostics)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return values;
        }

        try
        {
            var text = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(text))
            {
                return values;
            }

            using var document = JsonDocument.Parse(text, s_jsonc);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add($"[file] {file}: expected a JSON object.");
                return values;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"[file] {file}: {ex.Message}");
        }
        catch (IOException ex)
        {
            diagnostics.Add($"[file] {file}: {ex.Message}");
        }

        return values;
    }

    public void Dispose()
    {
        _userWatcher?.Dispose();
        _workspaceWatcher?.Dispose();
        _writeLock.Dispose();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(double))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class SettingsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
