using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Components;

/// <summary>
/// Something the IDE downloads when a feature first needs it (M22d): Node.js, the web language servers,
/// vscode-js-debug, converted icon themes. The Components page lists them with their version, size and licence, and can
/// remove, install or export them.
/// </summary>
public sealed class Component
{
    /// <summary>Its id, stable across versions (<c>node</c>, <c>js-debug</c>…).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The version this IDE uses, when it has one.</summary>
    public string? Version { get; init; }

    public required string License { get; init; }

    /// <summary>What uses it.</summary>
    public required string Description { get; init; }

    /// <summary>Its folders under the cache folder, as paths relative to it (an offline layout keeps them so).</summary>
    public required Func<IReadOnlyList<string>> Folders { get; init; }

    /// <summary>Whether it is there and complete.</summary>
    public required Func<bool> IsInstalled { get; init; }

    /// <summary>Downloads it (null: it is made on demand and can't be installed ahead).</summary>
    public Func<IProgress<string>?, CancellationToken, Task>? Install { get; init; }
}

/// <summary>A component as the page shows it.</summary>
/// <param name="Component">The component.</param>
/// <param name="Installed">Whether it is there.</param>
/// <param name="Size">Its folders' size in bytes.</param>
public sealed record ComponentStatus(Component Component, bool Installed, long Size);

/// <summary>An offline layout's index (<c>components.json</c>): each component's id, version and folders.</summary>
/// <param name="Components">The components it holds.</param>
public sealed record OfflineLayoutIndex(IReadOnlyList<OfflineLayoutEntry> Components);

/// <summary>One component of an offline layout.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Version">Its version.</param>
/// <param name="Folders">Its folders, relative to the layout's root and to the cache folder alike.</param>
public sealed record OfflineLayoutEntry(string Id, string? Version, IReadOnlyList<string> Folders);

/// <summary>
/// The on-demand components (M22d): their status, removing them, and offline layouts. A layout is a folder with the
/// components' folders as the cache holds them and a <c>components.json</c>; importing copies them into the cache, so
/// an air-gapped machine never downloads them.
/// </summary>
public sealed class ComponentCatalog(string cacheFolder, IReadOnlyList<Component> components)
{
    public const string LayoutIndex = "components.json";

    public IReadOnlyList<Component> Components { get; } = components;

    public string CacheFolder { get; } = cacheFolder;

    /// <summary>Each component's state and size (walks its folders; call off the UI thread).</summary>
    public IReadOnlyList<ComponentStatus> Status() =>
        [.. Components.Select(c => new ComponentStatus(c, c.IsInstalled(), c.Folders().Sum(f => Size(Path.Combine(CacheFolder, f)))))];

    /// <summary>Deletes a component's folders; a feature that needs it downloads it again.</summary>
    public void Remove(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        foreach (var folder in component.Folders())
        {
            var path = Path.Combine(CacheFolder, folder);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>Copies the installed components into <paramref name="target"/>, with its index; returns those exported.</summary>
    public IReadOnlyList<OfflineLayoutEntry> Export(string target)
    {
        var entries = new List<OfflineLayoutEntry>();
        foreach (var component in Components.Where(c => c.IsInstalled()))
        {
            var folders = component.Folders().Where(f => Directory.Exists(Path.Combine(CacheFolder, f))).ToList();
            foreach (var folder in folders)
            {
                Copy(Path.Combine(CacheFolder, folder), Path.Combine(target, folder));
            }

            entries.Add(new OfflineLayoutEntry(component.Id, component.Version, folders));
        }

        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, LayoutIndex), JsonSerializer.Serialize(new OfflineLayoutIndex(entries), ComponentJsonContext.Default.OfflineLayoutIndex));
        return entries;
    }

    /// <summary>
    /// Copies a layout's components into the cache, those this IDE uses (by id and version) and doesn't have yet;
    /// returns the ids imported. Throws <see cref="InvalidDataException"/> for a folder that isn't a layout.
    /// </summary>
    public IReadOnlyList<string> Import(string layout)
    {
        var indexFile = Path.Combine(layout, LayoutIndex);
        if (!File.Exists(indexFile))
        {
            throw new InvalidDataException($"{layout} isn't an offline layout: it has no {LayoutIndex}.");
        }

        var index = JsonSerializer.Deserialize(File.ReadAllText(indexFile), ComponentJsonContext.Default.OfflineLayoutIndex)
            ?? throw new InvalidDataException($"{LayoutIndex} is empty.");
        var imported = new List<string>();
        foreach (var entry in index.Components)
        {
            if (Components.FirstOrDefault(c => c.Id == entry.Id) is not { } component || component.Version != entry.Version || component.IsInstalled())
            {
                continue;
            }

            foreach (var folder in entry.Folders)
            {
                var source = Path.GetFullPath(Path.Combine(layout, folder));
                if (!source.StartsWith(Path.GetFullPath(layout), StringComparison.Ordinal) || folder.Contains("..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"The layout's folder {folder} is outside it.");
                }

                Copy(source, Path.Combine(CacheFolder, folder));
            }

            imported.Add(entry.Id);
        }

        return imported;
    }

    private static long Size(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OfflineLayoutIndex))]
internal sealed partial class ComponentJsonContext : JsonSerializerContext;
