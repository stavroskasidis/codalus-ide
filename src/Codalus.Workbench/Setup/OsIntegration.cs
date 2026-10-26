using System.Text;
using Codalus.Platform;

namespace Codalus.Workbench.Setup;

/// <summary>What Codalus IDE can add to the operating system, each on its own (M51, ADR 0084).</summary>
public enum OsIntegrationFeature
{
    /// <summary>"Open with Codalus IDE" on a folder's right-click in the file manager.</summary>
    Folders,

    /// <summary>Solutions and project files (<see cref="OsIntegration.FileExtensions"/>) open in Codalus IDE.</summary>
    FileTypes,
}

/// <summary>How a feature stands, read back from the registry or the files it writes.</summary>
public enum OsIntegrationState
{
    NotRegistered,

    Registered,

    /// <summary>Some of its entries are there, not all (one removed by hand, or a file manager installed since).</summary>
    Partial,

    /// <summary>All there, but they start another copy of Codalus IDE (another install, or a development build).</summary>
    OtherCopy,

    /// <summary>It can't be offered here (macOS file types outside the app bundle).</summary>
    Unavailable,
}

/// <summary>A feature's state, with what was found when it's another copy's, or why it's unavailable.</summary>
public sealed record OsIntegrationStatus(OsIntegrationState State, string? Detail = null)
{
    /// <summary>For a check box: on when registered, off when not (or unavailable), undetermined otherwise.</summary>
    public bool? IsOn => State switch
    {
        OsIntegrationState.Registered => true,
        OsIntegrationState.NotRegistered or OsIntegrationState.Unavailable => false,
        _ => null,
    };
}

/// <summary>
/// The command that starts this copy of Codalus IDE, as the OS should run it: the program and the arguments that go
/// before the folder or file. The installed app on Windows is Velopack's launcher next to <c>current\</c> (which an update
/// replaces); an AppImage is its file, not the folder it mounts; <c>dotnet codalus.dll</c> keeps the dll; a profile chosen
/// with <c>--user-data-dir</c> goes along.
/// </summary>
public sealed record OsLauncher(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>macOS: the app bundle the program is in (<c>…/Codalus IDE.app</c>), or null.</summary>
    public string? Bundle { get; init; }

    /// <summary>This process's launcher.</summary>
    public static OsLauncher Current(string? userDataDir) =>
        Resolve(PlatformInfo.Current, Environment.ProcessPath, AppContext.BaseDirectory, Environment.GetCommandLineArgs().FirstOrDefault(),
            Environment.GetEnvironmentVariable, File.Exists, userDataDir);

    /// <summary>The launcher of a process started as <paramref name="processPath"/> from <paramref name="baseDirectory"/>.</summary>
    public static OsLauncher Resolve(PlatformKind platform, string? processPath, string baseDirectory, string? firstArgument, Func<string, string?> environment, Func<string, bool> exists, string? userDataDir)
    {
        var program = processPath ?? Path.Combine(baseDirectory, platform == PlatformKind.Windows ? "codalus.exe" : "codalus");
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && firstArgument is { } assembly && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(Path.GetFullPath(assembly));
        }
        else if (platform == PlatformKind.Linux && environment("APPIMAGE") is { Length: > 0 } image && exists(image))
        {
            program = image;
        }
        else if (platform == PlatformKind.Windows
                 && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDirectory)) is { } root
                 && exists(Path.Combine(root, "Update.exe")) && exists(Path.Combine(root, Path.GetFileName(program))))
        {
            program = Path.Combine(root, Path.GetFileName(program));
        }

        if (userDataDir is { Length: > 0 })
        {
            arguments.Add("--user-data-dir");
            arguments.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataDir)));
        }

        return new OsLauncher(program, arguments) { Bundle = platform == PlatformKind.MacOS ? BundleOf(program) : null };
    }

    /// <summary><c>…/X.app</c> for a program at <c>…/X.app/Contents/MacOS/x</c>, else null.</summary>
    public static string? BundleOf(string program)
    {
        var parts = program.Split('/');
        return parts.Length >= 4 && parts[^2] == "MacOS" && parts[^3] == "Contents" && parts[^4].EndsWith(".app", StringComparison.Ordinal)
            ? string.Join('/', parts[..^3])
            : null;
    }

    /// <summary>The Windows command line: every part quoted, then <paramref name="placeholder"/> (<c>%1</c>, <c>%V</c>) quoted.</summary>
    public string WindowsCommand(string placeholder) =>
        string.Join(' ', Parts.Select(QuoteWindows)) + " \"" + placeholder + "\"";

    /// <summary>The command for a POSIX shell, each part single-quoted (the path the script adds goes after it).</summary>
    public string ShellCommand => string.Join(' ', Parts.Select(QuoteShell));

    /// <summary>A desktop entry's <c>Exec</c> value (freedesktop's rules), then <paramref name="fieldCode"/> (<c>%f</c>, <c>%F</c>).</summary>
    public string DesktopExec(string fieldCode) => string.Join(' ', Parts.Select(QuoteDesktop)) + " " + fieldCode;

    private IEnumerable<string> Parts => Arguments.Prepend(Program);

    /// <summary>One argument as <c>CommandLineToArgvW</c> reads it back.</summary>
    internal static string QuoteWindows(string argument)
    {
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            text.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }

    internal static string QuoteShell(string argument) => "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// One argument of an <c>Exec</c> key: <c>%</c> doubled, quoted with <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> escaped
    /// when it has a reserved character, then the key file's own escape of <c>\</c>.
    /// </summary>
    internal static string QuoteDesktop(string argument)
    {
        var text = argument.Replace("%", "%%", StringComparison.Ordinal);
        if (text.IndexOfAny([' ', '\t', '\n', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) >= 0)
        {
            var quoted = new StringBuilder("\"");
            foreach (var c in text)
            {
                if (c is '"' or '`' or '$' or '\\')
                {
                    quoted.Append('\\');
                }

                quoted.Append(c);
            }

            text = quoted.Append('"').ToString();
        }

        return text.Replace("\\", "\\\\", StringComparison.Ordinal);
    }
}

/// <summary>
/// Opt-in OS integration (M51, ADR 0084): "Open with Codalus IDE" on folders and the solution and project file types,
/// registered for the user only and removable. The registration itself is the source of truth: <see cref="GetStatus"/>
/// reads the registry or the files back, so Options shows what's there, and turning a feature off removes exactly the
/// entries it writes (by their names, or the marker in a file), leaving anything else alone. One subclass per OS, with
/// every root injectable, so tests write under a temporary registry key and folders.
/// </summary>
public abstract class OsIntegration
{
    /// <summary>The file types: solutions, solution filters and the project files.</summary>
    public static readonly IReadOnlyList<string> FileExtensions = [".sln", ".slnx", ".slnf", ".csproj", ".vbproj", ".fsproj"];

    /// <summary>In every file Codalus IDE writes for this, so it's recognized as its own (and nothing else is removed).</summary>
    public const string Marker = "codalus-os-integration";

    protected OsIntegration(OsLauncher launcher) => Launcher = launcher;

    /// <summary>The command entries start.</summary>
    public OsLauncher Launcher { get; }

    /// <summary>The label of the folder action, in the UI's language when it's written.</summary>
    protected static string OpenWithLabel => Localization.Localizer.Get("msg.osIntegration.openWith", "Open with Codalus IDE");

    /// <summary>The integration for this OS, writing where the OS reads it.</summary>
    public static OsIntegration ForCurrentPlatform(OsLauncher launcher)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsOsIntegration(launcher);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            // The machine's record, whatever profile (--user-data-dir) this copy runs with.
            return new MacOsIntegration(launcher, Path.Combine(home, "Library", "Services"), Path.Combine(home, "Library", "Application Support", "Codalus", "os-integration.txt"), new MacLaunchServices());
        }

        string Xdg(string variable, params string[] fallback) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir && Path.IsPathRooted(dir) ? dir : Path.Combine([home, .. fallback]);
        return new LinuxOsIntegration(launcher, Xdg("XDG_DATA_HOME", ".local", "share"), Xdg("XDG_CONFIG_HOME", ".config"));
    }

    /// <summary>Why <paramref name="feature"/> can't be offered here, or null when it can.</summary>
    public virtual string? UnavailableReason(OsIntegrationFeature feature) => null;

    /// <summary>The entries a feature writes, in order (tests read their names).</summary>
    internal abstract IReadOnlyList<OsEntry> Entries(OsIntegrationFeature feature);

    /// <summary>How <paramref name="feature"/> stands, read from the registry or files (quick: no process runs).</summary>
    public OsIntegrationStatus GetStatus(OsIntegrationFeature feature)
    {
        if (UnavailableReason(feature) is { } reason)
        {
            return new OsIntegrationStatus(OsIntegrationState.Unavailable, reason);
        }

        var read = Entries(feature).Select(e => e.Read()).ToList();
        var present = read.Count(r => r.State is OsEntryState.Current or OsEntryState.OtherCopy or OsEntryState.Damaged);
        var other = read.FirstOrDefault(r => r.State == OsEntryState.OtherCopy).Found;
        return present == 0 ? new OsIntegrationStatus(OsIntegrationState.NotRegistered)
            : present < read.Count || read.Any(r => r.State == OsEntryState.Damaged) ? new OsIntegrationStatus(OsIntegrationState.Partial)
            : read.All(r => r.State == OsEntryState.Current) ? new OsIntegrationStatus(OsIntegrationState.Registered)
            : new OsIntegrationStatus(OsIntegrationState.OtherCopy, other);
    }

    /// <summary>Registers (<paramref name="on"/>) or removes <paramref name="feature"/>; returns how it stands after.</summary>
    public Task<OsIntegrationStatus> SetAsync(OsIntegrationFeature feature, bool on, CancellationToken cancellationToken) =>
        on ? RegisterAsync(feature, cancellationToken) : UnregisterAsync(feature, cancellationToken);

    /// <summary>
    /// Writes every entry of <paramref name="feature"/> for <see cref="Launcher"/> (an entry of another copy is replaced,
    /// one that isn't Codalus IDE's own is left alone).
    /// </summary>
    public async Task<OsIntegrationStatus> RegisterAsync(OsIntegrationFeature feature, CancellationToken cancellationToken)
    {
        if (UnavailableReason(feature) is { } reason)
        {
            return new OsIntegrationStatus(OsIntegrationState.Unavailable, reason);
        }

        await BeforeRegisterAsync(feature, cancellationToken).ConfigureAwait(false);
        foreach (var entry in Entries(feature))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Read().State != OsEntryState.Foreign)
            {
                await entry.Write(cancellationToken).ConfigureAwait(false);
            }
        }

        await AfterChangeAsync(feature, registered: true, cancellationToken).ConfigureAwait(false);
        return GetStatus(feature);
    }

    /// <summary>Removes what <see cref="RegisterAsync"/> writes, and nothing else.</summary>
    public async Task<OsIntegrationStatus> UnregisterAsync(OsIntegrationFeature feature, CancellationToken cancellationToken)
    {
        if (UnavailableReason(feature) is { } reason)
        {
            return new OsIntegrationStatus(OsIntegrationState.Unavailable, reason);
        }

        foreach (var entry in Entries(feature).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Read().State is OsEntryState.Current or OsEntryState.OtherCopy or OsEntryState.Damaged)
            {
                entry.Remove();
            }
        }

        await AfterChangeAsync(feature, registered: false, cancellationToken).ConfigureAwait(false);
        return GetStatus(feature);
    }

    /// <summary>Before the entries are written (macOS: LaunchServices learns the bundle's document types).</summary>
    protected virtual Task BeforeRegisterAsync(OsIntegrationFeature feature, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>After entries were written or removed (Explorer told, the MIME database rebuilt).</summary>
    protected virtual Task AfterChangeAsync(OsIntegrationFeature feature, bool registered, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs one of the user's tools when it's there (one process per operation), ignoring how it went.</summary>
    internal static async Task RunIfPresentAsync(ProcessRunner run, string? program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (program is null)
        {
            return;
        }

        try
        {
            _ = await run(program, arguments, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // A cache the desktop rebuilds by itself later; the entries are written either way.
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// A file (or, with <paramref name="isFolder"/>, a bundle folder whose <paramref name="contentFile"/> is checked) that
    /// carries <see cref="Marker"/>: it's Codalus IDE's, and current when it has <paramref name="identity"/> (the command).
    /// </summary>
    internal static OsEntry FileEntry(string path, string identity, Func<IReadOnlyList<(string Relative, string Text)>> content, bool executable = false, string? stopAt = null, bool isFolder = false, string? contentFile = null)
    {
        string Checked() => isFolder ? Path.Combine(path, contentFile!) : path;
        return new OsEntry(path,
            () =>
            {
                if (!(isFolder ? Directory.Exists(path) : File.Exists(path)))
                {
                    return (OsEntryState.Missing, null);
                }

                var text = File.Exists(Checked()) ? File.ReadAllText(Checked()) : string.Empty;
                return !text.Contains(Marker, StringComparison.Ordinal) ? (OsEntryState.Foreign, null)
                    : text.Contains(identity, StringComparison.Ordinal) ? (OsEntryState.Current, null)
                    : (OsEntryState.OtherCopy, null);
            },
            _ =>
            {
                foreach (var (relative, text) in content())
                {
                    var file = relative.Length == 0 ? path : Path.Combine(path, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
                    if (executable && !OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                }

                return Task.CompletedTask;
            },
            () =>
            {
                if (isFolder)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                PruneEmptyFolders(Path.GetDirectoryName(path), stopAt);
            });
    }

    /// <summary>Removes <paramref name="folder"/> and its parents while they're empty, up to <paramref name="stopAt"/> (kept).</summary>
    internal static void PruneEmptyFolders(string? folder, string? stopAt)
    {
        if (stopAt is null)
        {
            return;
        }

        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        while (folder is not null
               && Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) is var full
               && full.Length > stop.Length && full.StartsWith(stop, StringComparison.Ordinal)
               && Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
        {
            Directory.Delete(full);
            folder = Path.GetDirectoryName(full);
        }
    }

    internal static string XmlEscape(string text) => System.Security.SecurityElement.Escape(text);
}

/// <summary>How one entry stands.</summary>
internal enum OsEntryState
{
    Missing,

    /// <summary>Codalus IDE's, starting <see cref="OsIntegration.Launcher"/>.</summary>
    Current,

    /// <summary>Codalus IDE's, starting another copy.</summary>
    OtherCopy,

    /// <summary>Codalus IDE's, half written.</summary>
    Damaged,

    /// <summary>Something else at the same place: never written over or removed.</summary>
    Foreign,
}

/// <summary>One registry key, file or setting that a feature writes; <see cref="Read"/> also gives what was found for another copy.</summary>
internal sealed record OsEntry(string Name, Func<(OsEntryState State, string? Found)> Read, Func<CancellationToken, Task> Write, Action Remove);
