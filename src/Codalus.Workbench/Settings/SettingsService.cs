using System.Text;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Commands;

namespace Codalus.Workbench.Settings;

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
/// Layered settings: schema defaults &lt; user <c>settings.json</c> &lt; workspace <c>.codalus/settings.json</c>.
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

    /// <summary>Per language id: the valid values of its <c>"[id]": { … }</c> blocks, the workspace's over the user's.</summary>
    private Dictionary<string, Dictionary<string, JsonElement>> _languages = new(StringComparer.Ordinal);
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

    /// <summary>The workspace layer (<c>.codalus/settings.json</c> of the open solution), or null.</summary>
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

    /// <summary>Watches <paramref name="file"/> if its folder exists (opening a solution must not create <c>.codalus/</c>).</summary>
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

    /// <summary>
    /// Keys whose entry in a settings file appeared, went or changed while their effective value didn't (a value equal to
    /// the default removed by Reset, or one the other layer overrides): only what shows where a value is set needs them,
    /// such as the Settings editor's Modified mark. Keys whose effective value changed are in <see cref="Changed"/>.
    /// </summary>
    public event EventHandler<SettingsChangedEventArgs>? LayersChanged;

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

    /// <summary>The shell's settings in the display language (M23b; plugins translate theirs in their manifests).</summary>
    private static SettingDescriptor Localized(SettingDescriptor descriptor)
    {
        if (descriptor.Source != Commands.ContributionSource.Shell)
        {
            return descriptor;
        }

        var key = descriptor.Key;
        return descriptor with
        {
            Title = descriptor.Title is { } title ? Localization.Localizer.Get($"setting.{key}.title", title) : null,
            Description = descriptor.Description is { } description ? Localization.Localizer.Get($"setting.{key}.description", description) : null,
            Category = descriptor.Category is { } category ? Localization.Localizer.Get($"settingCategory.{category}", category) : null,
            EnumDescriptions = descriptor.TranslateEnumDescriptions && descriptor.EnumDescriptions is { } names && descriptor.Enum is { } values && names.Count == values.Count
                ? [.. names.Select((name, i) => Localization.Localizer.Get($"setting.{key}.enum.{values[i]}", name))]
                : descriptor.EnumDescriptions,
        };
    }

    public void Register(IEnumerable<SettingDescriptor> descriptors) => Recompute(() =>
    {
        foreach (var descriptor in descriptors)
        {
            _descriptors[descriptor.Key] = Localized(descriptor);
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

    /// <summary>
    /// The settings as seen from documents of <paramref name="languageId"/>: a value in a <c>"[languageId]"</c> block of
    /// either file (VS Code's language-specific settings, e.g. <c>"[csharp]": { "editor.tabSize": 2 }</c>) wins over the
    /// general value, in any layer. A null or empty id reads the general values.
    /// </summary>
    public LanguageSettings ForLanguage(string? languageId) => new(this, languageId);

    /// <summary>The language ids that have a <c>"[id]"</c> block in either file.</summary>
    public IReadOnlyCollection<string> OverriddenLanguages
    {
        get
        {
            lock (_gate)
            {
                return [.. _languages.Keys];
            }
        }
    }

    /// <summary>The value for documents of <paramref name="languageId"/>: its language-specific value, else the effective one.</summary>
    public JsonElement GetValue(string key, string? languageId)
    {
        lock (_gate)
        {
            return languageId is { Length: > 0 } && _languages.TryGetValue(languageId, out var values) && values.TryGetValue(key, out var value)
                ? value
                : _effective.TryGetValue(key, out var general) ? general : default;
        }
    }

    /// <summary>The value one layer's <c>"[languageId]"</c> block sets, if any.</summary>
    public JsonElement? GetLanguageLayerValue(string key, string languageId, SettingScope scope)
    {
        lock (_gate)
        {
            var layer = scope == SettingScope.User ? _user : _workspace;
            return LanguageBlocks(layer).TryGetValue(languageId, out var block) && block.TryGetValue(key, out var value) ? value : null;
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

    /// <summary>
    /// The settings under <c>section.</c> as one object, nested at the dots (<c>ini.lint.enabled</c> →
    /// <c>{ "lint": { "enabled": … } }</c>), as LSP's <c>workspace/configuration</c> expects; null when none exist.
    /// </summary>
    public JsonElement? GetSection(string section)
    {
        var prefix = section + ".";
        var root = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in Snapshot().Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var parts = key[prefix.Length..].Split('.');
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (node[part] is not System.Text.Json.Nodes.JsonObject child)
                {
                    child = [];
                    node[part] = child;
                }

                node = child;
            }

            node[parts[^1]] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
        }

        if (root.Count == 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

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
    public Task SetAsync(string key, JsonElement? value, SettingScope scope, CancellationToken cancellationToken)
    {
        if (Find(key) is { } descriptor && value is { } v && descriptor.Validate(v) is { } error)
        {
            throw new ArgumentException($"{key}: {error}", nameof(value));
        }

        return SetRawAsync(key, value, scope, cancellationToken);
    }

    private async Task SetRawAsync(string key, JsonElement? value, SettingScope scope, CancellationToken cancellationToken)
    {
        var file = scope == SettingScope.User ? UserFile : WorkspaceFile ?? throw new InvalidOperationException("No workspace is open.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = File.Exists(file) ? ReadShared(file) : string.Empty;
            var updated = JsoncEditor.SetProperty(text, key, value is { } written ? Readable(written) : null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            // The watcher's reload (or another program, an indexer, an antivirus) may be reading the file: on Windows that
            // is a sharing violation for the writer, which passes in moments.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(file, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (IOException) when (attempt < 20 && OperatingSystem.IsWindows())
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
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

    /// <summary>
    /// Writes <paramref name="key"/> in the <c>"[languageId]"</c> block of a layer (null removes it, and an empty block
    /// with it). Throws <see cref="ArgumentException"/> if the value is invalid for the schema.
    /// </summary>
    public Task SetAsync(string key, JsonElement? value, SettingScope scope, string languageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(languageId);
        if (Find(key) is { } descriptor && value is { } v && descriptor.Validate(v) is { } error)
        {
            throw new ArgumentException($"{key}: {error}", nameof(value));
        }

        var block = new System.Text.Json.Nodes.JsonObject();
        if (GetLayerValue(LanguageKey(languageId), scope) is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var property in existing.EnumerateObject())
            {
                block[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            }
        }

        if (value is { } written)
        {
            block[key] = System.Text.Json.Nodes.JsonNode.Parse(written.GetRawText());
        }
        else
        {
            block.Remove(key);
        }

        JsonElement? updated = null;
        if (block.Count > 0)
        {
            using var document = JsonDocument.Parse(block.ToJsonString());
            updated = document.RootElement.Clone();
        }

        return SetRawAsync(LanguageKey(languageId), updated, scope, cancellationToken);
    }

    /// <summary>Adds an empty <c>"[languageId]": { }</c> block to a layer, unless a block for the language is there.</summary>
    public Task AddLanguageBlockAsync(string languageId, SettingScope scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(languageId);
        lock (_gate)
        {
            if ((scope == SettingScope.User ? _user : _workspace).Keys.Any(k => LanguageIds(k)?.Contains(languageId, StringComparer.Ordinal) == true))
            {
                return Task.CompletedTask;
            }
        }

        using var empty = JsonDocument.Parse("{}");
        return SetRawAsync(LanguageKey(languageId), empty.RootElement.Clone(), scope, cancellationToken);
    }

    /// <summary>The key of a language's block: <c>[csharp]</c>.</summary>
    public static string LanguageKey(string languageId) => $"[{languageId}]";

    /// <summary>
    /// The language ids of a <c>"[…]"</c> key: <c>[csharp]</c>, or several as in VS Code, <c>[csharp][vb]</c>; null for
    /// other keys.
    /// </summary>
    public static IReadOnlyList<string>? LanguageIds(string key)
    {
        if (key.Length < 3 || key[0] != '[' || key[^1] != ']')
        {
            return null;
        }

        var ids = key[1..^1].Split("][", StringSplitOptions.TrimEntries);
        return ids.All(id => id.Length > 0 && !id.Contains('[', StringComparison.Ordinal) && !id.Contains(']', StringComparison.Ordinal)) ? ids : null;
    }

    /// <summary>A layer's language blocks, by language id (a later block for the same language adds to an earlier one).</summary>
    private static Dictionary<string, Dictionary<string, JsonElement>> LanguageBlocks(Dictionary<string, JsonElement> layer)
    {
        var blocks = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var (key, value) in layer)
        {
            if (value.ValueKind != JsonValueKind.Object || LanguageIds(key) is not { } ids)
            {
                continue;
            }

            foreach (var id in ids)
            {
                if (!blocks.TryGetValue(id, out var block))
                {
                    blocks[id] = block = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                }

                foreach (var property in value.EnumerateObject())
                {
                    block[property.Name] = property.Value;
                }
            }
        }

        return blocks;
    }

    /// <summary>Reads a settings file without keeping a writer out (a reload runs while a setting is being written).</summary>
    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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
        HashSet<string> layers;
        lock (_gate)
        {
            var (user, workspace) = (_user, _workspace);
            change();
            layers = [.. LayerChanges(user, _user).Concat(LayerChanges(workspace, _workspace))];
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

            // Language blocks: valid values of registered settings, the workspace's over the user's.
            var languages = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
            foreach (var (layer, name) in new[] { (_user, "user"), (_workspace, "workspace") })
            {
                foreach (var (language, block) in LanguageBlocks(layer))
                {
                    foreach (var (key, value) in block)
                    {
                        if (!_descriptors.TryGetValue(key, out var descriptor))
                        {
                            continue;
                        }

                        if (layer == _workspace && descriptor.Scope == SettingScope.User)
                        {
                            diagnostics.Add($"{LanguageKey(language)} {key}: can only be set in user settings.");
                        }
                        else if (descriptor.Validate(value) is { } error)
                        {
                            diagnostics.Add($"{LanguageKey(language)} {key} ({name} settings): {error} Using the general value.");
                        }
                        else
                        {
                            if (!languages.TryGetValue(language, out var values))
                            {
                                languages[language] = values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                            }

                            values[key] = value;
                        }
                    }
                }
            }

            changed = [.. effective.Keys.Where(k => !_effective.TryGetValue(k, out var old) || old.GetRawText() != effective[k].GetRawText())];
            changed.UnionWith(_effective.Keys.Where(k => !effective.ContainsKey(k)));
            registered = [.. changed.Where(k => !_effective.ContainsKey(k))];
            foreach (var language in languages.Keys.Union(_languages.Keys))
            {
                var before = _languages.GetValueOrDefault(language) ?? [];
                var after = languages.GetValueOrDefault(language) ?? [];
                foreach (var key in before.Keys.Union(after.Keys).Where(k => !(before.TryGetValue(k, out var b) && after.TryGetValue(k, out var a) && b.GetRawText() == a.GetRawText())))
                {
                    changed.Add(key); // a language's value of the setting changed
                    changed.Add(LanguageKey(language));
                }
            }

            _effective = effective;
            _languages = languages;
            _diagnostics = diagnostics;
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(this, new SettingsChangedEventArgs(changed, registered));
        }

        layers.ExceptWith(changed);
        if (layers.Count > 0)
        {
            LayersChanged?.Invoke(this, new SettingsChangedEventArgs(layers));
        }
    }

    /// <summary>The keys a settings file added, removed or set to another value.</summary>
    private static IEnumerable<string> LayerChanges(Dictionary<string, JsonElement> before, Dictionary<string, JsonElement> after) =>
        before.Keys.Union(after.Keys).Where(k => !(before.TryGetValue(k, out var b) && after.TryGetValue(k, out var a) && b.GetRawText() == a.GetRawText()));

    private static Dictionary<string, JsonElement> ReadLayer(string file, List<string> diagnostics)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return values;
        }

        try
        {
            var text = ReadShared(file);
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
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>The settings as documents of one language see them (<see cref="SettingsService.ForLanguage"/>).</summary>
public readonly struct LanguageSettings(SettingsService settings, string? languageId)
{
    public string? LanguageId => languageId;

    public JsonElement GetValue(string key) => settings.GetValue(key, languageId);

    public string GetString(string key, string fallback = "") =>
        GetValue(key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : fallback;

    public bool GetBoolean(string key, bool fallback = false) =>
        GetValue(key) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : fallback;

    public int GetInt32(string key, int fallback = 0) =>
        GetValue(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : fallback;

    public IReadOnlyList<string> GetStrings(string key) =>
        GetValue(key) is { ValueKind: JsonValueKind.Array } v ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : [];
}
