using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Codalus.Platform;
using Codalus.Workbench.Setup;
using Microsoft.Win32;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Opt-in OS integration (M51, ADR 0084): each OS's entries, written under a temporary registry key, a temporary Services
/// folder and bundle, and temporary XDG folders with stand-in tools; read back; removed exactly, with what isn't Codalus
/// IDE's left alone. Nothing is registered on the machine itself.
/// </summary>
public sealed class OsIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-os-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string RepoRoot => typeof(OsIntegrationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A property list, without fetching its DTD.</summary>
    private static XDocument LoadPlist(string path)
    {
        using var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    // ------------------------------------------------------------------ the launcher

    [Fact]
    public void The_launcher_quotes_its_command_for_each_os()
    {
        var launcher = new OsLauncher(@"C:\Users\Ann Lee\AppData\Local\Codalus\codalus.exe", ["--user-data-dir", @"D:\My Profile\"]);
        Assert.Equal(@"""C:\Users\Ann Lee\AppData\Local\Codalus\codalus.exe"" ""--user-data-dir"" ""D:\My Profile\\"" ""%1""", launcher.WindowsCommand("%1"));
        Assert.Equal(@"""a\""b""", OsLauncher.QuoteWindows("a\"b"));

        var unix = new OsLauncher("/opt/My Apps/codalus", ["--user-data-dir", "/home/ann/it's $HOME"]);
        Assert.Equal("""'/opt/My Apps/codalus' '--user-data-dir' '/home/ann/it'\''s $HOME'""", unix.ShellCommand);
        // freedesktop's Exec: quoted with ", $ escaped, then the key file's own \ escape; % doubled.
        Assert.Equal("""
            "/opt/My Apps/codalus" --user-data-dir "/home/ann/it's \\$HOME" %f
            """, unix.DesktopExec("%f"));
        Assert.Equal("/opt/codalus/codalus %F", new OsLauncher("/opt/codalus/codalus", []).DesktopExec("%F"));
        Assert.Equal("/tmp/100%% %f", new OsLauncher("/tmp/100%", []).DesktopExec("%f"));
    }

    [Fact]
    public void The_launcher_is_the_installed_apps_own_and_carries_the_profile()
    {
        // Velopack's Windows install: the launcher next to current\, which an update replaces.
        var root = Path.Combine(_dir, "Codalus");
        Directory.CreateDirectory(Path.Combine(root, "current"));
        foreach (var file in new[] { "Update.exe", "codalus.exe", Path.Combine("current", "codalus.exe") })
        {
            File.WriteAllText(Path.Combine(root, file), string.Empty);
        }

        var installed = OsLauncher.Resolve(PlatformKind.Windows, Path.Combine(root, "current", "codalus.exe"), Path.Combine(root, "current") + Path.DirectorySeparatorChar, null, _ => null, File.Exists, null);
        Assert.Equal(Path.Combine(root, "codalus.exe"), installed.Program);
        Assert.Empty(installed.Arguments);

        // A portable copy or a development build: the program that runs, with its profile.
        var portable = OsLauncher.Resolve(PlatformKind.Windows, Path.Combine(_dir, "codalus.exe"), _dir, null, _ => null, File.Exists, Path.Combine(_dir, "profile"));
        Assert.Equal(Path.Combine(_dir, "codalus.exe"), portable.Program);
        Assert.Equal(["--user-data-dir", Path.Combine(_dir, "profile")], portable.Arguments);

        // dotnet codalus.dll keeps the dll; an AppImage is its file, not the folder it mounts.
        var dll = Path.Combine(_dir, "codalus.dll");
        Assert.Equal([dll], OsLauncher.Resolve(PlatformKind.Linux, "/usr/bin/dotnet", _dir, dll, _ => null, File.Exists, null).Arguments);
        var image = Path.Combine(_dir, "Codalus.AppImage");
        File.WriteAllText(image, string.Empty);
        Assert.Equal(image, OsLauncher.Resolve(PlatformKind.Linux, "/tmp/.mount_x/usr/bin/codalus", _dir, null, n => n == "APPIMAGE" ? image : null, File.Exists, null).Program);

        Assert.Equal("/Applications/Codalus IDE.app", OsLauncher.BundleOf("/Applications/Codalus IDE.app/Contents/MacOS/codalus"));
        Assert.Null(OsLauncher.BundleOf("/Users/ann/src/codalus/bin/codalus"));
    }

    [Fact]
    public async Task Status_says_registered_partly_or_another_copys()
    {
        var data = Path.Combine(_dir, "data");
        var config = Path.Combine(_dir, "config");
        var integration = Linux(new OsLauncher("/opt/codalus/codalus", []), data, config, new List<string>());
        Assert.Equal(new OsIntegrationStatus(OsIntegrationState.NotRegistered), integration.GetStatus(OsIntegrationFeature.Folders));
        Assert.Equal(false, integration.GetStatus(OsIntegrationFeature.Folders).IsOn);

        _ = await integration.RegisterAsync(OsIntegrationFeature.Folders, Ct);
        Assert.Equal(OsIntegrationState.Registered, integration.GetStatus(OsIntegrationFeature.Folders).State);
        Assert.Equal(true, integration.GetStatus(OsIntegrationFeature.Folders).IsOn);

        // Another copy (a development build) reads the same entries as another copy's.
        var other = Linux(new OsLauncher("/home/ann/src/codalus/bin/codalus", []), data, config, new List<string>());
        Assert.Equal(OsIntegrationState.OtherCopy, other.GetStatus(OsIntegrationFeature.Folders).State);
        Assert.Null(other.GetStatus(OsIntegrationFeature.Folders).IsOn);

        File.Delete(integration.NemoAction);
        Assert.Equal(OsIntegrationState.Partial, integration.GetStatus(OsIntegrationFeature.Folders).State);
    }

    // ------------------------------------------------------------------ Windows

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_registers_folders_and_file_types_under_the_users_classes_and_removes_only_its_own()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry is Windows'.");
        var key = @"Software\Codalus.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var notified = 0;
            var launcher = new OsLauncher(@"C:\Users\Ann\AppData\Local\Codalus\codalus.exe", []);
            var windows = new WindowsOsIntegration(launcher, key, () => notified++);

            // What was there before stays: another app's verb and another app's ProgID in Open with.
            using (var classes = Registry.CurrentUser.CreateSubKey(key, writable: true))
            {
                classes.CreateSubKey(@"Directory\shell\OtherApp\command").SetValue(null, "other.exe \"%1\"");
                classes.CreateSubKey(@".sln\OpenWithProgids").SetValue("OtherIde.sln", Array.Empty<byte>(), RegistryValueKind.None);
                classes.CreateSubKey(".sln").SetValue(null, "OtherIde.sln");
            }

            var folders = await windows.RegisterAsync(OsIntegrationFeature.Folders, Ct);
            Assert.Equal(OsIntegrationState.Registered, folders.State);
            using (var verb = Registry.CurrentUser.OpenSubKey(key + @"\Directory\shell\Codalus.OpenFolder")!)
            {
                Assert.Equal("Open with Codalus IDE", verb.GetValue(null));
                Assert.Equal(@"""C:\Users\Ann\AppData\Local\Codalus\codalus.exe"",0", verb.GetValue("Icon"));
                Assert.Equal(@"""C:\Users\Ann\AppData\Local\Codalus\codalus.exe"" ""%1""", verb.OpenSubKey("command")!.GetValue(null));
            }

            Assert.Equal(@"""C:\Users\Ann\AppData\Local\Codalus\codalus.exe"" ""%V""",
                Registry.CurrentUser.OpenSubKey(key + @"\Directory\Background\shell\Codalus.OpenFolder\command")!.GetValue(null));

            var types = await windows.RegisterAsync(OsIntegrationFeature.FileTypes, Ct);
            Assert.Equal(OsIntegrationState.Registered, types.State);
            Assert.Equal(1, notified);
            foreach (var extension in OsIntegration.FileExtensions)
            {
                var progId = WindowsOsIntegration.ProgId(extension);
                Assert.Equal(@"""C:\Users\Ann\AppData\Local\Codalus\codalus.exe"" ""%1""", Registry.CurrentUser.OpenSubKey($@"{key}\{progId}\shell\open\command")!.GetValue(null));
                Assert.Equal(@"""C:\Users\Ann\AppData\Local\Codalus\codalus.exe"",0", Registry.CurrentUser.OpenSubKey($@"{key}\{progId}\DefaultIcon")!.GetValue(null));
                using var list = Registry.CurrentUser.OpenSubKey($@"{key}\{extension}\OpenWithProgids")!;
                Assert.Equal(RegistryValueKind.None, list.GetValueKind(progId));
            }

            // Windows' default stays the user's to choose: the type's own value isn't touched.
            Assert.Equal("OtherIde.sln", Registry.CurrentUser.OpenSubKey(key + @"\.sln")!.GetValue(null));
            Assert.Equal("XML Solution File", Registry.CurrentUser.OpenSubKey(key + @"\Codalus.slnx")!.GetValue(null));

            // Another copy's command reads as another copy's, and registering this one replaces it.
            var dev = new WindowsOsIntegration(new OsLauncher(@"D:\src\codalus\bin\codalus.exe", []), key, () => notified++);
            var otherStatus = dev.GetStatus(OsIntegrationFeature.Folders);
            Assert.Equal(OsIntegrationState.OtherCopy, otherStatus.State);
            Assert.Equal(@"""C:\Users\Ann\AppData\Local\Codalus\codalus.exe"" ""%1""", otherStatus.Detail);

            // One key removed by hand: partly there.
            Registry.CurrentUser.DeleteSubKeyTree(key + @"\Directory\Background\shell\Codalus.OpenFolder");
            Assert.Equal(OsIntegrationState.Partial, windows.GetStatus(OsIntegrationFeature.Folders).State);

            Assert.Equal(OsIntegrationState.NotRegistered, (await windows.UnregisterAsync(OsIntegrationFeature.Folders, Ct)).State);
            Assert.Equal(OsIntegrationState.NotRegistered, (await windows.UnregisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
            Assert.Equal(2, notified);

            using var after = Registry.CurrentUser.OpenSubKey(key)!;
            Assert.Null(after.OpenSubKey(@"Directory\shell\Codalus.OpenFolder"));
            Assert.Null(after.OpenSubKey(@"Directory\Background\shell\Codalus.OpenFolder"));
            Assert.Equal("other.exe \"%1\"", after.OpenSubKey(@"Directory\shell\OtherApp\command")!.GetValue(null));
            Assert.Equal(["OtherIde.sln"], after.OpenSubKey(@".sln\OpenWithProgids")!.GetValueNames());
            Assert.Equal("OtherIde.sln", after.OpenSubKey(".sln")!.GetValue(null));
            Assert.All(OsIntegration.FileExtensions, e => Assert.Null(after.OpenSubKey(WindowsOsIntegration.ProgId(e))));
            Assert.All(OsIntegration.FileExtensions.Skip(1), e => Assert.Null(after.OpenSubKey(e))); // the keys it created
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            using var tests = Registry.CurrentUser.OpenSubKey(@"Software\Codalus.Tests");
            if (tests is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Codalus.Tests", throwOnMissingSubKey: false);
            }
        }
    }

    // ------------------------------------------------------------------ Linux

    private static LinuxOsIntegration Linux(OsLauncher launcher, string data, string config, List<string> calls, bool xdgMime = true, Action<IReadOnlyList<string>>? onXdgMime = null) =>
        new(launcher, data, config,
            (program, arguments, _, _) =>
            {
                calls.Add(program + " " + string.Join(' ', arguments));
                if (program.EndsWith("xdg-mime", StringComparison.Ordinal))
                {
                    onXdgMime?.Invoke(arguments);
                }

                return Task.FromResult((0, string.Empty));
            },
            name => name == "xdg-mime" && !xdgMime ? null : "/usr/bin/" + name);

    [Fact]
    public async Task Linux_adds_the_folder_actions_of_each_file_manager_and_removes_only_its_own()
    {
        var data = Path.Combine(_dir, "data");
        var config = Path.Combine(_dir, "config");
        var launcher = new OsLauncher("/opt/codalus/codalus", []);
        var linux = Linux(launcher, data, config, []);

        // Thunar's file has its default action; a script of the user's sits next to where Nautilus's goes.
        Directory.CreateDirectory(Path.GetDirectoryName(linux.ThunarActions)!);
        const string thunar = """
            <?xml version="1.0" encoding="UTF-8"?>
            <actions>
            <action>
            	<icon>utilities-terminal</icon>
            	<name>Open Terminal Here</name>
            	<unique-id>1600000000000000-1</unique-id>
            	<command>exo-open --working-directory %f --launch TerminalEmulator</command>
            	<patterns>*</patterns>
            	<directories/>
            </action>
            </actions>
            """;
        File.WriteAllText(linux.ThunarActions, thunar);
        var mine = Path.Combine(Path.GetDirectoryName(linux.NautilusScript)!, "My script");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "#!/bin/sh\n");

        Assert.Equal(
            [linux.NautilusScript, linux.DolphinServiceMenu, linux.NemoAction, linux.NemoBackgroundAction, linux.ThunarActions],
            linux.Entries(OsIntegrationFeature.Folders).Select(e => e.Name));
        Assert.Equal(OsIntegrationState.Registered, (await linux.RegisterAsync(OsIntegrationFeature.Folders, Ct)).State);

        var script = await File.ReadAllTextAsync(linux.NautilusScript, Ct);
        Assert.StartsWith("#!/bin/sh\n# codalus-os-integration", script, StringComparison.Ordinal);
        Assert.Contains("'/opt/codalus/codalus' \"$folder\"", script, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(linux.NautilusScript).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.GetUnixFileMode(linux.DolphinServiceMenu).HasFlag(UnixFileMode.UserExecute)); // KDE 6 runs only executable ones
        }

        var dolphin = await File.ReadAllTextAsync(linux.DolphinServiceMenu, Ct);
        Assert.Contains("MimeType=inode/directory;", dolphin, StringComparison.Ordinal);
        Assert.Contains("Name=Open with Codalus IDE\nIcon=codalus\nExec=/opt/codalus/codalus %f", dolphin, StringComparison.Ordinal);
        Assert.Contains("Exec=/opt/codalus/codalus %F\n", await File.ReadAllTextAsync(linux.NemoAction, Ct), StringComparison.Ordinal);
        Assert.Contains("Extensions=dir;", await File.ReadAllTextAsync(linux.NemoAction, Ct), StringComparison.Ordinal);
        var background = await File.ReadAllTextAsync(linux.NemoBackgroundAction, Ct);
        Assert.Contains("Exec=/opt/codalus/codalus %P\n", background, StringComparison.Ordinal);
        Assert.Contains("Selection=None", background, StringComparison.Ordinal);

        var actions = XDocument.Load(linux.ThunarActions).Root!.Elements("action").ToList();
        Assert.Equal(["Open Terminal Here", "Open with Codalus IDE"], actions.Select(a => a.Element("name")!.Value));
        Assert.Equal("'/opt/codalus/codalus' %f", actions[1].Element("command")!.Value);

        // Registering again (or for another copy) doesn't add a second Thunar action.
        _ = await linux.RegisterAsync(OsIntegrationFeature.Folders, Ct);
        Assert.Equal(2, XDocument.Load(linux.ThunarActions).Root!.Elements("action").Count());

        Assert.Equal(OsIntegrationState.NotRegistered, (await linux.UnregisterAsync(OsIntegrationFeature.Folders, Ct)).State);
        Assert.False(File.Exists(linux.NautilusScript));
        Assert.True(File.Exists(mine));
        Assert.False(Directory.Exists(Path.Combine(data, "kio"))); // the folders it created, emptied: gone
        Assert.False(Directory.Exists(Path.Combine(data, "nemo")));
        Assert.Equal(["Open Terminal Here"], XDocument.Load(linux.ThunarActions).Root!.Elements("action").Select(a => a.Element("name")!.Value));
        Assert.Contains("exo-open --working-directory %f", await File.ReadAllTextAsync(linux.ThunarActions, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_leaves_a_file_that_isnt_its_own_and_reads_it_as_partly_there()
    {
        var linux = Linux(new OsLauncher("/opt/codalus/codalus", []), Path.Combine(_dir, "data"), Path.Combine(_dir, "config"), []);
        Directory.CreateDirectory(Path.GetDirectoryName(linux.NemoAction)!);
        File.WriteAllText(linux.NemoAction, "[Nemo Action]\nName=Mine\n");

        Assert.Equal(OsIntegrationState.Partial, (await linux.RegisterAsync(OsIntegrationFeature.Folders, Ct)).State);
        Assert.Equal("[Nemo Action]\nName=Mine\n", await File.ReadAllTextAsync(linux.NemoAction, Ct));
        _ = await linux.UnregisterAsync(OsIntegrationFeature.Folders, Ct);
        Assert.Equal("[Nemo Action]\nName=Mine\n", await File.ReadAllTextAsync(linux.NemoAction, Ct));
        Assert.False(File.Exists(linux.NemoBackgroundAction));
    }

    [Fact]
    public async Task Linux_registers_the_mime_types_and_the_default_application_with_the_users_tools()
    {
        var data = Path.Combine(_dir, "data");
        var config = Path.Combine(_dir, "config");
        var calls = new List<string>();
        LinuxOsIntegration? linux = null;

        // The stand-in xdg-mime does what the real one does here: the XDG mimeapps.list.
        void XdgMime(IReadOnlyList<string> arguments)
        {
            Assert.Equal("default", arguments[0]);
            var lines = File.Exists(linux!.MimeAppsList) ? File.ReadAllLines(linux.MimeAppsList).ToList() : [];
            var at = lines.IndexOf("[Default Applications]") + 1;
            var end = lines.FindIndex(at, l => l.StartsWith('['));
            for (var i = (end < 0 ? lines.Count : end) - 1; i >= at; i--)
            {
                if (arguments.Skip(2).Any(t => lines[i].StartsWith(t + "=", StringComparison.Ordinal)))
                {
                    lines.RemoveAt(i);
                }
            }

            lines.InsertRange(at, arguments.Skip(2).Select(t => $"{t}={arguments[1]};"));
            File.WriteAllLines(linux.MimeAppsList, lines);
        }

        linux = Linux(new OsLauncher("/home/ann/Apps/Codalus IDE.AppImage", []), data, config, calls, onXdgMime: XdgMime);
        Directory.CreateDirectory(config);
        const string before = "[Default Applications]\ntext/html=firefox.desktop;\n\n[Added Associations]\napplication/x-csproj=rider.desktop;\n";
        File.WriteAllText(linux.MimeAppsList, before);

        Assert.Equal(OsIntegrationState.Registered, (await linux.RegisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
        Assert.Equal(
        [
            $"/usr/bin/update-mime-database {Path.Combine(data, "mime")}",
            $"/usr/bin/update-desktop-database {Path.Combine(data, "applications")}",
            "/usr/bin/xdg-mime default codalus-solutions.desktop application/x-sln application/x-slnx application/x-slnf application/x-csproj application/x-vbproj application/x-fsproj",
        ], calls);

        var package = XDocument.Load(linux.MimePackage);
        XNamespace mime = "http://www.freedesktop.org/standards/shared-mime-info";
        Assert.Equal(["*.sln", "*.slnx", "*.slnf", "*.csproj", "*.vbproj", "*.fsproj"], package.Root!.Elements(mime + "mime-type").Select(t => t.Element(mime + "glob")!.Attribute("pattern")!.Value));
        Assert.Equal("application/xml", package.Root.Elements(mime + "mime-type").Single(t => t.Attribute("type")!.Value == "application/x-csproj").Element(mime + "sub-class-of")!.Attribute("type")!.Value);

        var desktop = await File.ReadAllTextAsync(linux.DesktopEntry, Ct);
        Assert.Contains("Exec=\"/home/ann/Apps/Codalus IDE.AppImage\" %f\n", desktop, StringComparison.Ordinal);
        Assert.Contains("NoDisplay=true\n", desktop, StringComparison.Ordinal);
        Assert.Contains("MimeType=application/x-sln;application/x-slnx;application/x-slnf;application/x-csproj;application/x-vbproj;application/x-fsproj;\n", desktop, StringComparison.Ordinal);
        Assert.Contains("text/html=firefox.desktop;", await File.ReadAllTextAsync(linux.MimeAppsList, Ct), StringComparison.Ordinal);

        // Another app made the default for one type since: partly there.
        await File.WriteAllTextAsync(linux.MimeAppsList, (await File.ReadAllTextAsync(linux.MimeAppsList, Ct)).Replace("application/x-sln=codalus-solutions.desktop;", "application/x-sln=rider.desktop;", StringComparison.Ordinal), Ct);
        Assert.Equal(OsIntegrationState.Partial, linux.GetStatus(OsIntegrationFeature.FileTypes).State);

        calls.Clear();
        Assert.Equal(OsIntegrationState.NotRegistered, (await linux.UnregisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
        Assert.False(File.Exists(linux.MimePackage));
        Assert.False(File.Exists(linux.DesktopEntry));
        Assert.Equal([$"/usr/bin/update-mime-database {Path.Combine(data, "mime")}", $"/usr/bin/update-desktop-database {Path.Combine(data, "applications")}"], calls);
        var after = await File.ReadAllTextAsync(linux.MimeAppsList, Ct);
        Assert.DoesNotContain("codalus-solutions.desktop", after, StringComparison.Ordinal);
        Assert.Contains("application/x-sln=rider.desktop;", after, StringComparison.Ordinal); // the user's later choice stays
        Assert.Contains("text/html=firefox.desktop;", after, StringComparison.Ordinal);
        Assert.Contains("[Added Associations]\napplication/x-csproj=rider.desktop;", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_without_xdg_mime_writes_the_defaults_itself_and_gives_them_back()
    {
        var calls = new List<string>();
        var linux = Linux(new OsLauncher("/opt/codalus/codalus", []), Path.Combine(_dir, "data"), Path.Combine(_dir, "config"), calls, xdgMime: false);
        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(linux.MimeAppsList, "# mine\n[Default Applications]\napplication/x-csproj=rider.desktop;\n\n[Removed Associations]\ntext/plain=gedit.desktop;\n");

        Assert.Equal(OsIntegrationState.Registered, (await linux.RegisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
        Assert.DoesNotContain(calls, c => c.Contains("xdg-mime", StringComparison.Ordinal));
        var list = await File.ReadAllTextAsync(linux.MimeAppsList, Ct);
        Assert.Contains("application/x-csproj=codalus-solutions.desktop;rider.desktop;\n", list, StringComparison.Ordinal);
        Assert.Contains("application/x-fsproj=codalus-solutions.desktop;\n\n[Removed Associations]", list, StringComparison.Ordinal);

        _ = await linux.UnregisterAsync(OsIntegrationFeature.FileTypes, Ct);
        Assert.Equal("# mine\n[Default Applications]\napplication/x-csproj=rider.desktop;\n\n[Removed Associations]\ntext/plain=gedit.desktop;\n", await File.ReadAllTextAsync(linux.MimeAppsList, Ct));
    }

    // ------------------------------------------------------------------ macOS

    private sealed class StandInLaunchServices : ILaunchServices
    {
        public Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, List<string>> All { get; } = new(StringComparer.Ordinal);

        public string? TypeForExtension(string extension) => "io.github.stavroskasidis.codalus." + extension;

        public string? DefaultHandler(string contentType) => Defaults.GetValueOrDefault(contentType);

        public IReadOnlyList<string> Handlers(string contentType) => All.GetValueOrDefault(contentType) ?? [];

        public bool SetDefaultHandler(string contentType, string bundleId)
        {
            Defaults[contentType] = bundleId;
            return true;
        }
    }

    private (MacOsIntegration Mac, StandInLaunchServices Services, List<string> Calls, string Bundle) Mac(bool bundle = true)
    {
        var app = Path.Combine(_dir, "Applications", "Codalus IDE.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>CFBundleName</key><string>Codalus IDE</string><key>CFBundleIdentifier</key><string>io.github.stavroskasidis.codalus</string></dict></plist>
            """);
        var launcher = new OsLauncher(app + "/Contents/MacOS/codalus", []) { Bundle = bundle ? app : null };
        var services = new StandInLaunchServices();
        var calls = new List<string>();
        var mac = new MacOsIntegration(launcher, Path.Combine(_dir, "Services"), Path.Combine(_dir, "state", "os-integration.txt"), services,
            (program, arguments, _, _) =>
            {
                calls.Add(program + " " + string.Join(' ', arguments));
                return Task.FromResult((0, string.Empty));
            },
            _ => true);
        return (mac, services, calls, app);
    }

    [Fact]
    public async Task MacOS_adds_a_Finder_quick_action_for_folders_and_removes_only_its_own()
    {
        var (mac, _, calls, app) = Mac();
        Assert.Equal(OsIntegrationState.Registered, (await mac.RegisterAsync(OsIntegrationFeature.Folders, Ct)).State);

        var info = await File.ReadAllTextAsync(Path.Combine(mac.Workflow, "Contents", "Info.plist"), Ct);
        Assert.Contains("<string>Open with Codalus IDE</string>", info, StringComparison.Ordinal);
        Assert.Contains("<string>public.folder</string>", info, StringComparison.Ordinal);
        Assert.Contains("<string>com.apple.finder</string>", info, StringComparison.Ordinal);
        var workflow = LoadPlist(Path.Combine(mac.Workflow, "Contents", "document.wflow"));
        var command = workflow.Descendants("key").Where(k => k.Value == "COMMAND_STRING").Select(k => k.ElementsAfterSelf().First()).Single(v => v.Name == "string").Value;
        Assert.Contains($"open -n -a '{app}' --args \"$folder\"", command, StringComparison.Ordinal);
        Assert.Contains("com.apple.Automator.servicesMenu", workflow.ToString(), StringComparison.Ordinal);
        Assert.Equal(["/System/Library/CoreServices/pbs -update"], calls);

        // A Quick Action of the user's with the same name isn't touched.
        _ = await mac.UnregisterAsync(OsIntegrationFeature.Folders, Ct);
        Assert.False(Directory.Exists(mac.Workflow));
        Directory.CreateDirectory(Path.Combine(mac.Workflow, "Contents"));
        File.WriteAllText(Path.Combine(mac.Workflow, "Contents", "document.wflow"), "<plist/>");
        Assert.Equal(OsIntegrationState.NotRegistered, mac.GetStatus(OsIntegrationFeature.Folders).State);
        _ = await mac.RegisterAsync(OsIntegrationFeature.Folders, Ct);
        _ = await mac.UnregisterAsync(OsIntegrationFeature.Folders, Ct);
        Assert.Equal("<plist/>", await File.ReadAllTextAsync(Path.Combine(mac.Workflow, "Contents", "document.wflow"), Ct));
    }

    [Fact]
    public async Task MacOS_makes_the_bundle_the_default_app_and_gives_each_type_back()
    {
        var (mac, services, calls, app) = Mac();
        Assert.Equal("io.github.stavroskasidis.codalus", mac.BundleId);
        services.Defaults["io.github.stavroskasidis.codalus.sln"] = "com.microsoft.VSCode";
        services.All["io.github.stavroskasidis.codalus.csproj"] = ["io.github.stavroskasidis.codalus", "com.apple.TextEdit"];

        Assert.Equal(OsIntegrationState.Registered, (await mac.RegisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
        Assert.Equal([$"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f {app}"], calls);
        Assert.All(OsIntegration.FileExtensions, e => Assert.Equal("io.github.stavroskasidis.codalus", services.Defaults["io.github.stavroskasidis.codalus" + e]));
        // The types it took, with the app before (none for most).
        var state = await File.ReadAllLinesAsync(mac.StateFile, Ct);
        Assert.Equal(6, state.Length);
        Assert.Contains("io.github.stavroskasidis.codalus.sln\tcom.microsoft.VSCode", state);
        Assert.Contains("io.github.stavroskasidis.codalus.slnx\t", state);

        Assert.Equal(OsIntegrationState.NotRegistered, (await mac.UnregisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
        Assert.Equal("com.microsoft.VSCode", services.Defaults["io.github.stavroskasidis.codalus.sln"]); // the one before
        Assert.Equal("com.apple.TextEdit", services.Defaults["io.github.stavroskasidis.codalus.csproj"]); // another app that opens it
        Assert.False(File.Exists(mac.StateFile));

        // A default the user chose in the Finder isn't Codalus IDE's to take back.
        services.Defaults["io.github.stavroskasidis.codalus.sln"] = "io.github.stavroskasidis.codalus";
        Assert.Equal(OsIntegrationState.NotRegistered, mac.GetStatus(OsIntegrationFeature.FileTypes).State);
        _ = await mac.UnregisterAsync(OsIntegrationFeature.FileTypes, Ct);
        Assert.Equal("io.github.stavroskasidis.codalus", services.Defaults["io.github.stavroskasidis.codalus.sln"]);

        // Outside the app bundle (a development build), the file types can't be offered; the folders can.
        var (dev, _, _, _) = Mac(bundle: false);
        Assert.Equal(OsIntegrationState.Unavailable, dev.GetStatus(OsIntegrationFeature.FileTypes).State);
        Assert.Equal(OsIntegrationState.Unavailable, (await dev.RegisterAsync(OsIntegrationFeature.FileTypes, Ct)).State);
        Assert.Equal(OsIntegrationState.NotRegistered, dev.GetStatus(OsIntegrationFeature.Folders).State);
    }

    [Fact]
    public void The_macOS_bundle_declares_the_file_types_without_taking_them()
    {
        var plist = LoadPlist(Path.Combine(RepoRoot, "eng", "packaging", "macos", "Info.plist"));
        var root = plist.Root!.Element("dict")!;
        XElement ValueOf(XElement dict, string key) => dict.Elements("key").Single(k => k.Value == key).ElementsAfterSelf().First();
        var documents = ValueOf(root, "CFBundleDocumentTypes").Elements("dict").ToList();
        var declared = documents.SelectMany(d => ValueOf(d, "LSItemContentTypes").Elements("string").Select(s => s.Value)).ToHashSet();
        Assert.All(documents, d => Assert.True(ValueOf(d, "LSHandlerRank").Value is "Alternate" or "None"));
        var imported = ValueOf(root, "UTImportedTypeDeclarations").Elements("dict").ToList();
        foreach (var extension in OsIntegration.FileExtensions)
        {
            var type = imported.Single(t => ValueOf(ValueOf(t, "UTTypeTagSpecification"), "public.filename-extension").Elements("string").Any(s => "." + s.Value == extension));
            Assert.Contains(ValueOf(type, "UTTypeIdentifier").Value, declared);
        }
    }
}
