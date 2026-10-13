using Codalus.Platform;

namespace Codalus.Workbench.Settings;

/// <summary>
/// Profiles (M23b): named sets of the user's settings (turned-off plugins among them), keyboard shortcuts, snippets,
/// saved window layouts and window layout. The active profile's files stay where they always are, so nothing that
/// reads them changes; the others are kept in <c>&lt;config&gt;/profiles/&lt;name&gt;/</c>. Switching saves the live
/// files into the active profile's folder and copies the other profile's files into place.
/// </summary>
public sealed class ProfileStore(AppPaths paths)
{
    public const string DefaultName = "Default";

    /// <summary>The window layout's file in a profile's folder (the shell's to write and read).</summary>
    public const string WindowLayoutFile = "window-layout.json";

    private static readonly string[] s_configFiles = ["settings.json", "keybindings.json"];
    private static readonly string[] s_configFolders = ["snippets"];
    private static readonly string[] s_stateFiles = ["layouts.json"];

    public string Root => Path.Combine(paths.Config, "profiles");

    private string ActiveFile => Path.Combine(Root, "active");

    /// <summary>The active profile's name.</summary>
    public string Active
    {
        get
        {
            try
            {
                return File.Exists(ActiveFile) && File.ReadAllText(ActiveFile).Trim() is { Length: > 0 } name && Exists(name) ? name : DefaultName;
            }
            catch (IOException)
            {
                return DefaultName;
            }
        }
    }

    /// <summary>Default first, then the others by name.</summary>
    public IReadOnlyList<string> Names =>
        [DefaultName, .. (Directory.Exists(Root) ? Directory.EnumerateDirectories(Root) : [])
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => !string.Equals(n, DefaultName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>A profile's folder; names are case-insensitive on every OS (Linux's file system isn't), so "copy" is "Copy"'s.</summary>
    public string Folder(string name) =>
        Path.Combine(Root, Names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? name);

    public bool Exists(string name) =>
        Names.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="name"/> can't be a new profile's name, or null.</summary>
    public string? Validate(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return "A profile needs a name.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or "..")
        {
            return "A profile's name can't contain / \\ : * ? \" < > |.";
        }

        return Exists(name) ? $"There is already a profile called {name}." : null;
    }

    /// <summary>
    /// Creates a profile: a copy of the active one's files (<paramref name="copyActive"/>, with its window layout), or
    /// empty (the defaults).
    /// </summary>
    public void Create(string name, bool copyActive, string? windowLayout = null)
    {
        name = name.Trim();
        if (Validate(name) is { } error)
        {
            throw new ArgumentException(error, nameof(name));
        }

        var folder = Folder(name);
        Directory.CreateDirectory(folder);
        if (copyActive)
        {
            CopyLiveTo(folder, windowLayout);
        }
    }

    /// <summary>Saves the live files (and <paramref name="windowLayout"/>) as the active profile's.</summary>
    public void SaveActive(string? windowLayout)
    {
        var folder = Folder(Active);
        Directory.CreateDirectory(folder);
        CopyLiveTo(folder, windowLayout);
    }

    /// <summary>
    /// Makes <paramref name="name"/> active: saves the live files as the active profile's, then puts its files in their
    /// places (a file it doesn't have is removed, so the default applies). Returns its saved window layout, or null.
    /// </summary>
    public string? Switch(string name, string? currentWindowLayout)
    {
        if (!Exists(name))
        {
            throw new ArgumentException($"There is no profile called {name}.", nameof(name));
        }

        SaveActive(currentWindowLayout);
        var folder = Folder(name);
        foreach (var file in s_configFiles)
        {
            Replace(Path.Combine(folder, file), Path.Combine(paths.Config, file));
        }

        foreach (var file in s_stateFiles)
        {
            Replace(Path.Combine(folder, file), Path.Combine(paths.State, file));
        }

        foreach (var sub in s_configFolders)
        {
            var live = Path.Combine(paths.Config, sub);
            if (Directory.Exists(live))
            {
                Directory.Delete(live, recursive: true);
            }

            CopyFolder(Path.Combine(folder, sub), live);
        }

        Directory.CreateDirectory(Root);
        File.WriteAllText(ActiveFile, name);
        var layout = Path.Combine(folder, WindowLayoutFile);
        return File.Exists(layout) ? File.ReadAllText(layout) : null;
    }

    /// <summary>Deletes a profile other than Default and the active one.</summary>
    public void Delete(string name)
    {
        if (string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Active, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Default profile and the active one can't be deleted.");
        }

        if (Directory.Exists(Folder(name)))
        {
            Directory.Delete(Folder(name), recursive: true);
        }
    }

    private void CopyLiveTo(string folder, string? windowLayout)
    {
        foreach (var file in s_configFiles)
        {
            Replace(Path.Combine(paths.Config, file), Path.Combine(folder, file));
        }

        foreach (var file in s_stateFiles)
        {
            Replace(Path.Combine(paths.State, file), Path.Combine(folder, file));
        }

        foreach (var sub in s_configFolders)
        {
            var saved = Path.Combine(folder, sub);
            if (Directory.Exists(saved))
            {
                Directory.Delete(saved, recursive: true);
            }

            CopyFolder(Path.Combine(paths.Config, sub), saved);
        }

        if (windowLayout is not null)
        {
            File.WriteAllText(Path.Combine(folder, WindowLayoutFile), windowLayout);
        }
    }

    /// <summary>Copies <paramref name="from"/> over <paramref name="to"/>, or removes <paramref name="to"/> when there's nothing to copy.</summary>
    private static void Replace(string from, string to)
    {
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            var temp = to + ".tmp";
            File.Copy(from, temp, overwrite: true);
            File.Move(temp, to, overwrite: true);
        }
        else if (File.Exists(to))
        {
            File.Delete(to);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var sub in Directory.EnumerateDirectories(from))
        {
            CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
        }
    }
}
