using System.Xml;
using Codalus.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Globbing;

namespace Codalus.Projects.Host.ProjectManagement;

/// <summary>
/// Item edits for the Properties window, Show All Files, linked files and moves between projects (ADR 0033). An edit is
/// applied to the project file's construction model, the project is evaluated again in memory with the edited XML, and
/// the edit is adjusted until the evaluated items are what was asked: a file a glob includes gets an <c>Update</c> item for
/// its metadata, a type change under a glob writes <c>&lt;Old Remove&gt;</c> and <c>&lt;New Include&gt;</c>, and a value that equals
/// what the project would evaluate to without its own definition removes that definition (the minimal change).
/// Only unconditioned items in top-level item groups are edited; imports are never edited.
/// </summary>
internal sealed class ProjectItems : IDisposable
{
    private static readonly StringComparison s_pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Build actions every project offers, first in the list.</summary>
    private static readonly string[] s_commonItemTypes = ["None", "Compile", "Content", "EmbeddedResource", "AdditionalFiles"];

    /// <summary>Item types that name files of the project (besides those in <c>AvailableItemName</c>).</summary>
    private static readonly HashSet<string> s_fileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "None", "Content", "EmbeddedResource", "Page", "ApplicationDefinition", "Resource",
        "AdditionalFiles", "AvaloniaResource", "AvaloniaXaml", "Protobuf", "TypeScriptCompile",
    };

    /// <summary>Item types that aren't files of the project (references, usings, folders).</summary>
    private static readonly HashSet<string> s_nonFileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PackageReference", "ProjectReference", "FrameworkReference", "Reference", "PackageVersion", "Using", "Analyzer", "Folder",
    };

    /// <summary>Metadata the Properties window always shows for a file.</summary>
    internal static readonly string[] WellKnownMetadata =
        [ItemMetadataNames.CopyToOutputDirectory, ItemMetadataNames.Generator, ItemMetadataNames.CustomToolNamespace];

    /// <summary>Folders never listed or included (version control, IDE state, packages).</summary>
    private static readonly HashSet<string> s_skippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", ".idea", "node_modules", "bin", "obj" };

    private readonly ProjectCollection _collection;
    private readonly Dictionary<string, string> _globals;
    private readonly string _before;
    private readonly string _directory;
    private readonly Project _project;

    private ProjectItems(string path, IReadOnlyDictionary<string, string> globals)
    {
        Path = path;
        _directory = System.IO.Path.GetDirectoryName(path)!;
        _globals = new Dictionary<string, string>(globals, StringComparer.OrdinalIgnoreCase);
        _collection = new ProjectCollection(_globals);
        Xml = ProjectRootElement.Open(path, _collection, preserveFormatting: true);
        Xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true); // always the file on disk
        _before = Xml.RawXml; // Reload marks the element dirty, so a change is a different text, not HasUnsavedChanges
        _project = new Project(Xml, _globals, null, _collection);

        // A multi-targeted project's default items (Compile globs…) come with a target framework.
        if (_project.GetPropertyValue("TargetFramework").Length == 0 && ProjectEvaluator.TargetFrameworks(_project) is [var first, ..])
        {
            _globals["TargetFramework"] = first;
            _project = new Project(Xml, _globals, null, _collection);
        }
    }

    public string Path { get; }

    public ProjectRootElement Xml { get; }

    public bool Changed => Xml.RawXml != _before;

    /// <summary>Whether the order of <c>Compile</c> items is the compile order (F#, M36).</summary>
    public bool IsOrdered => _project.GetPropertyValue("DefaultLanguageSourceExtension").Equals(".fs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a project file for item edits, evaluated with <paramref name="globals"/> (the active configuration).</summary>
    public static ProjectItems Open(string projectPath, IReadOnlyDictionary<string, string> globals) => new(projectPath, globals);

    /// <summary>Writes the project file if an edit changed it. Returns whether it did.</summary>
    public bool Save()
    {
        if (!Changed)
        {
            return false;
        }

        Xml.Save();
        return true;
    }

    public void Dispose() => _collection.Dispose();

    // ------------------------------------------------------------------ reading

    /// <summary>The file as the Properties window shows it.</summary>
    public ItemInfoResult Describe(string file)
    {
        file = Normalize(file);
        var items = ItemsFor(file).ToList();
        var primary = items.FirstOrDefault();
        var elements = ElementsNaming(Xml, file, includeConditioned: true, includeWildcards: true)
            .Select(e => new ItemElementInfo(Operation(e), e.ItemType, Value(e), e.Location.Line, Condition(e)))
            .ToArray();

        var metadata = new List<ItemMetadataInfo>();
        if (primary is not null)
        {
            var names = new List<string>(WellKnownMetadata);
            if (IsLinked(file, primary))
            {
                names.Add(ItemMetadataNames.Link);
            }

            names.AddRange(primary.DirectMetadata.Select(m => m.Name).Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)));
            foreach (var name in names)
            {
                var direct = primary.DirectMetadata.LastOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Xml is not null && IsThisProject(m.Xml.ContainingProject));
                metadata.Add(new ItemMetadataInfo(name, primary.GetMetadataValue(name), direct?.UnevaluatedValue));
            }
        }

        return new ItemInfoResult(
            Path,
            file,
            primary?.ItemType,
            primary is not null && IsLinked(file, primary),
            primary is null ? null : NullIfEmpty(primary.GetMetadataValue(ItemMetadataNames.Link)),
            primary?.Xml is { } origin && HasWildcard(origin.Include),
            elements,
            [.. metadata],
            ItemTypes());
    }

    /// <summary>The Build Action choices: the common item types, then the project's <c>AvailableItemName</c>s.</summary>
    public string[] ItemTypes()
    {
        var available = _project.GetItems("AvailableItemName").Select(i => i.EvaluatedInclude)
            .Where(t => t.Length > 0 && !s_nonFileItemTypes.Contains(t) && !t.StartsWith('_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !s_commonItemTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        return [.. s_commonItemTypes, .. available];
    }

    /// <summary>The file's items of file types, in evaluation order (the first is the one the Properties window edits).</summary>
    private IEnumerable<ProjectItem> ItemsFor(string file) =>
        _project.Items.Where(i => IsFileItemType(i.ItemType) && i.EvaluatedInclude.Length > 0 && SamePath(Full(i.EvaluatedInclude), file));

    private bool IsItem(string file, string itemType) =>
        _project.GetItems(itemType).Any(i => i.EvaluatedInclude.Length > 0 && SamePath(Full(i.EvaluatedInclude), file));

    private bool IsFileItemType(string itemType) =>
        s_fileItemTypes.Contains(itemType)
        || (!s_nonFileItemTypes.Contains(itemType) && _project.GetItems("AvailableItemName").Any(a => a.EvaluatedInclude.Equals(itemType, StringComparison.OrdinalIgnoreCase)));

    private bool IsLinked(string file, ProjectItem item) =>
        !IsUnder(file, _directory) || item.GetMetadataValue(ItemMetadataNames.Link).Length > 0;

    private bool IsThisProject(ProjectRootElement element) => string.Equals(element.FullPath, Path, s_pathComparison);

    // ------------------------------------------------------------------ build action and metadata

    /// <summary>
    /// Gives the file the item type <paramref name="itemType"/> (null: keep it) and the metadata (null value: the
    /// inherited value). A file that isn't an item yet is included first.
    /// </summary>
    public void Set(string file, string? itemType, IReadOnlyDictionary<string, string?>? metadata)
    {
        file = Normalize(file);
        var current = ItemsFor(file).FirstOrDefault();
        var type = itemType is { Length: > 0 } ? itemType : current?.ItemType
                   ?? throw new InvalidOperationException($"{System.IO.Path.GetFileName(file)} is not an item of {System.IO.Path.GetFileName(Path)}.");
        var carried = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (current is null)
        {
            Include(file, type, link: null);
        }
        else if (!current.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase))
        {
            ChangeType(file, current, type, carried);
        }

        foreach (var (name, value) in carried.Where(c => metadata?.ContainsKey(c.Key) != true))
        {
            SetMetadata(file, type, name, value);
        }

        foreach (var (name, value) in metadata ?? new Dictionary<string, string?>())
        {
            SetMetadata(file, type, name, value);
        }
    }

    /// <summary>
    /// Moves the file from <paramref name="current"/>'s type to <paramref name="type"/>: its explicit items of the old type
    /// go (their metadata is carried over), a glob that still includes it gets a <c>Remove</c>, and it is included as the
    /// new type unless a glob already does.
    /// </summary>
    private void ChangeType(string file, ProjectItem current, string type, Dictionary<string, string> carried)
    {
        var oldType = current.ItemType;
        var link = IsUnder(file, _directory) ? null : NullIfEmpty(current.GetMetadataValue(ItemMetadataNames.Link));
        foreach (var element in ExactElements(Xml, file, oldType).Where(e => e.Include.Length > 0 || e.Update.Length > 0).ToList())
        {
            foreach (var m in element.Metadata.Where(m => m.Condition.Length == 0))
            {
                carried[m.Name] = m.Value;
            }

            RemoveElement(element);
        }

        carried.Remove(ItemMetadataNames.Link);
        Reevaluate();
        if (IsItem(file, oldType))
        {
            AddElement(oldType, ItemOperations.Remove, Relative(file));
            Reevaluate();
        }

        if (!IsItem(file, type))
        {
            foreach (var remove in ExactElements(Xml, file, type).Where(e => e.Remove.Length > 0).ToList())
            {
                RemoveElement(remove);
            }

            Reevaluate();
            if (!IsItem(file, type))
            {
                Include(file, type, link);
            }
        }
    }

    /// <summary>
    /// Sets one metadata value of the file's <paramref name="type"/> item (null: removes the project file's definitions).
    /// A value that equals what the project would evaluate to without the project file's own definitions removes them.
    /// </summary>
    private void SetMetadata(string file, string type, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new ArgumentException($"'{name}' is not a valid metadata name.");
        }

        var definitions = Definitions(Xml, file, type, name).ToList();
        if (value is not null && definitions.Count > 0 && definitions[^1].Value == value)
        {
            return;
        }

        if (value is null || SameMetadataValue(name, InheritedValue(file, type, name), value))
        {
            RemoveDefinitions(Xml, file, type, name);
            Reevaluate();
            return;
        }

        if (definitions.Count > 0)
        {
            definitions[^1].Value = value; // the last definition wins in MSBuild
        }
        else
        {
            var elements = ExactElements(Xml, file, type).Where(e => e.Include.Length > 0 || e.Update.Length > 0).ToList();
            var target = elements.FirstOrDefault(e => e.Include.Length > 0) ?? elements.LastOrDefault()
                         ?? AddElement(type, ItemOperations.Update, Relative(file));
            target.AddMetadata(name, value);
        }

        Reevaluate();
    }

    /// <summary>Whether two values of a metadata mean the same: an empty <c>CopyToOutputDirectory</c> is <c>Never</c>.</summary>
    private static bool SameMetadataValue(string name, string inherited, string value)
    {
        if (name.Equals(ItemMetadataNames.CopyToOutputDirectory, StringComparison.OrdinalIgnoreCase))
        {
            static string Normalize(string v) => v.Trim().Length == 0 ? "Never" : v.Trim();
            return Normalize(inherited).Equals(Normalize(value), StringComparison.OrdinalIgnoreCase);
        }

        return inherited == value;
    }

    /// <summary>The metadata value the file would have without the project file's own definitions of it.</summary>
    private string InheritedValue(string file, string type, string name)
    {
        var copy = ProjectRootElement.Create(XmlReader.Create(new StringReader(Xml.RawXml)), _collection, preserveFormatting: false);
        copy.FullPath = System.IO.Path.Combine(_directory, $"~{Guid.NewGuid():N}{System.IO.Path.GetExtension(Path)}");
        RemoveDefinitions(copy, file, type, name);
        var trial = new Project(copy, _globals, null, _collection);
        try
        {
            return trial.GetItems(type).FirstOrDefault(i => SamePath(Full(i.EvaluatedInclude), file))?.GetMetadataValue(name) ?? string.Empty;
        }
        finally
        {
            _collection.UnloadProject(trial);
            _collection.TryUnloadProject(copy);
        }
    }

    private IEnumerable<ProjectMetadataElement> Definitions(ProjectRootElement xml, string file, string type, string name) =>
        ExactElements(xml, file, type)
            .Where(e => e.Include.Length > 0 || e.Update.Length > 0)
            .SelectMany(e => e.Metadata.Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && m.Condition.Length == 0));

    private void RemoveDefinitions(ProjectRootElement xml, string file, string type, string name)
    {
        foreach (var definition in Definitions(xml, file, type, name).ToList())
        {
            var item = (ProjectItemElement)definition.Parent;
            item.RemoveChild(definition);
            if (item.Update.Length > 0 && item.Metadata.Count == 0)
            {
                RemoveElement(item);
            }
        }
    }

    // ------------------------------------------------------------------ include and exclude

    /// <summary>Exclude From Project: the file (or every file under the folder) stops being an item.</summary>
    public void Exclude(string path)
    {
        path = Normalize(path);
        if (Directory.Exists(path))
        {
            foreach (var element in TopLevelItems(Xml).Where(e => (e.Include.Length > 0 || e.Update.Length > 0) && SinglePath(e) is { } value && IsSameOrUnder(Full(value), path)).ToList())
            {
                RemoveElement(element);
            }

            var pattern = Relative(path) + "\\**";
            foreach (var type in DefaultGlobTypes())
            {
                if (!TopLevelItems(Xml).Any(e => e.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase) && e.Condition.Length == 0 && SamePattern(e.Remove, pattern)))
                {
                    AddElement(type, ItemOperations.Remove, pattern);
                }
            }

            Reevaluate();
            return;
        }

        foreach (var element in ExactElements(Xml, path, null).Where(e => e.Include.Length > 0 || e.Update.Length > 0).ToList())
        {
            RemoveElement(element);
        }

        Reevaluate();
        foreach (var type in ItemsFor(path).Select(i => i.ItemType).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            AddElement(type, ItemOperations.Remove, Relative(path));
        }

        Reevaluate();
    }

    /// <summary>
    /// Include In Project: removes the <c>Remove</c> items that name the file (or folder), and includes what is still
    /// excluded with the item type the SDK's globs would give it.
    /// </summary>
    public void IncludeInProject(string path)
    {
        path = Normalize(path);
        var isFolder = Directory.Exists(path);
        foreach (var element in TopLevelItems(Xml).Where(e => e.Remove.Length > 0 && e.Condition.Length == 0 && (isFolder ? NamesFolder(e.Remove, path) : SinglePath(e) is { } v && SamePath(Full(v), path))).ToList())
        {
            RemoveElement(element);
        }

        Reevaluate();
        var files = isFolder ? FilesUnder(path).ToList() : [path];
        foreach (var file in files.Where(f => !ItemsFor(f).Any()))
        {
            foreach (var remove in ExactElements(Xml, file, null).Where(e => e.Remove.Length > 0).ToList())
            {
                RemoveElement(remove);
            }

            Reevaluate();
            if (!ItemsFor(file).Any())
            {
                Include(file, DefaultItemType(file), link: null);
            }
        }
    }

    /// <summary>Adds links to files outside the project, shown in <paramref name="folder"/> (files inside are just included).</summary>
    public void AddLinks(IEnumerable<string> files, string folder)
    {
        folder = Normalize(folder);
        foreach (var file in files.Select(Normalize))
        {
            if (IsUnder(file, _directory))
            {
                IncludeInProject(file);
                continue;
            }

            if (ItemsFor(file).Any(i => i.Xml is { } e && IsThisProject(e.ContainingProject) && !HasWildcard(e.Include)))
            {
                continue; // already linked
            }

            var relativeFolder = IsUnder(folder, _directory) ? Relative(folder) : string.Empty;
            var link = relativeFolder.Length == 0 ? System.IO.Path.GetFileName(file) : relativeFolder + "\\" + System.IO.Path.GetFileName(file);
            Include(file, DefaultItemType(file), link);
        }
    }

    private void Include(string file, string type, string? link)
    {
        var element = AddElement(type, ItemOperations.Include, Relative(file));
        if (link is not null)
        {
            element.AddMetadata(ItemMetadataNames.Link, link, expressAsAttribute: true);
        }

        Reevaluate();
    }

    /// <summary>The item type a file gets when it is included: the one the SDK's globs would give it.</summary>
    private string DefaultItemType(string file)
    {
        var extension = System.IO.Path.GetExtension(file);
        if (IsOrdered && extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase))
        {
            return "Compile"; // F# signature files compile, before their implementation
        }

        if (extension.Equals(_project.GetPropertyValue("DefaultLanguageSourceExtension"), StringComparison.OrdinalIgnoreCase)
            || (extension.Length > 0 && _project.GetPropertyValue("DefaultLanguageSourceExtension").Length == 0 && extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            return "Compile";
        }

        if (extension.Equals(".resx", StringComparison.OrdinalIgnoreCase))
        {
            return "EmbeddedResource";
        }

        if (IsTrue(_project.GetPropertyValue("EnableDefaultContentItems"))
            && (IsUnder(file, System.IO.Path.Combine(_directory, "wwwroot")) || extension.ToLowerInvariant() is ".json" or ".config" or ".cshtml" or ".razor"))
        {
            return "Content";
        }

        return "None";
    }

    /// <summary>The item types whose default globs the project uses (what a folder's exclusion removes from).</summary>
    private IEnumerable<string> DefaultGlobTypes()
    {
        if (!IsFalse(_project.GetPropertyValue("EnableDefaultCompileItems")))
        {
            yield return "Compile";
        }

        if (IsTrue(_project.GetPropertyValue("EnableDefaultContentItems")))
        {
            yield return "Content";
        }

        if (!IsFalse(_project.GetPropertyValue("EnableDefaultEmbeddedResourceItems")))
        {
            yield return "EmbeddedResource";
        }

        if (!IsFalse(_project.GetPropertyValue("EnableDefaultNoneItems")))
        {
            yield return "None";
        }
    }

    // ------------------------------------------------------------------ compile order

    /// <summary>
    /// F#'s compile order (M36): moves the file's <c>Compile</c> include before or after <paramref name="target"/>'s.
    /// Both must be explicit includes in the project file, as F# projects write them.
    /// </summary>
    public void Move(string file, string target, bool after)
    {
        var element = CompileInclude(Normalize(file));
        var anchor = CompileInclude(Normalize(target));
        if (element == anchor)
        {
            return;
        }

        var group = element.Parent;
        group.RemoveChild(element);
        if (after)
        {
            anchor.Parent.InsertAfterChild(element, anchor);
        }
        else
        {
            anchor.Parent.InsertBeforeChild(element, anchor);
        }

        if (group.Count == 0)
        {
            group.Parent.RemoveChild(group);
        }

        Reevaluate();
    }

    private ProjectItemElement CompileInclude(string file) =>
        ExactElements(Xml, file, "Compile").FirstOrDefault(e => e.Include.Length > 0)
        ?? throw new InvalidOperationException($"{System.IO.Path.GetFileName(file)} has no Compile item of its own in {System.IO.Path.GetFileName(Path)}, so it has no place in the compile order.");

    // ------------------------------------------------------------------ moves between projects

    /// <summary>An explicit item carried from one project to another with its file (ADR 0033).</summary>
    internal sealed record CarriedItem(string ItemType, string RelativePath, IReadOnlyDictionary<string, string> Metadata);

    /// <summary>Applies items carried from another project to the files that arrived in this one.</summary>
    public void Receive(string root, IEnumerable<CarriedItem> items)
    {
        foreach (var item in items)
        {
            var file = item.RelativePath.Length == 0 ? Normalize(root) : Normalize(System.IO.Path.Combine(root, item.RelativePath));
            var metadata = item.Metadata.Where(m => !m.Key.Equals(ItemMetadataNames.Link, StringComparison.OrdinalIgnoreCase)).ToDictionary(m => m.Key, m => (string?)m.Value, StringComparer.OrdinalIgnoreCase);
            Set(file, item.ItemType, metadata);
        }
    }

    // ------------------------------------------------------------------ elements

    private void Reevaluate() => _project.ReevaluateIfNecessary();

    /// <summary>
    /// Adds an item: an <c>Include</c> next to other includes of its type, an <c>Update</c> next to other updates of its
    /// type (else in a new group at the end), a <c>Remove</c> next to other removes (else in a new group after the last
    /// property group).
    /// </summary>
    private ProjectItemElement AddElement(string type, string operation, string value)
    {
        var groups = Xml.ItemGroups.Where(g => g.Parent == Xml && g.Condition.Length == 0).ToList();
        ProjectItemGroupElement? group = operation switch
        {
            ItemOperations.Include => groups.FirstOrDefault(g => g.Items.Any(i => i.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase) && i.Include.Length > 0)),
            ItemOperations.Update => groups.FirstOrDefault(g => g.Items.Any(i => i.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase) && i.Update.Length > 0)),
            _ => groups.FirstOrDefault(g => g.Items.Count > 0 && g.Items.All(i => i.Remove.Length > 0)) ?? groups.FirstOrDefault(g => g.Items.Any(i => i.Remove.Length > 0)),
        };

        if (group is null)
        {
            group = Xml.CreateItemGroupElement();
            var after = operation == ItemOperations.Remove ? Xml.Children.OfType<ProjectPropertyGroupElement>().LastOrDefault() : null;
            if (after is not null)
            {
                Xml.InsertAfterChild(group, after);
            }
            else
            {
                Xml.AppendChild(group);
            }
        }

        if (operation == ItemOperations.Include)
        {
            if (IsOrdered && type.Equals("Compile", StringComparison.OrdinalIgnoreCase) && group.Items.Where(i => i.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase) && i.Include.Length > 0).ToList() is [_, ..] compiled)
            {
                // F#: the compile order. A new file goes last, or before the last file of a program (its entry point).
                var include = Xml.CreateItemElement(type, value);
                var isProgram = _project.GetPropertyValue("OutputType").Trim().ToLowerInvariant() is "exe" or "winexe";
                if (isProgram)
                {
                    group.InsertBeforeChild(include, compiled[^1]);
                }
                else
                {
                    group.InsertAfterChild(include, compiled[^1]);
                }

                return include;
            }

            return group.AddItem(type, value);
        }

        var element = Xml.CreateItemElement(type);
        if (operation == ItemOperations.Update)
        {
            element.Update = value;
        }
        else
        {
            element.Remove = value;
        }

        // After the last item of the same operation (and type, when there is one), else at the end of the group.
        var sameOperation = group.Items.Where(i => Operation(i) == operation).ToList();
        var previous = sameOperation.LastOrDefault(i => i.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase)) ?? sameOperation.LastOrDefault();
        if (previous is not null)
        {
            group.InsertAfterChild(element, previous);
        }
        else
        {
            group.AppendChild(element);
        }

        return element;
    }

    private static void RemoveElement(ProjectItemElement element)
    {
        var group = element.Parent;
        group.RemoveChild(element);
        if (group.Count == 0)
        {
            group.Parent.RemoveChild(group);
        }
    }

    private static IEnumerable<ProjectItemElement> TopLevelItems(ProjectRootElement xml) =>
        xml.ItemGroups.Where(g => g.Parent == xml).SelectMany(g => g.Items);

    /// <summary>Unconditioned items of top-level groups whose value is exactly the file (of <paramref name="type"/>, or any type).</summary>
    private IEnumerable<ProjectItemElement> ExactElements(ProjectRootElement xml, string file, string? type) =>
        TopLevelItems(xml).Where(e => e.Condition.Length == 0 && e.Parent.Condition.Length == 0
                                      && (type is null || e.ItemType.Equals(type, StringComparison.OrdinalIgnoreCase))
                                      && SinglePath(e) is { } value && SamePath(Full(value), file));

    /// <summary>Every project file item naming the file, exactly or through a wildcard (for the Properties window).</summary>
    private IEnumerable<ProjectItemElement> ElementsNaming(ProjectRootElement xml, string file, bool includeConditioned, bool includeWildcards) =>
        TopLevelItems(xml).Where(e =>
        {
            if (!includeConditioned && (e.Condition.Length > 0 || e.Parent.Condition.Length > 0))
            {
                return false;
            }

            var value = Value(e);
            if (value.Contains('$', StringComparison.Ordinal))
            {
                return false;
            }

            if (SinglePath(e) is { } single)
            {
                return SamePath(Full(single), file);
            }

            return includeWildcards && value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part =>
                HasWildcard(part) ? MSBuildGlob.Parse(_directory, part.Replace('\\', '/')).IsMatch(file) : SamePath(Full(part), file));
        });

    private static string Operation(ProjectItemElement e) =>
        e.Include.Length > 0 ? ItemOperations.Include : e.Update.Length > 0 ? ItemOperations.Update : ItemOperations.Remove;

    private static string Value(ProjectItemElement e) => e.Include.Length > 0 ? e.Include : e.Update.Length > 0 ? e.Update : e.Remove;

    private static string? Condition(ProjectItemElement e) =>
        string.Join(" and ", new[] { e.Parent.Condition, e.Condition }.Where(c => c.Length > 0)) is { Length: > 0 } condition ? condition : null;

    /// <summary>The element's value when it is one plain path (no wildcard, list or property), else null.</summary>
    private static string? SinglePath(ProjectItemElement e)
    {
        var value = Value(e).Trim();
        return value.Length == 0 || value.Contains(';', StringComparison.Ordinal) || value.Contains('$', StringComparison.Ordinal) || value.Contains('@', StringComparison.Ordinal) || HasWildcard(value) ? null : value;
    }

    private static bool HasWildcard(string value) => value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);

    /// <summary>Whether a <c>Remove</c> value excludes exactly the folder: <c>Folder\**</c>, <c>Folder\**\*</c> or <c>Folder\</c>.</summary>
    private bool NamesFolder(string value, string folder)
    {
        var trimmed = value.Trim().Replace('/', '\\');
        foreach (var suffix in new[] { "\\**\\*", "\\**", "\\" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal) && !HasWildcard(trimmed[..^suffix.Length]))
            {
                return SamePath(Full(trimmed[..^suffix.Length]), folder);
            }
        }

        return false;
    }

    private static bool SamePattern(string value, string pattern) =>
        string.Equals(value.Trim().Replace('/', '\\'), pattern, s_pathComparison);

    /// <summary>The files under a folder that a project could include (not under bin/obj, version control or packages).</summary>
    private static IEnumerable<string> FilesUnder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            yield return file;
        }

        foreach (var directory in Directory.EnumerateDirectories(folder).Where(d => !s_skippedFolders.Contains(System.IO.Path.GetFileName(d))))
        {
            foreach (var file in FilesUnder(directory))
            {
                yield return file;
            }
        }
    }

    // ------------------------------------------------------------------ paths

    private string Full(string value) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.Combine(_directory, value.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar))));

    /// <summary>The project-relative path as project files write it (with backslashes, as Visual Studio does).</summary>
    private string Relative(string path) => System.IO.Path.GetRelativePath(_directory, path).Replace('/', '\\');

    private static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, s_pathComparison);

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar, s_pathComparison);

    private static bool IsSameOrUnder(string path, string root) => SamePath(path, root) || IsUnder(path, root);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsTrue(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string value) => value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
}
