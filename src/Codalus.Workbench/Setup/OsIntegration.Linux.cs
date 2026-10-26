using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.Setup;

/// <summary>
/// Linux (ADR 0084), in the user's XDG folders only:
/// <list type="bullet">
/// <item>Folders: a GNOME Files (Nautilus) script, a KDE Dolphin service menu, two Nemo actions (a folder, and a folder's
/// empty space) and, when Thunar's custom actions file exists, an action in it. Each says "Open with Codalus IDE".</item>
/// <item>File types: the MIME types shared-mime-info lacks (<c>mime/packages/codalus-dotnet.xml</c>), a desktop entry
/// that opens them (<c>applications/codalus-solutions.desktop</c>, hidden from the menus) and Codalus IDE as their default
/// application (<c>xdg-mime default</c>, or <c>mimeapps.list</c> directly without it). <c>update-mime-database</c> and
/// <c>update-desktop-database</c> run when they're installed. These are the user's tools, one process per operation.</item>
/// </list>
/// The packaged desktop entry (<c>/usr/share/applications/codalus.desktop</c>) declares no new types, so nothing becomes
/// the default without this opt-in.
/// </summary>
public sealed class LinuxOsIntegration(OsLauncher launcher, string dataHome, string configHome, ProcessRunner? run = null, Func<string, string?>? findTool = null) : OsIntegration(launcher)
{
    /// <summary>The desktop entry the file types open with.</summary>
    public const string DesktopId = "codalus-solutions.desktop";

    /// <summary>The Thunar action's <c>unique-id</c>.</summary>
    public const string ThunarId = "codalus-open-folder";

    /// <summary>The MIME types, by extension (the names MonoDevelop gave them, and <c>codalus.desktop</c> already uses for projects).</summary>
    public static readonly IReadOnlyList<(string Extension, string Type, string Parent, string Comment)> MimeTypes =
    [
        (".sln", "application/x-sln", "text/plain", "Solution file"),
        (".slnx", "application/x-slnx", "application/xml", "XML solution file"),
        (".slnf", "application/x-slnf", "application/json", "Solution filter file"),
        (".csproj", "application/x-csproj", "application/xml", "C# project file"),
        (".vbproj", "application/x-vbproj", "application/xml", "Visual Basic project file"),
        (".fsproj", "application/x-fsproj", "application/xml", "F# project file"),
    ];

    private readonly ProcessRunner _run = run ?? EnvironmentCheck.RunProcessAsync;
    private readonly Func<string, string?> _findTool = findTool ?? (name => Ai.Acp.AcpAgentConnector.FindOnPath(name, Environment.GetEnvironmentVariable("PATH")));

    public string DataHome { get; } = dataHome;

    public string ConfigHome { get; } = configHome;

    public string NautilusScript => Path.Combine(DataHome, "nautilus", "scripts", "Open with Codalus IDE");

    public string DolphinServiceMenu => Path.Combine(DataHome, "kio", "servicemenus", "codalus-open-folder.desktop");

    public string NemoAction => Path.Combine(DataHome, "nemo", "actions", "codalus-open-folder.nemo_action");

    public string NemoBackgroundAction => Path.Combine(DataHome, "nemo", "actions", "codalus-open-folder-background.nemo_action");

    public string ThunarActions => Path.Combine(ConfigHome, "Thunar", "uca.xml");

    public string MimePackage => Path.Combine(DataHome, "mime", "packages", "codalus-dotnet.xml");

    public string DesktopEntry => Path.Combine(DataHome, "applications", DesktopId);

    /// <summary>Where the default applications are (the XDG location <c>xdg-mime</c> writes).</summary>
    public string MimeAppsList => Path.Combine(ConfigHome, "mimeapps.list");

    /// <summary>The older location some desktops still read; cleaned too.</summary>
    private string LegacyMimeAppsList => Path.Combine(DataHome, "applications", "mimeapps.list");

    internal override IReadOnlyList<OsEntry> Entries(OsIntegrationFeature feature)
    {
        if (feature == OsIntegrationFeature.FileTypes)
        {
            return
            [
                FileEntry(MimePackage, Marker, () => [(string.Empty, MimePackageXml())], stopAt: DataHome),
                FileEntry(DesktopEntry, Launcher.DesktopExec("%f"), () => [(string.Empty, SolutionsDesktopEntry())], stopAt: DataHome),
                DefaultsEntry(),
            ];
        }

        var exec = Launcher.DesktopExec("%f");
        List<OsEntry> entries =
        [
            FileEntry(NautilusScript, Launcher.ShellCommand, () => [(string.Empty, NautilusScriptText())], executable: true, stopAt: DataHome),
            FileEntry(DolphinServiceMenu, exec, () => [(string.Empty, DolphinServiceMenuText())], executable: true, stopAt: DataHome),
            FileEntry(NemoAction, Launcher.DesktopExec("%F"), () => [(string.Empty, NemoActionText(background: false))], stopAt: DataHome),
            FileEntry(NemoBackgroundAction, Launcher.DesktopExec("%P"), () => [(string.Empty, NemoActionText(background: true))], stopAt: DataHome),
        ];

        // Thunar writes its file with its default actions the first time it runs: one Codalus IDE created would have none.
        if (File.Exists(ThunarActions))
        {
            entries.Add(ThunarEntry());
        }

        return entries;
    }

    protected override async Task AfterChangeAsync(OsIntegrationFeature feature, bool registered, CancellationToken cancellationToken)
    {
        // Registering rebuilt the caches before it set the defaults (DefaultsEntry).
        if (feature == OsIntegrationFeature.FileTypes && !registered)
        {
            await UpdateDatabasesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateDatabasesAsync(CancellationToken cancellationToken)
    {
        await RunIfPresentAsync(_run, _findTool("update-mime-database"), [Path.Combine(DataHome, "mime")], cancellationToken).ConfigureAwait(false);
        await RunIfPresentAsync(_run, _findTool("update-desktop-database"), [Path.Combine(DataHome, "applications")], cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ folders

    private string NautilusScriptText() => $"""
        #!/bin/sh
        # {Marker}: {OpenWithLabel}. Codalus IDE wrote this file (Tools > Options > Environment > System Integration); turning that off removes it.
        [ $# -eq 0 ] && set -- "$PWD"
        for folder in "$@"; do
          case "$folder" in /*) ;; *) folder="$PWD/$folder" ;; esac
          {Launcher.ShellCommand} "$folder" >/dev/null 2>&1 &
        done

        """;

    private string DolphinServiceMenuText() => $"""
        # {Marker}: Codalus IDE wrote this file (Tools > Options > Environment > System Integration); turning that off removes it.
        [Desktop Entry]
        Type=Service
        ServiceTypes=KonqPopupMenu/Plugin
        X-KDE-ServiceTypes=KonqPopupMenu/Plugin
        MimeType=inode/directory;
        Actions=openWithCodalus;
        X-KDE-Priority=TopLevel

        [Desktop Action openWithCodalus]
        Name={OpenWithLabel}
        Icon=codalus
        Exec={Launcher.DesktopExec("%f")}

        """;

    private string NemoActionText(bool background) => $"""
        # {Marker}: Codalus IDE wrote this file (Tools > Options > Environment > System Integration); turning that off removes it.
        [Nemo Action]
        Name={OpenWithLabel}
        Comment={OpenWithLabel}
        Exec={Launcher.DesktopExec(background ? "%P" : "%F")}
        Icon-Name=codalus
        Selection={(background ? "None" : "NotNone")}
        Extensions={(background ? "any" : "dir")};

        """;

    private OsEntry ThunarEntry() => new(ThunarActions,
        () =>
        {
            if (LoadThunar() is not { } document)
            {
                return (File.Exists(ThunarActions) ? OsEntryState.Foreign : OsEntryState.Missing, null);
            }

            return ThunarActionsOf(document).FirstOrDefault() is { } action
                ? (action.Element("command")?.Value == ThunarCommand ? OsEntryState.Current : OsEntryState.OtherCopy, action.Element("command")?.Value)
                : (OsEntryState.Missing, null);
        },
        _ =>
        {
            if (LoadThunar() is { Root: { } root } document)
            {
                ThunarActionsOf(document).ToList().ForEach(a => a.Remove());
                root.Add(new XElement("action",
                    new XElement("icon", "codalus"),
                    new XElement("name", OpenWithLabel),
                    new XElement("submenu", string.Empty),
                    new XElement("unique-id", ThunarId),
                    new XElement("command", ThunarCommand),
                    new XElement("description", Marker),
                    new XElement("range", string.Empty),
                    new XElement("patterns", "*"),
                    new XElement("directories")));
                SaveThunar(document);
            }

            return Task.CompletedTask;
        },
        () =>
        {
            if (LoadThunar() is { } document)
            {
                ThunarActionsOf(document).ToList().ForEach(a => a.Remove());
                SaveThunar(document);
            }
        });

    private string ThunarCommand => Launcher.ShellCommand + " %f";

    private static IEnumerable<XElement> ThunarActionsOf(XDocument document) =>
        document.Root?.Elements("action").Where(a => a.Element("unique-id")?.Value == ThunarId) ?? [];

    private XDocument? LoadThunar()
    {
        try
        {
            return File.Exists(ThunarActions) ? XDocument.Load(ThunarActions, LoadOptions.PreserveWhitespace) : null;
        }
        catch (XmlException)
        {
            return null; // not ours to repair
        }
    }

    private void SaveThunar(XDocument document)
    {
        using var writer = XmlWriter.Create(ThunarActions, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false });
        document.Save(writer);
    }

    // ------------------------------------------------------------------ file types

    private static string MimePackageXml()
    {
        var types = string.Concat(MimeTypes.Select(t => $"""
              <mime-type type="{t.Type}">
                <comment>{XmlEscape(t.Comment)}</comment>
                <sub-class-of type="{t.Parent}"/>
                <glob pattern="*{t.Extension}"/>
              </mime-type>

            """));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- {Marker}: Codalus IDE wrote this file (Tools > Options > Environment > System Integration); turning that off removes it. -->
            <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
            {types}</mime-info>

            """;
    }

    private string SolutionsDesktopEntry() => $"""
        [Desktop Entry]
        Type=Application
        Name=Codalus IDE
        Comment={Localization.Localizer.Get("msg.osIntegration.desktopComment", "Open solutions and projects in Codalus IDE")}
        Exec={Launcher.DesktopExec("%f")}
        Icon=codalus
        Terminal=false
        NoDisplay=true
        StartupWMClass=codalus
        MimeType={string.Concat(MimeTypes.Select(t => t.Type + ";"))}
        X-Codalus-Integration={Marker}

        """;

    /// <summary>Codalus IDE as each type's default application, in <c>mimeapps.list</c>.</summary>
    private OsEntry DefaultsEntry() => new(MimeAppsList,
        () =>
        {
            var defaults = MimeTypes.Count(t => MimeApps.DefaultOf(MimeAppsList, t.Type) == DesktopId);
            return defaults == 0 ? (OsEntryState.Missing, null) : defaults == MimeTypes.Count ? (OsEntryState.Current, null) : (OsEntryState.Damaged, null);
        },
        async cancellationToken =>
        {
            // The types and the entry have to be known first: some desktops check them when the default is set.
            await UpdateDatabasesAsync(cancellationToken).ConfigureAwait(false);
            var types = MimeTypes.Select(t => t.Type).ToList();
            if (_findTool("xdg-mime") is { } xdgMime)
            {
                await RunIfPresentAsync(_run, xdgMime, [.. types.Prepend(DesktopId).Prepend("default")], cancellationToken).ConfigureAwait(false);
            }

            // Without xdg-mime, or where it wrote somewhere else (some desktops), the XDG file itself.
            if (types.Any(t => MimeApps.DefaultOf(MimeAppsList, t) != DesktopId))
            {
                MimeApps.SetDefault(MimeAppsList, DesktopId, types);
            }
        },
        () =>
        {
            var types = MimeTypes.Select(t => t.Type).ToList();
            MimeApps.Remove(MimeAppsList, DesktopId, types);
            MimeApps.Remove(LegacyMimeAppsList, DesktopId, types);
        });
}

/// <summary>
/// The XDG <c>mimeapps.list</c> (an INI file): the first desktop entry of a type's line under
/// <c>[Default Applications]</c> is its default. Edits change only the lines of the types given, and only Codalus IDE's
/// entry in them, so the user's other choices stay as they were.
/// </summary>
internal static class MimeApps
{
    private const string Defaults = "[Default Applications]";
    private const string Added = "[Added Associations]";

    public static string? DefaultOf(string file, string type)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        var section = string.Empty;
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                section = trimmed;
            }
            else if (section == Defaults && Split(trimmed) is { } entry && entry.Type == type)
            {
                return entry.Apps.FirstOrDefault();
            }
        }

        return null;
    }

    /// <summary>Puts <paramref name="desktop"/> first on each type's line (the others stay after it), adding the section or lines needed.</summary>
    public static void SetDefault(string file, string desktop, IReadOnlyList<string> types)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var start = lines.FindIndex(l => l.Trim() == Defaults);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(Defaults);
            start = lines.Count - 1;
        }

        var missing = types.ToList();
        var end = SectionEnd(lines, start);
        for (var i = start + 1; i < end; i++)
        {
            if (Split(lines[i].Trim()) is { } entry && missing.Remove(entry.Type))
            {
                lines[i] = Join(entry.Type, entry.Apps.Where(a => a != desktop).Prepend(desktop));
            }
        }

        lines.InsertRange(end, missing.Select(t => Join(t, [desktop])));
        Write(file, lines);
    }

    /// <summary>Takes <paramref name="desktop"/> off each type's line in the defaults and added associations (a line left empty goes).</summary>
    public static void Remove(string file, string desktop, IReadOnlyList<string> types)
    {
        if (!File.Exists(file))
        {
            return;
        }

        var lines = File.ReadAllLines(file).ToList();
        var changed = false;
        var section = string.Empty;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                section = trimmed;
                continue;
            }

            if (section is Defaults or Added && Split(trimmed) is { } entry && types.Contains(entry.Type) && entry.Apps.Contains(desktop))
            {
                var rest = entry.Apps.Where(a => a != desktop).ToList();
                changed = true;
                if (rest.Count == 0)
                {
                    lines.RemoveAt(i--);
                }
                else
                {
                    lines[i] = Join(entry.Type, rest);
                }
            }
        }

        if (changed)
        {
            Write(file, lines);
        }
    }

    private static int SectionEnd(List<string> lines, int start)
    {
        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('['))
        {
            end++;
        }

        // New lines go after the section's last entry, before the blank lines that separate it from the next.
        while (end > start + 1 && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        return end;
    }

    private static (string Type, List<string> Apps)? Split(string line)
    {
        var equals = line.IndexOf('=', StringComparison.Ordinal);
        return line.Length == 0 || line.StartsWith('#') || equals <= 0
            ? null
            : (line[..equals].Trim(), line[(equals + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    private static string Join(string type, IEnumerable<string> apps) => type + "=" + string.Concat(apps.Select(a => a + ";"));

    private static void Write(string file, List<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }
}
