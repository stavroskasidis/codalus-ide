using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace DaedalusStudio.Projects.Host;

/// <summary>
/// Keeps project files consistent with file operations, through MSBuild's construction model with formatting
/// preserved. Only explicit paths are touched: an item whose Include/Update/Remove names a renamed, moved or
/// deleted file or folder, <c>DependentUpon</c> metadata, <c>Folder</c> items and project references. Globs are
/// left alone, since they follow the files by themselves.
/// </summary>
internal sealed class ProjectFileEditor(ProjectCollection collection)
{
    private static readonly StringComparison s_pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Rewrites references to <paramref name="oldPath"/> (a file or a folder) as <paramref name="newPath"/> in
    /// <paramref name="projects"/>. Returns the project files that changed.
    /// </summary>
    public IReadOnlyList<string> Renamed(IEnumerable<string> projects, string oldPath, string newPath)
    {
        var changed = new List<string>();
        foreach (var projectPath in projects.Where(File.Exists))
        {
            var xml = Open(projectPath);
            var directory = Path.GetDirectoryName(projectPath)!;
            var edited = false;
            foreach (var item in xml.Items.ToList())
            {
                edited |= Rewrite(item, directory, oldPath, newPath);
                if (item.Metadata.FirstOrDefault(m => m.Name == "DependentUpon") is { } dependent
                    && Path.GetDirectoryName(Path.Combine(directory, FirstPath(item)))?.Equals(Path.GetDirectoryName(oldPath), s_pathComparison) == true
                    && dependent.Value.Equals(Path.GetFileName(oldPath), s_pathComparison))
                {
                    dependent.Value = Path.GetFileName(newPath);
                    edited = true;
                }
            }

            if (edited)
            {
                xml.Save();
                changed.Add(projectPath);
            }
        }

        return changed;
    }

    /// <summary>Removes items that name <paramref name="deletedPath"/> (or anything under it) explicitly.</summary>
    public IReadOnlyList<string> Deleted(IEnumerable<string> projects, string deletedPath)
    {
        var changed = new List<string>();
        foreach (var projectPath in projects.Where(File.Exists))
        {
            var xml = Open(projectPath);
            var directory = Path.GetDirectoryName(projectPath)!;
            var edited = false;
            foreach (var item in xml.Items.ToList())
            {
                var value = item.Include.Length > 0 ? item.Include : item.Update.Length > 0 ? item.Update : item.Remove;
                if (value.Contains('*', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal) || value.Length == 0)
                {
                    continue;
                }

                if (IsSameOrUnder(Full(directory, value), deletedPath))
                {
                    RemoveItem(item);
                    edited = true;
                }
            }

            if (edited)
            {
                xml.Save();
                changed.Add(projectPath);
            }
        }

        return changed;
    }

    public bool AddProjectReference(string projectPath, string referencePath)
    {
        var xml = Open(projectPath);
        var relative = Relative(Path.GetDirectoryName(projectPath)!, referencePath);
        if (xml.Items.Any(i => i.ItemType == "ProjectReference" && Full(Path.GetDirectoryName(projectPath)!, i.Include).Equals(referencePath, s_pathComparison)))
        {
            return false;
        }

        var group = xml.ItemGroups.FirstOrDefault(g => g.Condition.Length == 0 && g.Items.Any(i => i.ItemType == "ProjectReference"));
        if (group is null)
        {
            group = xml.AddItemGroup();
        }

        group.AddItem("ProjectReference", relative);
        xml.Save();
        return true;
    }

    public bool RemoveProjectReference(string projectPath, string referencePath)
    {
        var xml = Open(projectPath);
        var directory = Path.GetDirectoryName(projectPath)!;
        var items = xml.Items.Where(i => i.ItemType == "ProjectReference" && Full(directory, i.Include).Equals(referencePath, s_pathComparison)).ToList();
        foreach (var item in items)
        {
            RemoveItem(item);
        }

        if (items.Count > 0)
        {
            xml.Save();
        }

        return items.Count > 0;
    }

    private ProjectRootElement Open(string path)
    {
        // Always start from the file on disk: it may have been edited outside Daedalus.
        var xml = ProjectRootElement.Open(path, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
        return xml;
    }

    private static bool Rewrite(ProjectItemElement item, string directory, string oldPath, string newPath)
    {
        var edited = false;
        string Map(string value)
        {
            if (value.Length == 0 || value.Contains('*', StringComparison.Ordinal) || value.Contains('$', StringComparison.Ordinal))
            {
                return value;
            }

            var parts = value.Split(';');
            for (var i = 0; i < parts.Length; i++)
            {
                var trimmed = parts[i].Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var full = Full(directory, trimmed);
                if (IsSameOrUnder(full, oldPath))
                {
                    var mapped = newPath + full[oldPath.TrimEnd(Path.DirectorySeparatorChar).Length..];
                    var separator = trimmed.Contains('/', StringComparison.Ordinal) && !trimmed.Contains('\\', StringComparison.Ordinal) ? '/' : '\\';
                    var relative = Relative(directory, mapped).Replace(Path.DirectorySeparatorChar, separator);
                    if (trimmed.EndsWith('\\') || trimmed.EndsWith('/'))
                    {
                        relative += separator;
                    }

                    parts[i] = parts[i].Replace(trimmed, relative, StringComparison.Ordinal);
                    edited = true;
                }
            }

            return string.Join(';', parts);
        }

        if (item.Include.Length > 0)
        {
            item.Include = Map(item.Include);
        }

        if (item.Update.Length > 0)
        {
            item.Update = Map(item.Update);
        }

        if (item.Remove.Length > 0)
        {
            item.Remove = Map(item.Remove);
        }

        return edited;
    }

    private static string FirstPath(ProjectItemElement item) =>
        (item.Include.Length > 0 ? item.Include : item.Update).Split(';')[0].Trim();

    private static void RemoveItem(ProjectItemElement item)
    {
        var group = item.Parent;
        group.RemoveChild(item);
        if (group.Count == 0)
        {
            group.Parent.RemoveChild(group);
        }
    }

    private static string Full(string directory, string value) =>
        Path.GetFullPath(Path.Combine(directory, value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)))
            .TrimEnd(Path.DirectorySeparatorChar);

    private static string Relative(string directory, string path) => Path.GetRelativePath(directory, path);

    internal static bool IsSameOrUnder(string path, string root)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(trimmedRoot, s_pathComparison)
               || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, s_pathComparison);
    }
}
