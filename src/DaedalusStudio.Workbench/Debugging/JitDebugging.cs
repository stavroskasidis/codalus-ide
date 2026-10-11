using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DaedalusStudio.Workbench.Debugging;

/// <summary>
/// Where Just-In-Time debugging's variables live for the user's programs (M14c, ADR 0039): per user, never machine-wide,
/// so no administrator rights are needed.
/// </summary>
public interface IUserEnvironment
{
    /// <summary>What changes, in words for the consent dialog.</summary>
    string Description { get; }

    /// <summary>The current values of <paramref name="names"/> there (missing ones are left out).</summary>
    IReadOnlyDictionary<string, string> Read(IReadOnlyCollection<string> names);

    /// <summary>Sets the variables (a null value removes one).</summary>
    void Write(IReadOnlyDictionary<string, string?> values);
}

/// <summary>
/// Just-In-Time debugging (M14c, ADR 0039), off by default. No OS lets an unprivileged program register as the
/// debugger of other programs' crashes (Windows' <c>AeDebug</c> key is machine-wide; Linux and macOS have no JIT
/// debugger). So it is post-mortem: registration sets the .NET runtime's crash-dump variables in the user's environment
/// (<c>DOTNET_DbgEnableMiniDump</c> and friends), so a .NET program that crashes writes a full dump into Daedalus's
/// crash-dump folder; Daedalus watches the folder (<see cref="CrashDumpWatcher"/>) and offers to open each new dump in
/// its dump view (M14b). Unregistering removes the variables. Programs started after the change get it.
/// </summary>
public sealed class JitDebugging(IUserEnvironment environment, string dumpFolder)
{
    public const string EnableVariable = "DOTNET_DbgEnableMiniDump";
    public const string TypeVariable = "DOTNET_DbgMiniDumpType";
    public const string NameVariable = "DOTNET_DbgMiniDumpName";

    /// <summary>The variables registration sets (and unregistering removes).</summary>
    public static IReadOnlyList<string> Variables { get; } = [EnableVariable, TypeVariable, NameVariable];

    public IUserEnvironment Environment { get; } = environment;

    /// <summary>Where crashed programs' dumps go: <c>%e.%p.dmp</c> (program and process id).</summary>
    public string DumpFolder { get; } = dumpFolder;

    /// <summary>The variables for <paramref name="folder"/>: full dumps (type 4), so frames map to lines (ADR 0032).</summary>
    public static IReadOnlyDictionary<string, string> Settings(string folder) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EnableVariable] = "1",
        [TypeVariable] = "4",
        [NameVariable] = Path.Combine(folder, "%e.%p.dmp"),
    };

    /// <summary>Whether this Daedalus is registered: the variables point at its folder.</summary>
    public bool IsRegistered
    {
        get
        {
            var current = Environment.Read(Variables);
            static string Folder(string path) => path.Replace('\\', '/').TrimEnd('/');
            return current.GetValueOrDefault(EnableVariable) == "1"
                   && current.GetValueOrDefault(NameVariable) is { } name
                   && Folder(name).LastIndexOf('/') is var slash and > 0
                   && string.Equals(Folder(name)[..slash], Folder(DumpFolder), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Whether another registration (another Daedalus, or the user's own settings) already set the variables.</summary>
    public bool IsSetElsewhere => !IsRegistered && Environment.Read(Variables).ContainsKey(EnableVariable);

    public void Register()
    {
        Directory.CreateDirectory(DumpFolder);
        Environment.Write(Settings(DumpFolder).ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal));
    }

    public void Unregister() => Environment.Write(Variables.ToDictionary(v => v, _ => (string?)null, StringComparer.Ordinal));

    /// <summary>The user environment of this OS.</summary>
    public static IUserEnvironment ForThisSystem(string home) =>
        OperatingSystem.IsWindows() ? new WindowsUserEnvironment()
        : OperatingSystem.IsMacOS() ? new MacUserEnvironment(home)
        : new LinuxUserEnvironment(home);
}

/// <summary>Windows: the user's environment variables (<c>HKEY_CURRENT_USER\Environment</c>), which new programs inherit.</summary>
public sealed class WindowsUserEnvironment : IUserEnvironment
{
    public string Description => "your Windows user environment variables (HKEY_CURRENT_USER\\Environment)";

    public IReadOnlyDictionary<string, string> Read(IReadOnlyCollection<string> names)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in names)
            {
                if (System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is { } value)
                {
                    values[name] = value;
                }
            }
        }

        return values;
    }

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        foreach (var (name, value) in values)
        {
            // Also broadcasts the change, so Explorer starts new programs with it.
            System.Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        }
    }
}

/// <summary>
/// Linux: <c>~/.config/environment.d/90-daedalus-jit.conf</c> (the systemd user session, so desktop sessions get it)
/// and a marked block in <c>~/.profile</c> (login shells).
/// </summary>
public sealed class LinuxUserEnvironment(string home) : IUserEnvironment
{
    public const string BlockStart = "# >>> Daedalus Studio: Just-In-Time debugging (crash dumps) >>>";
    public const string BlockEnd = "# <<< Daedalus Studio <<<";

    private string Conf => Path.Combine(home, ".config", "environment.d", "90-daedalus-jit.conf");

    private string Profile => Path.Combine(home, ".profile");

    public string Description => $"{Conf} and a marked block in {Profile}";

    public IReadOnlyDictionary<string, string> Read(IReadOnlyCollection<string> names)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(Conf))
        {
            foreach (var line in File.ReadAllLines(Conf))
            {
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && names.Contains(line[..equals].Trim()))
                {
                    values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
                }
            }
        }

        return values;
    }

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        var current = Read(values.Keys.ToList()).ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            current[name] = value;
        }

        var set = current.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        if (set.Count == 0)
        {
            File.Delete(Conf);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Conf)!);
            File.WriteAllText(Conf, "# Written by Daedalus Studio (Just-In-Time debugging); Debug > Just-In-Time Debugging turns it off.\n" + string.Concat(set.Select(p => $"{p.Key}={p.Value}\n")));
        }

        var profile = File.Exists(Profile) ? File.ReadAllText(Profile) : string.Empty;
        profile = RemoveBlock(profile);
        if (set.Count > 0)
        {
            var block = new StringBuilder().Append(BlockStart).Append('\n');
            foreach (var (name, value) in set)
            {
                block.Append("export ").Append(name).Append('=').Append(Build.WslLaunch.ShellQuote(value!)).Append('\n');
            }

            profile = profile.TrimEnd('\n') + (profile.Length > 0 ? "\n\n" : string.Empty) + block.Append(BlockEnd).Append('\n');
        }

        if (profile.Length > 0 || File.Exists(Profile))
        {
            File.WriteAllText(Profile, profile);
        }
    }

    /// <summary><paramref name="text"/> without the marked block.</summary>
    public static string RemoveBlock(string text)
    {
        var start = text.IndexOf(BlockStart, StringComparison.Ordinal);
        var end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            return text;
        }

        var after = end + BlockEnd.Length;
        if (after < text.Length && text[after] == '\n')
        {
            after++;
        }

        var before = text[..start].TrimEnd('\n');
        var rest = text[after..];
        return before.Length == 0 ? rest : before + "\n" + rest;
    }
}

/// <summary>
/// macOS: <c>launchctl setenv</c> now (programs started by launchd, the Dock and Finder, get it) and a LaunchAgent that
/// sets it again at each login (<c>~/Library/LaunchAgents/dev.daedalus.jit.plist</c>).
/// </summary>
public sealed class MacUserEnvironment(string home) : IUserEnvironment
{
    private string Agent => Path.Combine(home, "Library", "LaunchAgents", "dev.daedalus.jit.plist");

    public string Description => $"the launchd user environment (launchctl setenv) and {Agent}";

    public IReadOnlyDictionary<string, string> Read(IReadOnlyCollection<string> names)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (Launchctl("getenv", name) is { Length: > 0 } value)
            {
                values[name] = value;
            }
        }

        return values;
    }

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _ = value is null ? Launchctl("unsetenv", name) : Launchctl("setenv", name, value);
        }

        var set = Read(JitDebugging.Variables);
        if (set.Count == 0)
        {
            File.Delete(Agent);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Agent)!);
        File.WriteAllText(Agent, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>dev.daedalus.jit</string>
              <key>ProgramArguments</key>
              <array><string>/bin/sh</string><string>-c</string><string>{System.Security.SecurityElement.Escape(string.Join("; ", set.Select(p => $"launchctl setenv {p.Key} {Build.WslLaunch.ShellQuote(p.Value)}")))}</string></array>
              <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }

    private static string? Launchctl(params string[] arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("launchctl", arguments) { RedirectStandardOutput = true, UseShellExecute = false }) ?? throw new InvalidOperationException();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>A dump that a crashed .NET program wrote (Just-In-Time debugging).</summary>
public sealed record CrashDump(string Path, string Program, int? ProcessId, DateTime Written);

/// <summary>
/// Watches the crash-dump folder (M14c): each new <c>program.pid.dmp</c> is reported once it is complete (its size no
/// longer changes), on a background thread.
/// </summary>
public sealed class CrashDumpWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public CrashDumpWatcher(string folder)
    {
        Directory.CreateDirectory(folder);
        Folder = folder;
        _watcher = new FileSystemWatcher(folder, "*.dmp") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
        _watcher.Created += (_, e) => _ = WaitAndReportAsync(e.FullPath);
        _watcher.Renamed += (_, e) => _ = WaitAndReportAsync(e.FullPath);
    }

    public string Folder { get; }

    public event EventHandler<CrashDump>? Crashed;

    /// <summary>The dumps in the folder, newest first.</summary>
    public IReadOnlyList<CrashDump> Existing() =>
        [.. Directory.EnumerateFiles(Folder, "*.dmp").Select(Describe).OrderByDescending(d => d.Written)];

    public static CrashDump Describe(string path)
    {
        // program.exe.1234.dmp → program.exe, 1234
        var name = Path.GetFileNameWithoutExtension(path);
        var dot = name.LastIndexOf('.');
        int? pid = dot > 0 && int.TryParse(name[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        return new CrashDump(path, pid is null ? name : name[..dot], pid, File.GetLastWriteTime(path));
    }

    private async Task WaitAndReportAsync(string path)
    {
        long size = -1;
        for (var i = 0; i < 600; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            long current;
            try
            {
                current = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (current > 0 && current == size)
            {
                break;
            }

            size = current;
        }

        lock (_reported)
        {
            if (!File.Exists(path) || !_reported.Add(path))
            {
                return;
            }
        }

        Crashed?.Invoke(this, Describe(path));
    }

    public void Dispose() => _watcher.Dispose();
}
