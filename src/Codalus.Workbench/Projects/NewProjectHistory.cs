namespace Codalus.Workbench.Projects;

/// <summary>A recent template of New Project's list: its row id, and whether the user pinned it.</summary>
public sealed record RecentTemplate(string Id, bool Pinned);

/// <summary>
/// What New Project remembers (ADR 0082), in <c>&lt;state&gt;/new-project.json</c>: the recent templates (pinned first,
/// at most <see cref="Max"/> with the pinned ones), the recent locations (at most <see cref="Max"/>), the filters, Place
/// solution and project in the same directory, the framework per template, and the options' values by the key their
/// IDE host file gives (<see cref="TemplateParameter.PersistenceKey"/>). A missing or unreadable file reads as empty;
/// writes are atomic (<see cref="StateFile"/>).
/// </summary>
public sealed class NewProjectHistory(string file)
{
    public const int Max = 10;

    private readonly Lock _gate = new();
    private NewProjectState? _state;

    public event EventHandler? Changed;

    /// <summary>Pinned first (in the order they were pinned), then the others, most recent first.</summary>
    public IReadOnlyList<RecentTemplate> RecentTemplates
    {
        get
        {
            lock (_gate)
            {
                var entries = (Load().RecentTemplates ?? []).Select(e => new RecentTemplate(e.Id, e.Pinned == true)).ToList();
                return [.. entries.Where(e => e.Pinned), .. entries.Where(e => !e.Pinned)];
            }
        }
    }

    /// <summary>Recent locations, most recent first.</summary>
    public IReadOnlyList<string> RecentLocations
    {
        get
        {
            lock (_gate)
            {
                return [.. Load().RecentLocations ?? []];
            }
        }
    }

    public string? LanguageFilter => Read(s => s.LanguageFilter);

    public string? PlatformFilter => Read(s => s.PlatformFilter);

    public string? ProjectTypeFilter => Read(s => s.ProjectTypeFilter);

    public bool SameDirectory => Read(s => s.SameDirectory) ?? false;

    /// <summary>Uses a template: it goes first among the unpinned recents.</summary>
    public void AddRecentTemplate(string id) => Update(state =>
    {
        var list = (state.RecentTemplates ?? []).ToList();
        var pinned = list.Any(e => e.Id == id && e.Pinned == true);
        list.RemoveAll(e => e.Id == id);
        list.Insert(0, new RecentTemplateState(id, pinned ? true : null));
        var keep = Math.Max(0, Max - list.Count(e => e.Pinned == true));
        var unpinned = list.Where(e => e.Pinned != true).Skip(keep).ToList();
        list.RemoveAll(unpinned.Contains);
        return state with { RecentTemplates = list };
    });

    public void SetPinned(string id, bool pinned) => Update(state =>
    {
        var list = (state.RecentTemplates ?? []).ToList();
        var index = list.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            list.Insert(0, new RecentTemplateState(id, pinned ? true : null));
        }
        else
        {
            list[index] = list[index] with { Pinned = pinned ? true : null };
        }

        return state with { RecentTemplates = list };
    });

    public void RemoveRecentTemplate(string id) => Update(state => state with { RecentTemplates = [.. (state.RecentTemplates ?? []).Where(e => e.Id != id)] });

    /// <summary>A location used: it goes first, compared with the platform's case rules (<see cref="SolutionService.PathComparer"/>).</summary>
    public void AddRecentLocation(string location) => Update(state =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        var list = (state.RecentLocations ?? []).Where(l => !SolutionService.PathComparer.Equals(Path.TrimEndingDirectorySeparator(l), full)).ToList();
        list.Insert(0, full);
        return state with { RecentLocations = [.. list.Take(Max)] };
    });

    public void SetFilters(string? language, string? platform, string? projectType) =>
        Update(state => state.LanguageFilter == language && state.PlatformFilter == platform && state.ProjectTypeFilter == projectType
            ? state
            : state with { LanguageFilter = language, PlatformFilter = platform, ProjectTypeFilter = projectType });

    public void SetSameDirectory(bool value) => Update(state => state with { SameDirectory = value ? true : null });

    /// <summary>The framework last chosen for a template (its row id), or null.</summary>
    public string? Framework(string groupId) => Read(s => s.Frameworks?.GetValueOrDefault(groupId));

    public void SetFramework(string groupId, string? framework) => Update(state =>
    {
        var map = new Dictionary<string, string>(state.Frameworks ?? [], StringComparer.Ordinal);
        if (framework is null)
        {
            map.Remove(groupId);
        }
        else
        {
            map[groupId] = framework;
        }

        return state with { Frameworks = map };
    });

    /// <summary>
    /// A remembered value: <paramref name="persistenceKey"/> is <c>group:&lt;group&gt;</c>, <c>shared:&lt;scope&gt;</c> or
    /// <c>shared::&lt;symbol&gt;</c>, <paramref name="name"/> the symbol.
    /// </summary>
    public string? Value(string persistenceKey, string name) => Read(s => s.Values?.GetValueOrDefault(Key(persistenceKey, name)));

    public void SetValue(string persistenceKey, string name, string? value) => SetValues([(persistenceKey, name, value)]);

    /// <summary>Remembers several values in one write.</summary>
    public void SetValues(IEnumerable<(string PersistenceKey, string Name, string? Value)> values) => Update(state =>
    {
        var map = new Dictionary<string, string>(state.Values ?? [], StringComparer.Ordinal);
        foreach (var (key, name, value) in values)
        {
            if (value is null)
            {
                map.Remove(Key(key, name));
            }
            else
            {
                map[Key(key, name)] = value;
            }
        }

        return state with { Values = map };
    });

    /// <summary>
    /// Where New Project starts when nothing is open: the newest recent location, else <paramref name="setting"/>
    /// (<c>templates.defaultLocation</c>), else <c>source/repos</c> in the home folder.
    /// </summary>
    public string DefaultLocation(string? setting) =>
        RecentLocations is [var newest, ..] ? newest
        : (setting is { Length: > 0 } configured ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos"));

    private static string Key(string persistenceKey, string name) => persistenceKey + "/" + name;

    private T Read<T>(Func<NewProjectState, T> read)
    {
        lock (_gate)
        {
            return read(Load());
        }
    }

    /// <summary>Writes the change (a change that returns the same state writes nothing) and says so.</summary>
    private void Update(Func<NewProjectState, NewProjectState> change)
    {
        lock (_gate)
        {
            var current = Load();
            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                return;
            }

            _state = next;
            StateFile.Write(file, _state, StateJsonContext.Default.NewProjectState);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private NewProjectState Load() => _state ??= StateFile.Read(file, StateJsonContext.Default.NewProjectState) ?? new NewProjectState();
}

internal sealed record NewProjectState
{
    public IReadOnlyList<RecentTemplateState>? RecentTemplates { get; init; }

    public IReadOnlyList<string>? RecentLocations { get; init; }

    public string? LanguageFilter { get; init; }

    public string? PlatformFilter { get; init; }

    public string? ProjectTypeFilter { get; init; }

    public bool? SameDirectory { get; init; }

    public Dictionary<string, string>? Frameworks { get; init; }

    public Dictionary<string, string>? Values { get; init; }
}

internal sealed record RecentTemplateState(string Id, bool? Pinned);
