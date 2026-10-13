using System.Text;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Context;

namespace Codalus.Workbench.Keybindings;

public enum KeybindingSource
{
    Default,
    Plugin,
    User,
}

/// <summary>
/// A key binding rule. A user rule whose command starts with <c>-</c> removes matching default or plugin rules
/// (same command; same keys if keys are given), the same convention as VS Code.
/// </summary>
public sealed record KeybindingRule(KeySequence? Keys, string Command, WhenClause When, KeybindingSource Source)
{
    public JsonElement? Args { get; init; }

    public string? SourceId { get; init; }

    public bool IsRemoval => Command.StartsWith('-');
}

public enum KeyDispatchKind
{
    NoMatch,

    /// <summary>The chord is the prefix of a multi-chord binding; wait for the next chord.</summary>
    Pending,
    Command,
}

public readonly record struct KeyDispatchResult(KeyDispatchKind Kind, KeybindingRule? Rule = null, KeySequence? Pending = null);

/// <summary>Resolves key presses to commands and owns the effective set of key bindings.</summary>
public sealed class KeybindingService(IContextKeyReader context, PlatformKind platform)
{
    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Lock _gate = new();
    private IReadOnlyList<KeybindingRule> _defaults = [];
    private readonly Dictionary<string, IReadOnlyList<KeybindingRule>> _plugins = new(StringComparer.Ordinal);
    private IReadOnlyList<KeybindingRule> _user = [];
    private List<KeybindingRule> _effective = [];
    private List<KeyChord> _pending = [];

    public PlatformKind Platform => platform;

    public event EventHandler? Changed;

    public IReadOnlyList<KeybindingRule> EffectiveRules
    {
        get
        {
            lock (_gate)
            {
                return _effective;
            }
        }
    }

    public IReadOnlyList<KeybindingRule> UserRules
    {
        get
        {
            lock (_gate)
            {
                return _user;
            }
        }
    }

    public void SetDefaults(IReadOnlyList<KeybindingRule> rules) => Update(() => _defaults = rules);

    public void SetPluginRules(string pluginId, IReadOnlyList<KeybindingRule> rules) => Update(() => _plugins[pluginId] = rules);

    public void RemovePluginRules(string pluginId) => Update(() => _plugins.Remove(pluginId));

    public void SetUserRules(IReadOnlyList<KeybindingRule> rules) => Update(() => _user = rules);

    private void Update(Action change)
    {
        lock (_gate)
        {
            change();
            var rules = new List<KeybindingRule>(_defaults);
            foreach (var plugin in _plugins.Values)
            {
                rules.AddRange(plugin);
            }

            foreach (var rule in _user)
            {
                if (rule.IsRemoval)
                {
                    var command = rule.Command[1..];
                    rules.RemoveAll(r => r.Command == command && (rule.Keys is null || Equals(r.Keys, rule.Keys)));
                }
                else
                {
                    rules.Add(rule);
                }
            }

            _effective = rules.Where(r => r.Keys is not null).ToList();
            _pending = [];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Feeds one key press. Later rules win over earlier ones (user over plugin over default), and only rules
    /// whose when-clause holds are considered.
    /// </summary>
    public KeyDispatchResult Dispatch(KeyChord chord)
    {
        lock (_gate)
        {
            var sequence = new List<KeyChord>(_pending) { chord };
            KeybindingRule? exact = null;
            var prefix = false;
            foreach (var rule in _effective)
            {
                var keys = rule.Keys!.Chords;
                if (keys.Count < sequence.Count || !StartsWith(keys, sequence) || !rule.When.Evaluate(context))
                {
                    continue;
                }

                if (keys.Count == sequence.Count)
                {
                    exact = rule;
                }
                else
                {
                    prefix = true;
                }
            }

            if (exact is not null)
            {
                _pending = [];
                return new KeyDispatchResult(KeyDispatchKind.Command, exact);
            }

            if (prefix)
            {
                _pending = sequence;
                return new KeyDispatchResult(KeyDispatchKind.Pending, Pending: new KeySequence(sequence));
            }

            var wasPending = _pending.Count > 0;
            _pending = [];

            // A chord that doesn't continue a pending sequence may still start a new one.
            return wasPending ? Dispatch(chord) : new KeyDispatchResult(KeyDispatchKind.NoMatch);
        }
    }

    /// <summary>
    /// The single-key rule a press would run right now, without touching chord state (a focused terminal asks first
    /// whether a key belongs to the IDE).
    /// </summary>
    public KeyDispatchResult Peek(KeyChord chord)
    {
        lock (_gate)
        {
            KeybindingRule? exact = null;
            foreach (var rule in _effective)
            {
                var keys = rule.Keys!.Chords;
                if (keys.Count == 1 && keys[0] == chord && rule.When.Evaluate(context))
                {
                    exact = rule;
                }
            }

            return exact is null ? new KeyDispatchResult(KeyDispatchKind.NoMatch) : new KeyDispatchResult(KeyDispatchKind.Command, exact);
        }
    }

    public void ResetChord()
    {
        lock (_gate)
        {
            _pending = [];
        }
    }

    /// <summary>The binding shown next to a command in menus and the palette (the highest-precedence one).</summary>
    public KeySequence? GetBinding(string commandId)
    {
        lock (_gate)
        {
            for (var i = _effective.Count - 1; i >= 0; i--)
            {
                if (_effective[i].Command == commandId)
                {
                    return _effective[i].Keys;
                }
            }

            return null;
        }
    }

    /// <summary>Groups of rules that share keys and when-clause but run different commands.</summary>
    public IReadOnlyList<IReadOnlyList<KeybindingRule>> FindConflicts()
    {
        lock (_gate)
        {
            return _effective
                .GroupBy(r => (r.Keys!.ToString(), r.When.Text))
                .Where(g => g.Select(r => r.Command).Distinct(StringComparer.Ordinal).Count() > 1)
                .Select(g => (IReadOnlyList<KeybindingRule>)g.ToList())
                .ToList();
        }
    }

    private static bool StartsWith(IReadOnlyList<KeyChord> keys, List<KeyChord> sequence)
    {
        for (var i = 0; i < sequence.Count; i++)
        {
            if (keys[i] != sequence[i])
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ JSON (defaults, plugins, keybindings.json)

    /// <summary>
    /// Parses a JSONC array of <c>{ "key", "command", "when"?, "args"?, "mac"?, "linux"?, "win"? }</c>.
    /// Platform-specific keys override <c>key</c>. Invalid entries are skipped and reported.
    /// </summary>
    public static IReadOnlyList<KeybindingRule> ParseRules(string json, KeybindingSource source, PlatformKind platform, string? sourceId, ICollection<string>? diagnostics)
    {
        var rules = new List<KeybindingRule>();
        using var document = JsonDocument.Parse(json.Length == 0 ? "[]" : json, s_jsonc);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics?.Add("Key bindings must be a JSON array.");
            return rules;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (TryParseRule(item, source, platform, sourceId, diagnostics) is { } rule)
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    public static KeybindingRule? TryParseRule(JsonElement item, KeybindingSource source, PlatformKind platform, string? sourceId, ICollection<string>? diagnostics)
    {
        string? Str(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var command = Str("command");
        if (string.IsNullOrWhiteSpace(command))
        {
            diagnostics?.Add($"Key binding without a command: {item.GetRawText()}");
            return null;
        }

        var platformKey = platform switch
        {
            PlatformKind.MacOS => Str("mac"),
            PlatformKind.Linux => Str("linux"),
            _ => Str("win"),
        };
        var keyText = platformKey ?? Str("key");
        try
        {
            var keys = string.IsNullOrWhiteSpace(keyText) ? null : KeySequence.Parse(keyText, platform);
            if (keys is null && !command.StartsWith('-'))
            {
                diagnostics?.Add($"Key binding for '{command}' has no key.");
                return null;
            }

            return new KeybindingRule(keys, command, WhenClause.Parse(Str("when")), source)
            {
                Args = item.TryGetProperty("args", out var args) ? args.Clone() : null,
                SourceId = sourceId,
            };
        }
        catch (FormatException ex)
        {
            diagnostics?.Add(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Rebinds <paramref name="commandId"/> for the user: removes earlier user rules for it, hides default/plugin
    /// rules with <c>-command</c> entries and adds the new binding (none if <paramref name="keys"/> is null).
    /// Returns the new keybindings.json content.
    /// </summary>
    public string RebindForUser(string commandId, KeySequence? keys)
    {
        List<KeybindingRule> user;
        lock (_gate)
        {
            user = _user.Where(r => r.Command != commandId && r.Command != "-" + commandId).ToList();
            var inherited = _defaults.Concat(_plugins.Values.SelectMany(p => p)).Where(r => r.Command == commandId).ToList();
            foreach (var rule in inherited)
            {
                user.Add(new KeybindingRule(rule.Keys, "-" + commandId, rule.When, KeybindingSource.User));
            }

            if (keys is not null)
            {
                user.Add(new KeybindingRule(keys, commandId, WhenClause.Always, KeybindingSource.User));
            }
        }

        SetUserRules(user);
        return Serialize(user);
    }

    /// <summary>Restores the default and plugin bindings of a command by dropping the user's rules for it.</summary>
    public string ResetForUser(string commandId)
    {
        List<KeybindingRule> user;
        lock (_gate)
        {
            user = _user.Where(r => r.Command != commandId && r.Command != "-" + commandId).ToList();
        }

        SetUserRules(user);
        return Serialize(user);
    }

    public static string Serialize(IEnumerable<KeybindingRule> rules)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,

            // A file people edit: keep "ctrl+k" readable instead of "ctrl+k".
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartArray();
            foreach (var rule in rules)
            {
                writer.WriteStartObject();
                if (rule.Keys is not null)
                {
                    writer.WriteString("key", rule.Keys.ToString());
                }

                writer.WriteString("command", rule.Command);
                if (rule.When.Text != WhenClause.Always.Text)
                {
                    writer.WriteString("when", rule.When.Text);
                }

                if (rule.Args is { } args)
                {
                    writer.WritePropertyName("args");
                    args.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return "// Codalus IDE key bindings. Entries override the defaults; \"-command\" removes a default binding.\n"
               + Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }
}
