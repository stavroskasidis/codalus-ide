using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Codalus.Platform;
using Codalus.Platform.Credentials;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Secrets;

namespace Codalus.Workbench.Tests;

/// <summary>
/// MAUI archives and distribution (ADR 0114) with a stand-in build and stand-in tools: nothing builds, signs, installs a
/// certificate or uploads. The commands each channel runs, the passwords' way to the tools (their environment, never
/// the command line), the archives' files and Xcode's, and what each channel needs before it can sign.
/// </summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-archives-").FullName;
    private readonly List<ToolCommand> _commands = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_dir, "data", "archives");

    private string Xcode => Path.Combine(_dir, "home", "Library", "Developer", "Xcode", "Archives");

    private string Jdk => Path.Combine(_dir, "jdk");

    private string Sdk => Path.Combine(_dir, "android-sdk");

    /// <summary>A service on <paramref name="platform"/> whose tools are <paramref name="tool"/> and whose build is <paramref name="build"/>.</summary>
    private ArchiveService Service(
        PlatformKind platform,
        Func<ToolCommand, Action<string>?, int>? tool = null,
        Func<IReadOnlyDictionary<string, string>, BuildResultInfo>? build = null,
        SecretStorage? secrets = null)
    {
        var archives = new AppArchives(Root, platform == PlatformKind.MacOS ? Xcode : null);
        var identities = new SigningIdentities(Path.Combine(_dir, "data", "signing.json"), secrets ?? new SecretStorage(new InMemoryCredentialStore()));
        return new ArchiveService(
            platform,
            archives,
            identities,
            () => new AndroidToolchain.Locations(Sdk, Jdk, null, null),
            (command, onLine, _) =>
            {
                _commands.Add(command);
                return Task.FromResult(tool?.Invoke(command, onLine) ?? 0);
            },
            (_, _, properties, _) => Task.FromResult(build?.Invoke(properties) ?? new BuildResultInfo(true, false, 0, 0, 1)),
            Path.Combine(_dir, "home"),
            name => name == "NUGET_PACKAGES" ? Path.Combine(_dir, "nuget") : null);
    }

    [Fact]
    public void Property_lists_are_read_and_written()
    {
        var xml = PropertyList.Write(
        [
            new("method", "app-store-connect"),
            new("uploadSymbols", true),
            new("count", 3),
            new("provisioningProfiles", new List<KeyValuePair<string, object>> { new("dev.example.app", "UUID-1") }),
            new("list", new List<object> { "a", "b" }),
        ]);
        Assert.Contains("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\"", xml, StringComparison.Ordinal);
        var read = PropertyList.ParseDictionary(xml)!;
        Assert.Equal("app-store-connect", read["method"]);
        Assert.Equal(true, read["uploadSymbols"]);
        Assert.Equal(3L, read["count"]);
        Assert.Equal("UUID-1", PropertyList.String(read, "provisioningProfiles", "dev.example.app"));
        Assert.Equal(["a", "b"], ((IReadOnlyList<object>)read["list"]).Cast<string>());
        Assert.Null(PropertyList.ParseDictionary("not xml"));
    }

    [Fact]
    public void Build_numbers_go_up_by_one()
    {
        Assert.Equal("2", AppArchives.NextBuildNumber("1"));
        Assert.Equal("1.0.4", AppArchives.NextBuildNumber("1.0.3"));
        Assert.Equal("10", AppArchives.NextBuildNumber("9"));
        Assert.Equal("1", AppArchives.NextBuildNumber(null));
        Assert.Equal("beta.1", AppArchives.NextBuildNumber("beta"));
    }

    [Fact]
    public void Each_platform_archives_with_its_own_sdk_properties()
    {
        var android = ArchiveService.Properties(new ArchiveRequest("/a/App.csproj", "net10.0-android", MobilePlatform.Android), "/arch/packages", Architecture.Arm64);
        Assert.Equal("net10.0-android", android["TargetFramework"]);
        Assert.Equal("Release", android["Configuration"]);
        Assert.Equal("true", android["_IsPublishing"]);
        Assert.Equal("aab;apk", android["AndroidPackageFormats"]);
        Assert.Equal("false", android["AndroidKeyStore"]); // the debug key; Distribute signs with the user's
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), android["PublishDir"], StringComparison.Ordinal);

        var ios = ArchiveService.Properties(new ArchiveRequest("/a/App.csproj", "net10.0-ios", MobilePlatform.iOS), "/arch/packages", Architecture.Arm64);
        Assert.Equal(("ios-arm64", "true"), (ios["RuntimeIdentifier"], ios["ArchiveOnBuild"]));
        var mac = ArchiveService.Properties(new ArchiveRequest("/a/App.csproj", "net10.0-maccatalyst", MobilePlatform.MacCatalyst), "/arch/packages", Architecture.Arm64);
        Assert.Equal("true", mac["ArchiveOnBuild"]);
        Assert.False(mac.ContainsKey("RuntimeIdentifier")); // both architectures, as the SDK publishes a Mac app

        var msix = ArchiveService.Properties(new ArchiveRequest("/a/App.csproj", "net10.0-windows10.0.19041.0", MobilePlatform.Windows) { WindowsPackageType = "MSIX" }, "/arch/packages", Architecture.X64);
        Assert.Equal(("win-x64", "MSIX", "true", "false"), (msix["RuntimeIdentifier"], msix["WindowsPackageType"], msix["GenerateAppxPackageOnBuild"], msix["AppxPackageSigningEnabled"]));
        var unpackaged = ArchiveService.Properties(new ArchiveRequest("/a/App.csproj", "net10.0-windows10.0.19041.0", MobilePlatform.Windows) { WindowsPackageType = "None" }, "/arch/packages", Architecture.Arm64);
        Assert.Equal(("win-arm64", "true", "true"), (unpackaged["RuntimeIdentifier"], unpackaged["SelfContained"], unpackaged["WindowsAppSDKSelfContained"]));
        Assert.False(unpackaged.ContainsKey("WindowsPackageType"));
    }

    [Fact]
    public async Task An_android_archive_keeps_the_packages_the_publish_made()
    {
        var service = Service(PlatformKind.Linux, build: properties =>
        {
            var folder = properties["PublishDir"];
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "dev.example.todo.aab"), "unsigned");
            File.WriteAllText(Path.Combine(folder, "dev.example.todo-Signed.aab"), "aab");
            File.WriteAllText(Path.Combine(folder, "dev.example.todo-Signed.apk"), "apk");
            return new BuildResultInfo(true, false, 0, 0, 1);
        });
        var result = await service.ArchiveAsync(new ArchiveRequest("/src/Todo/Todo.csproj", "net10.0-android", MobilePlatform.Android) { Name = "Todo", ApplicationId = "dev.example.todo", DisplayVersion = "1.2", Version = "7" }, Ct);
        var archive = Assert.IsType<AppArchive>(result.Archive);
        Assert.Equal("1.2 (7)", archive.VersionLabel);
        Assert.Equal(3, archive.Packages.Count);
        Assert.StartsWith(Path.Combine(Root, "Todo"), archive.Path, StringComparison.Ordinal);

        // It is read back from its archive.json, and the channels pick their package (the signed one first).
        var listed = Assert.Single(service.Archives.List());
        Assert.Equal((archive.Path, MobilePlatform.Android, "dev.example.todo", "/src/Todo/Todo.csproj"), (listed.Path, listed.Platform, listed.ApplicationId, listed.Project));
        Assert.Equal("dev.example.todo-Signed.aab", Path.GetFileName(Distribution.AndroidPackage(listed, DistributionChannel.GooglePlay)));
        Assert.Equal("dev.example.todo-Signed.apk", Path.GetFileName(Distribution.AndroidPackage(listed, DistributionChannel.AdHoc)));
        Assert.Equal([DistributionChannel.AdHoc, DistributionChannel.GooglePlay], Distribution.Channels(listed));

        service.Archives.Delete(listed);
        Assert.Empty(service.Archives.List());
    }

    [Fact]
    public async Task A_failed_or_empty_build_leaves_no_archive()
    {
        var failed = Service(PlatformKind.Linux, build: _ => new BuildResultInfo(false, false, 2, 0, 1));
        var result = await failed.ArchiveAsync(new ArchiveRequest("/src/Todo/Todo.csproj", "net10.0-android", MobilePlatform.Android), Ct);
        Assert.Null(result.Archive);
        Assert.Contains("2 error(s)", result.Message, StringComparison.Ordinal);

        var empty = await Service(PlatformKind.Linux).ArchiveAsync(new ArchiveRequest("/src/Todo/Todo.csproj", "net10.0-android", MobilePlatform.Android), Ct);
        Assert.Null(empty.Archive);
        Assert.Contains("no package", empty.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Root, "Todo")) && Directory.GetFileSystemEntries(Path.Combine(Root, "Todo")).Length > 0);

        // Apple's archives are made on a Mac only.
        var ios = await Service(PlatformKind.Linux).ArchiveAsync(new ArchiveRequest("/src/Todo/Todo.csproj", "net10.0-ios", MobilePlatform.iOS), Ct);
        Assert.Contains("on a Mac", ios.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keystores_are_created_and_imported_with_their_passwords_in_the_credential_store()
    {
        var store = new InMemoryCredentialStore();
        var secrets = new SecretStorage(store);
        var service = Service(PlatformKind.Linux, secrets: secrets, tool: (command, onLine) =>
        {
            if (command.Arguments[0] == "-genkeypair")
            {
                File.WriteAllText(command.Arguments[command.Arguments.ToList().IndexOf("-keystore") + 1], "keystore");
                return 0;
            }

            // -list: the password is right only when it is "secret1".
            if (command.Environment![Distribution.StorePasswordVariable] != "secret1")
            {
                onLine?.Invoke("keytool error: java.io.IOException: keystore password was incorrect");
                return 1;
            }

            return 0;
        });
        var path = Path.Combine(_dir, "keys", "todo.keystore");
        var (created, message) = await service.CreateKeystoreAsync("Todo upload key", path, "upload", "secret1", Distribution.DistinguishedName("Jane Doe", "Example, Ltd", "GB"), 30, null, Ct);
        Assert.NotNull(created);
        Assert.Contains("Google Play", message, StringComparison.Ordinal);
        var genkey = _commands.Single();
        Assert.Equal(Path.Combine(Jdk, "bin", "keytool"), genkey.Program);
        Assert.Contains("CN=Jane Doe, O=Example\\, Ltd, C=GB", genkey.Arguments);
        Assert.Contains(((int)(30 * 365)).ToString(System.Globalization.CultureInfo.InvariantCulture), genkey.Arguments);
        Assert.DoesNotContain("secret1", genkey.Arguments); // the password goes through the environment
        Assert.Equal(["-storepass:env", Distribution.StorePasswordVariable], genkey.Arguments.SkipWhile(a => a != "-storepass:env").Take(2));

        // Only the path and alias are in the file; the passwords are in the credential store.
        var file = File.ReadAllText(service.Identities.File);
        Assert.Contains("todo.keystore", file, StringComparison.Ordinal);
        Assert.DoesNotContain("secret1", file, StringComparison.Ordinal);
        Assert.Equal(("secret1", "secret1"), await service.Identities.KeystorePasswordsAsync(created, Ct));

        // A short password, an existing file and a wrong password are refused.
        Assert.Null((await service.CreateKeystoreAsync("x", Path.Combine(_dir, "keys", "x.keystore"), "a", "12345", "CN=x", 1, null, Ct)).Keystore);
        Assert.Contains("already exists", (await service.CreateKeystoreAsync("x", path, "a", "123456", "CN=x", 1, null, Ct)).Message, StringComparison.Ordinal);
        var wrong = await service.ImportKeystoreAsync("Again", path, "upload", "nope", null, Ct);
        Assert.Null(wrong.Keystore);
        Assert.Equal("The keystore's password is wrong.", wrong.Message);
        var imported = await service.ImportKeystoreAsync("Again", path, "upload", "secret1", "keypass", Ct);
        Assert.Equal(("secret1", "keypass"), await service.Identities.KeystorePasswordsAsync(imported.Keystore!, Ct));
        Assert.Equal(2, service.Identities.Keystores().Count);

        await service.Identities.RemoveAsync(created.Id, Ct);
        Assert.Equal(["Again"], service.Identities.Keystores().Select(k => k.Name));
        Assert.Equal(((string?)null, (string?)null), await service.Identities.KeystorePasswordsAsync(created, Ct));
        Assert.True(File.Exists(path)); // the file stays
    }

    private async Task<(ArchiveService Service, AppArchive Archive, AndroidKeystore Keystore)> AndroidArchiveAsync()
    {
        var service = Service(PlatformKind.Linux, tool: (command, _) =>
        {
            if (command.Arguments.Contains("--out"))
            {
                File.WriteAllText(command.Arguments[command.Arguments.ToList().IndexOf("--out") + 1], "signed apk");
            }

            return 0;
        });
        Directory.CreateDirectory(Path.Combine(Sdk, "build-tools", "35.0.0"));
        Directory.CreateDirectory(Path.Combine(Sdk, "build-tools", "36.0.0"));
        File.WriteAllText(Path.Combine(Sdk, "build-tools", "35.0.0", "apksigner"), string.Empty);
        File.WriteAllText(Path.Combine(Sdk, "build-tools", "36.0.0", "apksigner"), string.Empty);

        var folder = Path.Combine(Root, "Todo", "2026-11-28 10-00-00 Android");
        var packages = Directory.CreateDirectory(Path.Combine(folder, "packages")).FullName;
        var aab = Path.Combine(packages, "dev.example.todo-Signed.aab");
        using (var zip = ZipFile.Open(aab, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "base/manifest/AndroidManifest.xml", "META-INF/MANIFEST.MF", "META-INF/ANDROIDD.SF", "META-INF/ANDROIDD.RSA", "META-INF/services/x.Provider" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(entry);
            }
        }

        File.WriteAllText(Path.Combine(packages, "dev.example.todo-Signed.apk"), "debug apk");
        var archive = new AppArchive(folder, MobilePlatform.Android, "Todo", DateTimeOffset.UtcNow)
        {
            ApplicationId = "dev.example.todo",
            Version = "7",
            Packages = [aab, Path.Combine(packages, "dev.example.todo-Signed.apk")],
        };
        AppArchives.Save(archive);
        var keystore = await service.Identities.AddKeystoreAsync("Upload", Path.Combine(_dir, "upload.keystore"), "upload", "storepw", "keypw", Ct);
        return (service, archive, keystore);
    }

    [Fact]
    public async Task Ad_hoc_signs_the_apk_with_apksigner_from_the_newest_build_tools()
    {
        var (service, archive, keystore) = await AndroidArchiveAsync();
        Assert.Equal("Choose a signing key, or create one.", service.Missing(archive, DistributionChannel.AdHoc, new DistributionSigning()));

        var result = await service.DistributeAsync(archive, DistributionChannel.AdHoc, new DistributionSigning { Keystore = keystore }, null, Ct);
        Assert.True(result.Succeeded, result.Message);
        var signed = Assert.Single(result.Files);
        Assert.Equal("dev.example.todo-7.apk", Path.GetFileName(signed));
        Assert.StartsWith(Path.Combine(archive.Path, "Distribution", "Ad Hoc "), result.Folder, StringComparison.Ordinal);

        var command = _commands.Single();
        Assert.Equal(Path.Combine(Sdk, "build-tools", "36.0.0", "apksigner"), command.Program);
        Assert.Equal(["sign", "--ks", keystore.Path, "--ks-key-alias", "upload", "--ks-pass", "env:" + Distribution.StorePasswordVariable, "--key-pass", "env:" + Distribution.KeyPasswordVariable, "--out", signed, archive.Packages[1]], command.Arguments);
        Assert.Equal(("storepw", "keypw", Jdk), (command.Environment![Distribution.StorePasswordVariable], command.Environment[Distribution.KeyPasswordVariable], command.Environment["JAVA_HOME"]));
    }

    [Fact]
    public async Task Google_play_signs_a_copy_of_the_bundle_with_jarsigner_alone()
    {
        var (service, archive, keystore) = await AndroidArchiveAsync();
        var result = await service.DistributeAsync(archive, DistributionChannel.GooglePlay, new DistributionSigning { Keystore = keystore }, null, Ct);
        Assert.True(result.Succeeded, result.Message);
        var bundle = Assert.Single(result.Files);
        Assert.Equal("dev.example.todo-7.aab", Path.GetFileName(bundle));

        // The debug key's signature is gone from the copy (and only from the copy); META-INF's other files stay.
        using (var zip = ZipFile.OpenRead(bundle))
        {
            Assert.Equal(["META-INF/services/x.Provider", "base/manifest/AndroidManifest.xml"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }

        using (var original = ZipFile.OpenRead(archive.Packages[0]))
        {
            Assert.Equal(5, original.Entries.Count);
        }

        var command = _commands.Single();
        Assert.Equal(Path.Combine(Jdk, "bin", "jarsigner"), command.Program);
        Assert.Equal(["-keystore", keystore.Path, "-storepass:env", Distribution.StorePasswordVariable, "-keypass:env", Distribution.KeyPasswordVariable, "-sigalg", "SHA256withRSA", "-digestalg", "SHA-256", bundle, "upload"], command.Arguments);
        Assert.Equal(new Uri("https://play.google.com/console"), Distribution.UploadPage(DistributionChannel.GooglePlay));
    }

    [Fact]
    public async Task A_failed_signing_leaves_no_distribution()
    {
        var (_, archive, keystore) = await AndroidArchiveAsync();
        var failing = Service(PlatformKind.Linux, tool: (_, onLine) => { onLine?.Invoke("Failed to load signer"); return 1; });
        await failing.Identities.AddKeystoreAsync("Upload", keystore.Path, "upload", "storepw", null, Ct);
        var result = await failing.DistributeAsync(archive, DistributionChannel.GooglePlay, new DistributionSigning { Keystore = failing.Identities.Keystores()[^1] }, null, Ct);
        Assert.False(result.Succeeded);
        Assert.Contains("exit code 1", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(result.Folder));

        // A keystore whose passwords the credential store lost is explained.
        var lost = await failing.DistributeAsync(archive, DistributionChannel.AdHoc, new DistributionSigning { Keystore = keystore }, null, Ct);
        Assert.Contains("no password", lost.Message, StringComparison.Ordinal);
    }

    private static string XcodeInfo(string bundleId, string path) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict>
          <key>ApplicationProperties</key><dict>
            <key>ApplicationPath</key><string>{path}</string>
            <key>CFBundleIdentifier</key><string>{bundleId}</string>
            <key>CFBundleShortVersionString</key><string>1.2</string>
            <key>CFBundleVersion</key><string>7</string>
          </dict>
          <key>CreationDate</key><date>2026-11-28T10:00:00Z</date>
          <key>Name</key><string>Todo</string>
        </dict></plist>
        """;

    private string XcodeArchive(string bundleId, bool mac)
    {
        var folder = Path.Combine(Xcode, "2026-11-28", $"Todo {(mac ? "mac" : "ios")}.xcarchive");
        var app = Path.Combine(folder, "Products", "Applications", "Todo.app");
        Directory.CreateDirectory(mac ? Path.Combine(app, "Contents") : app);
        File.WriteAllText(Path.Combine(folder, "Info.plist"), XcodeInfo(bundleId, "Applications/Todo.app"));
        return folder;
    }

    private static ProvisioningProfile Profile(string uuid, ProvisioningKind kind, string platform, string appId = "TEAM123456.dev.example.todo", int days = 100) =>
        new(uuid, uuid + " profile", "TEAM123456", appId, DateTimeOffset.UtcNow.AddDays(days), "/p/" + uuid) { Kind = kind, Platforms = [platform] };

    [Fact]
    public async Task Apple_archives_are_xcodes_and_export_with_the_chosen_certificate_and_profile()
    {
        var ios = AppArchives.ReadXcodeArchive(XcodeArchive("dev.example.todo", mac: false))!;
        var mac = AppArchives.ReadXcodeArchive(XcodeArchive("dev.example.todo", mac: true))!;
        Assert.Equal((MobilePlatform.iOS, "Todo", "dev.example.todo", "1.2 (7)"), (ios.Platform, ios.Name, ios.ApplicationId, ios.VersionLabel));
        Assert.Equal(MobilePlatform.MacCatalyst, mac.Platform);
        Assert.True(ios.IsXcodeArchive);

        var service = Service(PlatformKind.MacOS, tool: (command, onLine) =>
        {
            if (command.Program == "security")
            {
                onLine?.Invoke("  1) 0123456789ABCDEF0123456789ABCDEF01234567 \"Apple Distribution: Example Ltd (TEAM123456)\"");
                onLine?.Invoke("  2) 1111111111111111111111111111111111111111 \"Apple Development: Jane Doe (TEAM123456)\"");
                onLine?.Invoke("  3) 2222222222222222222222222222222222222222 \"3rd Party Mac Developer Installer: Example Ltd (TEAM123456)\"");
                return 0;
            }

            File.WriteAllText(Path.Combine(command.Arguments[command.Arguments.ToList().IndexOf("-exportPath") + 1], "Todo.ipa"), "ipa");
            return 0;
        });
        Assert.Equal(2, service.Archives.List().Count);
        Assert.NotNull(service.Archives.NewestXcodeArchive(new DateTimeOffset(2026, 11, 28, 9, 59, 59, TimeSpan.Zero), "dev.example.todo"));
        Assert.Null(service.Archives.NewestXcodeArchive(new DateTimeOffset(2026, 11, 28, 11, 0, 0, TimeSpan.Zero), null));

        var identities = await service.AppleIdentitiesAsync(codeSigningOnly: true, Ct);
        var distribution = identities.Single(i => Distribution.SignsFor(i, DistributionChannel.AppStore));
        Assert.Equal("1111111111111111111111111111111111111111", identities.Single(i => Distribution.SignsFor(i, DistributionChannel.Development)).Hash);
        Assert.True(Distribution.IsInstallerIdentity(identities[2]));

        // The profiles that fit: the platform, the channel's kind, the bundle id, unexpired.
        var installed = new[]
        {
            Profile("STORE", ProvisioningKind.AppStore, "iOS"),
            Profile("ADHOC", ProvisioningKind.AdHoc, "iOS"),
            Profile("OLD", ProvisioningKind.AppStore, "iOS", days: -1),
            Profile("OTHER", ProvisioningKind.AppStore, "iOS", appId: "TEAM123456.com.other"),
            Profile("MAC", ProvisioningKind.AppStore, "OSX"),
        };
        Assert.Equal(["STORE"], Distribution.Profiles(installed, ios, DistributionChannel.AppStore, DateTimeOffset.UtcNow).Select(p => p.Uuid));
        Assert.Equal(["MAC"], Distribution.Profiles(installed, mac, DistributionChannel.AppStore, DateTimeOffset.UtcNow).Select(p => p.Uuid));
        Assert.Contains("provisioning profile", service.Missing(ios, DistributionChannel.AppStore, new DistributionSigning { AppleIdentity = distribution }), StringComparison.Ordinal);
        Assert.Contains("installer", service.Missing(mac, DistributionChannel.AppStore, new DistributionSigning { AppleIdentity = distribution, Profile = installed[4] }), StringComparison.Ordinal);

        var result = await service.DistributeAsync(ios, DistributionChannel.AppStore, new DistributionSigning { AppleIdentity = distribution, Profile = installed[0] }, null, Ct);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Todo.ipa", Path.GetFileName(Assert.Single(result.Files)));
        Assert.StartsWith(Path.Combine(Root, "Todo", "Todo ios", "Distribution", "App Store Connect "), result.Folder, StringComparison.Ordinal); // beside the IDE's archives, not in Xcode's folder

        var export = _commands.Last();
        Assert.Equal("xcrun", export.Program);
        Assert.Equal(["xcodebuild", "-exportArchive", "-archivePath", ios.Path, "-exportPath", result.Folder, "-exportOptionsPlist", Path.Combine(result.Folder, "ExportOptions.plist")], export.Arguments);
        var options = PropertyList.ParseDictionary(File.ReadAllText(Path.Combine(result.Folder, "ExportOptions.plist")))!;
        Assert.Equal(("app-store-connect", "manual", "TEAM123456", distribution.Hash), (options["method"], options["signingStyle"], options["teamID"], options["signingCertificate"]));
        Assert.Equal("STORE", PropertyList.String(options, "provisioningProfiles", "dev.example.todo"));
        Assert.False(options.ContainsKey("installerSigningCertificate"));

        // A Mac App Store package names the installer's certificate too.
        var macOptions = PropertyList.ParseDictionary(Distribution.ExportOptions(mac, DistributionChannel.AppStore, distribution, installed[4], identities[2]))!;
        Assert.Equal(identities[2].Hash, macOptions["installerSigningCertificate"]);
        Assert.Equal("developer-id", Distribution.ExportMethod(DistributionChannel.DeveloperId));
        Assert.Equal("release-testing", Distribution.ExportMethod(DistributionChannel.AdHoc));

        // Deleting an Xcode archive deletes its distributions beside the IDE's.
        service.Archives.Delete(ios);
        Assert.False(Directory.Exists(ios.Path));
        Assert.False(Directory.Exists(Path.Combine(Root, "Todo", "Todo ios")));
    }

    [Fact]
    public void Provisioning_profiles_say_what_they_sign_for()
    {
        static ProvisioningProfile? Parse(string body) => AppleSigning.ParseProfile(Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>UUID</key><string>U</string>{body}</dict></plist>
            """), "/p/u");
        const string Devices = "<key>ProvisionedDevices</key><array><string>00008140-001</string></array>";
        Assert.Equal(ProvisioningKind.Development, Parse(Devices + "<key>Entitlements</key><dict><key>get-task-allow</key><true/></dict><key>Platform</key><array><string>iOS</string></array>")!.Kind);
        Assert.Equal(ProvisioningKind.AdHoc, Parse(Devices + "<key>Entitlements</key><dict><key>get-task-allow</key><false/></dict>")!.Kind);
        Assert.Equal(ProvisioningKind.AppStore, Parse("<key>Platform</key><array><string>iOS</string></array>")!.Kind);
        Assert.Equal(ProvisioningKind.Enterprise, Parse("<key>ProvisionsAllDevices</key><true/><key>Platform</key><array><string>iOS</string></array>")!.Kind);
        var developerId = Parse("<key>ProvisionsAllDevices</key><true/><key>Platform</key><array><string>OSX</string></array><key>Entitlements</key><dict><key>com.apple.application-identifier</key><string>T.dev.example.todo</string></dict>")!;
        Assert.Equal(ProvisioningKind.DeveloperId, developerId.Kind);
        Assert.True(developerId.IsFor(MobilePlatform.MacCatalyst));
        Assert.False(developerId.IsFor(MobilePlatform.iOS));
        Assert.True(developerId.Covers("dev.example.todo"));
    }

    [Fact]
    public async Task Windows_signs_the_msix_with_signtool_and_writes_the_stores_upload()
    {
        var folder = Path.Combine(Root, "Todo", "2026-11-28 10-00-00 Windows");
        var msix = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "packages", "Todo_1.2.7.0_x64_Test")).FullName, "Todo_1.2.7.0_x64.msix");
        File.WriteAllText(msix, "msix");
        var archive = new AppArchive(folder, MobilePlatform.Windows, "Todo", DateTimeOffset.UtcNow) { Packages = [msix], IsPackaged = true, Publisher = "CN=Example Ltd, O=Example Ltd, C=GB", Version = "7" };
        Assert.Equal([DistributionChannel.Sideload, DistributionChannel.MicrosoftStore], Distribution.Channels(archive));
        Assert.Equal([DistributionChannel.Sideload], Distribution.Channels(archive with { IsPackaged = false }));

        // signtool from the Windows SDK package the MAUI build restores, the newest version for this machine's architecture.
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        foreach (var version in new[] { "10.0.22621.0", "10.0.26100.0" })
        {
            foreach (var arch in new[] { "x64", "arm64" })
            {
                var bin = Directory.CreateDirectory(Path.Combine(_dir, "nuget", "microsoft.windows.sdk.buildtools", "10.0.26100.1", "bin", version, arch)).FullName;
                File.WriteAllText(Path.Combine(bin, "signtool.exe"), string.Empty);
            }
        }

        var service = Service(PlatformKind.Windows, tool: (command, onLine) =>
        {
            if (command.Program == "powershell")
            {
                onLine?.Invoke("""[{"Thumbprint":"ABCDEF0123456789ABCDEF0123456789ABCDEF01","Subject":"CN=Example Ltd, O=Example Ltd, C=GB","NotAfter":"2027-01-01T00:00:00.0000000Z"},{"Thumbprint":"1111111111111111111111111111111111111111","Subject":"CN=Someone Else","NotAfter":"2027-01-01T00:00:00Z"}]""");
            }

            return 0;
        });
        var certificates = await service.StoreCertificatesAsync(Ct);
        Assert.Equal(2, certificates.Count);
        var matching = Assert.Single(certificates, c => Distribution.SubjectMatches(c.Subject, archive.Publisher));
        Assert.True(Distribution.SubjectMatches("C=GB,  CN=Example Ltd,O=Example Ltd", archive.Publisher));
        Assert.Equal("Choose a certificate, or create a test certificate.", service.Missing(archive, DistributionChannel.Sideload, new DistributionSigning()));
        Assert.Null(service.Missing(archive, DistributionChannel.MicrosoftStore, new DistributionSigning()));

        var result = await service.DistributeAsync(archive, DistributionChannel.Sideload, new DistributionSigning { StoreCertificate = matching }, null, Ct);
        Assert.True(result.Succeeded, result.Message);
        var sign = _commands.Last();
        Assert.EndsWith(Path.Combine("10.0.26100.0", architecture, "signtool.exe"), sign.Program, StringComparison.Ordinal);
        Assert.Equal(["sign", "/fd", "SHA256", "/sha1", matching.Thumbprint, "/s", "My", Path.Combine(result.Folder, Path.GetFileName(msix))], sign.Arguments);

        // A .pfx's password is on signtool's command line (it has no other way) but never in what's shown.
        var pfx = Distribution.SignMsixCommand("signtool.exe", "a.msix", null, new CertificateFile("id", "Cert", "/c/cert.pfx"), "hunter2");
        Assert.Contains("hunter2", pfx.Arguments);
        Assert.DoesNotContain("hunter2", Distribution.Shown(pfx), StringComparison.Ordinal);

        var store = await service.DistributeAsync(archive, DistributionChannel.MicrosoftStore, new DistributionSigning(), null, Ct);
        var upload = Assert.Single(store.Files);
        Assert.Equal("Todo_1.2.7.0_x64.msixupload", Path.GetFileName(upload));
        using var zip = ZipFile.OpenRead(upload);
        Assert.Equal(["Todo_1.2.7.0_x64.msix"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task A_test_certificate_matches_the_packages_publisher_and_is_exported_for_installing()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Root, "Todo", "w")).FullName;
        var archive = new AppArchive(folder, MobilePlatform.Windows, "Todo", DateTimeOffset.UtcNow) { IsPackaged = true, Publisher = "CN=Jane's Apps" };
        var service = Service(PlatformKind.Windows, tool: (_, onLine) =>
        {
            onLine?.Invoke("ABCDEF0123456789ABCDEF0123456789ABCDEF01");
            return 0;
        });
        var (certificate, cer, message) = await service.CreateTestCertificateAsync(archive, null, Ct);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", certificate!.Thumbprint);
        Assert.Equal(Path.Combine(folder, "Todo test certificate.cer"), cer);
        Assert.Contains("Trusted People", message, StringComparison.Ordinal);
        var script = _commands.Single().Arguments[^1];
        Assert.Contains("-Subject 'CN=Jane''s Apps'", script, StringComparison.Ordinal); // quoted for PowerShell
        Assert.Contains("1.3.6.1.5.5.7.3.3", script, StringComparison.Ordinal); // code signing
        Assert.Contains("Export-Certificate", script, StringComparison.Ordinal);

        var noPublisher = await service.CreateTestCertificateAsync(archive with { Publisher = null }, null, Ct);
        Assert.Null(noPublisher.Certificate);
        Assert.Null((await Service(PlatformKind.Linux).CreateTestCertificateAsync(archive, null, Ct)).Certificate);
    }

    [Fact]
    public async Task An_unpackaged_windows_app_is_distributed_as_a_zip()
    {
        var folder = Path.Combine(Root, "Todo", "u");
        var app = Directory.CreateDirectory(Path.Combine(folder, "packages", "app")).FullName;
        File.WriteAllText(Path.Combine(app, "Todo.exe"), "exe");
        var archive = new AppArchive(folder, MobilePlatform.Windows, "Todo", DateTimeOffset.UtcNow) { Packages = [app], Version = "7", Framework = "net10.0-windows10.0.19041.0" };
        var result = await Service(PlatformKind.Linux).DistributeAsync(archive, DistributionChannel.Sideload, new DistributionSigning(), null, Ct);
        Assert.True(result.Succeeded, result.Message);
        using var zip = ZipFile.OpenRead(Assert.Single(result.Files));
        Assert.Equal(["Todo.exe"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void Jar_signature_files_are_told_apart()
    {
        Assert.True(Distribution.IsSignatureEntry("META-INF/MANIFEST.MF"));
        Assert.True(Distribution.IsSignatureEntry("META-INF/CERT.RSA"));
        Assert.True(Distribution.IsSignatureEntry("META-INF/KEY0.EC"));
        Assert.False(Distribution.IsSignatureEntry("META-INF/services/a.b"));
        Assert.False(Distribution.IsSignatureEntry("META-INF/com/android/build/gradle/app-metadata.properties"));
        Assert.False(Distribution.IsSignatureEntry("base/META-INF/CERT.SF"));
    }

    [Fact]
    public void A_manifests_publisher_is_read()
    {
        var manifest = Path.Combine(_dir, "Package.appxmanifest");
        File.WriteAllText(manifest, """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="maui-todo" Publisher="CN=User Name" Version="0.0.0.0" />
            </Package>
            """);
        Assert.Equal("CN=User Name", Distribution.ManifestPublisher(manifest));
        Assert.Null(Distribution.ManifestPublisher(Path.Combine(_dir, "missing.appxmanifest")));
    }
}
