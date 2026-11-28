using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Mobile;

/// <summary>Where a distribution of an archive goes (ADR 0114): each platform's channels, as the classic IDE's Distribute offers them.</summary>
public enum DistributionChannel
{
    /// <summary>Android: a signed <c>.apk</c> to install directly. iOS: Ad Hoc (Release Testing) to registered devices.</summary>
    AdHoc,

    /// <summary>Android: a signed <c>.aab</c> for the Play Console.</summary>
    GooglePlay,

    /// <summary>iOS and Mac Catalyst: App Store Connect (and TestFlight).</summary>
    AppStore,

    /// <summary>iOS: an enterprise's in-house distribution.</summary>
    Enterprise,

    /// <summary>iOS and Mac Catalyst: signed for development devices.</summary>
    Development,

    /// <summary>Mac Catalyst: Developer ID, outside the Mac App Store.</summary>
    DeveloperId,

    /// <summary>Windows: a signed <c>.msix</c> (or the unpackaged app's <c>.zip</c>) to install directly.</summary>
    Sideload,

    /// <summary>Windows: an unsigned <c>.msixupload</c> for Partner Center (the Store signs it).</summary>
    MicrosoftStore,
}

/// <summary>A Windows code-signing certificate in the user's certificate store (<c>Cert:\CurrentUser\My</c>).</summary>
public sealed record StoreCertificate(string Thumbprint, string Subject, DateTimeOffset? Expires);

/// <summary>A distribution's result: the files written, or why it failed.</summary>
public sealed record DistributionResult(bool Succeeded, string Folder, IReadOnlyList<string> Files, string Message);

/// <summary>
/// What each platform's channels need and the commands that sign for them (ADR 0114). Passwords reach the tools
/// through their environment (<c>env:</c> for <c>apksigner</c>, <c>:env</c> for <c>keytool</c> and <c>jarsigner</c>),
/// never on a command line, except <c>signtool</c>'s <c>/p</c>, which has no other way (hidden in what's shown).
/// </summary>
public static class Distribution
{
    public const string StorePasswordVariable = "CODALUS_SIGNING_STORE_PASSWORD";
    public const string KeyPasswordVariable = "CODALUS_SIGNING_KEY_PASSWORD";

    public static IReadOnlyList<DistributionChannel> Channels(AppArchive archive) => archive.Platform switch
    {
        MobilePlatform.Android => [DistributionChannel.AdHoc, DistributionChannel.GooglePlay],
        MobilePlatform.iOS => [DistributionChannel.AppStore, DistributionChannel.AdHoc, DistributionChannel.Enterprise, DistributionChannel.Development],
        MobilePlatform.MacCatalyst => [DistributionChannel.AppStore, DistributionChannel.DeveloperId, DistributionChannel.Development],
        MobilePlatform.Windows => archive.IsPackaged ? [DistributionChannel.Sideload, DistributionChannel.MicrosoftStore] : [DistributionChannel.Sideload],
        _ => [],
    };

    public static string Title(DistributionChannel channel) => channel switch
    {
        DistributionChannel.AdHoc => "Ad Hoc",
        DistributionChannel.GooglePlay => "Google Play",
        DistributionChannel.AppStore => "App Store Connect",
        DistributionChannel.Enterprise => "Enterprise",
        DistributionChannel.Development => "Development",
        DistributionChannel.DeveloperId => "Developer ID",
        DistributionChannel.Sideload => "Sideload",
        DistributionChannel.MicrosoftStore => "Microsoft Store",
        _ => channel.ToString(),
    };

    /// <summary>What the channel makes, in a sentence.</summary>
    public static string Description(MobilePlatform platform, DistributionChannel channel, bool packaged = true) => (platform, channel) switch
    {
        (MobilePlatform.Android, DistributionChannel.AdHoc) => "A signed .apk to install on devices directly or share outside the store.",
        (MobilePlatform.Android, DistributionChannel.GooglePlay) => "A signed Android App Bundle (.aab) to upload in the Play Console.",
        (MobilePlatform.iOS, DistributionChannel.AppStore) => "An .ipa signed for App Store Connect and TestFlight, to upload with Transporter.",
        (MobilePlatform.iOS, DistributionChannel.AdHoc) => "An .ipa for the devices an ad hoc provisioning profile lists.",
        (MobilePlatform.iOS, DistributionChannel.Enterprise) => "An .ipa for an Apple Developer Enterprise Program's in-house distribution.",
        (_, DistributionChannel.Development) => "Signed with a development profile, for the devices it lists.",
        (MobilePlatform.MacCatalyst, DistributionChannel.AppStore) => "A signed installer package (.pkg) for the Mac App Store, to upload with Transporter.",
        (MobilePlatform.MacCatalyst, DistributionChannel.DeveloperId) => "An app signed with Developer ID, to notarize and share outside the Mac App Store.",
        (MobilePlatform.Windows, DistributionChannel.Sideload) => packaged ? "A signed .msix to install directly, with the certificate's .cer when it is a test certificate." : "The unpackaged app, self-contained, as a .zip.",
        (MobilePlatform.Windows, DistributionChannel.MicrosoftStore) => "An .msixupload for Partner Center; the Store signs it, so no certificate is needed.",
        _ => string.Empty,
    };

    /// <summary>The page the files are uploaded on (the hand-off), or null for channels that aren't uploaded.</summary>
    public static Uri? UploadPage(DistributionChannel channel) => channel switch
    {
        DistributionChannel.GooglePlay => new Uri("https://play.google.com/console"),
        DistributionChannel.AppStore => new Uri("https://appstoreconnect.apple.com/apps"),
        DistributionChannel.MicrosoftStore => new Uri("https://partner.microsoft.com/dashboard/apps-and-games/overview"),
        _ => null,
    };

    /// <summary>The Apple export method of <c>-exportOptionsPlist</c> (Xcode 15.3's names).</summary>
    public static string ExportMethod(DistributionChannel channel) => channel switch
    {
        DistributionChannel.AppStore => "app-store-connect",
        DistributionChannel.AdHoc => "release-testing",
        DistributionChannel.Enterprise => "enterprise",
        DistributionChannel.DeveloperId => "developer-id",
        _ => "debugging",
    };

    /// <summary>The provisioning profile kind an Apple channel needs.</summary>
    public static ProvisioningKind ProfileKind(DistributionChannel channel) => channel switch
    {
        DistributionChannel.AppStore => ProvisioningKind.AppStore,
        DistributionChannel.AdHoc => ProvisioningKind.AdHoc,
        DistributionChannel.Enterprise => ProvisioningKind.Enterprise,
        DistributionChannel.DeveloperId => ProvisioningKind.DeveloperId,
        _ => ProvisioningKind.Development,
    };

    /// <summary>Whether a keychain identity's certificate signs for an Apple channel (by its name's kind, as Xcode files them).</summary>
    public static bool SignsFor(SigningIdentity identity, DistributionChannel channel)
    {
        var name = identity.Name;
        return channel switch
        {
            DistributionChannel.Development => name.StartsWith("Apple Development", StringComparison.Ordinal) || name.StartsWith("iPhone Developer", StringComparison.Ordinal) || name.StartsWith("Mac Developer", StringComparison.Ordinal),
            DistributionChannel.DeveloperId => name.StartsWith("Developer ID Application", StringComparison.Ordinal),
            _ => name.StartsWith("Apple Distribution", StringComparison.Ordinal) || name.StartsWith("iPhone Distribution", StringComparison.Ordinal) || name.StartsWith("3rd Party Mac Developer Application", StringComparison.Ordinal),
        };
    }

    /// <summary>Whether a keychain identity signs a Mac App Store installer package.</summary>
    public static bool IsInstallerIdentity(SigningIdentity identity) =>
        identity.Name.StartsWith("3rd Party Mac Developer Installer", StringComparison.Ordinal) || identity.Name.StartsWith("Mac Installer Distribution", StringComparison.Ordinal);

    /// <summary>The installed profiles that fit an archive's channel: its platform and kind, its bundle id, unexpired; newest expiry first.</summary>
    public static IReadOnlyList<ProvisioningProfile> Profiles(IEnumerable<ProvisioningProfile> installed, AppArchive archive, DistributionChannel channel, DateTimeOffset now) =>
        [.. installed
            .Where(p => p.IsFor(archive.Platform) && p.Kind == ProfileKind(channel) && !p.IsExpired(now) && (archive.ApplicationId is null || p.Covers(archive.ApplicationId)))
            .OrderByDescending(p => p.Expires ?? DateTimeOffset.MaxValue)];

    // ------------------------------------------------------------------ Apple

    /// <summary>
    /// <c>xcodebuild -exportArchive</c>'s options for a manual signing: the method, the team, the certificate (by its
    /// SHA-1), the app's profile by its UUID, and for a Mac App Store package the installer's certificate.
    /// </summary>
    public static string ExportOptions(AppArchive archive, DistributionChannel channel, SigningIdentity identity, ProvisioningProfile profile, SigningIdentity? installer = null)
    {
        var options = new List<KeyValuePair<string, object>>
        {
            new("method", ExportMethod(channel)),
            new("destination", "export"),
            new("signingStyle", "manual"),
            new("signingCertificate", identity.Hash),
            new("provisioningProfiles", new List<KeyValuePair<string, object>> { new(archive.ApplicationId ?? profile.ApplicationId ?? string.Empty, profile.Uuid) }),
        };
        if ((profile.Team ?? identity.Team) is { Length: > 0 } team)
        {
            options.Insert(1, new("teamID", team));
        }

        if (installer is not null && archive.Platform == MobilePlatform.MacCatalyst && channel == DistributionChannel.AppStore)
        {
            options.Add(new("installerSigningCertificate", installer.Hash));
        }

        if (channel is DistributionChannel.AppStore)
        {
            options.Add(new("uploadSymbols", true));
            options.Add(new("manageAppVersionAndBuildNumber", false));
        }

        return PropertyList.Write(options);
    }

    public static ToolCommand ExportCommand(AppArchive archive, string exportFolder, string optionsFile) =>
        new("xcrun", ["xcodebuild", "-exportArchive", "-archivePath", archive.Path, "-exportPath", exportFolder, "-exportOptionsPlist", optionsFile]);

    public static ToolCommand FindIdentitiesCommand(bool codeSigningOnly) =>
        new("security", codeSigningOnly ? ["find-identity", "-v", "-p", "codesigning"] : ["find-identity", "-v"]);

    // ------------------------------------------------------------------ Android

    /// <summary>
    /// <c>keytool -genkeypair</c> for a new upload key: RSA 2048 in a PKCS12 keystore (one password for the store and
    /// the key, as PKCS12 has it), valid <paramref name="years"/> years (Google Play asks for 25 or more).
    /// </summary>
    public static ToolCommand CreateKeystoreCommand(string keytool, string path, string alias, string distinguishedName, int years, string password) =>
        new(keytool, ["-genkeypair", "-keystore", path, "-storetype", "PKCS12", "-alias", alias, "-keyalg", "RSA", "-keysize", "2048", "-validity", (years * 365).ToString(CultureInfo.InvariantCulture), "-dname", distinguishedName, "-storepass:env", StorePasswordVariable, "-keypass:env", StorePasswordVariable])
        {
            Environment = new Dictionary<string, string> { [StorePasswordVariable] = password },
        };

    /// <summary><c>keytool -list</c> of one alias: whether the keystore opens with the password and has the key.</summary>
    public static ToolCommand CheckKeystoreCommand(string keytool, string path, string alias, string storePassword) =>
        new(keytool, ["-list", "-keystore", path, "-alias", alias, "-storepass:env", StorePasswordVariable])
        {
            Environment = new Dictionary<string, string> { [StorePasswordVariable] = storePassword },
        };

    /// <summary>An X.500 name for <c>-dname</c> from what the user typed (commas escaped).</summary>
    public static string DistinguishedName(string name, string? organization, string? country)
    {
        static string Escape(string value) => value.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal);
        var parts = new List<string> { "CN=" + Escape(name) };
        if (!string.IsNullOrWhiteSpace(organization))
        {
            parts.Add("O=" + Escape(organization));
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            parts.Add("C=" + Escape(country));
        }

        return string.Join(", ", parts);
    }

    /// <summary><c>apksigner sign</c> of an APK into <paramref name="output"/> (it replaces the debug signature the archive has).</summary>
    public static ToolCommand SignApkCommand(string apksigner, AndroidKeystore keystore, string input, string output, string storePassword, string keyPassword, string? jdk) =>
        new(apksigner, ["sign", "--ks", keystore.Path, "--ks-key-alias", keystore.Alias, "--ks-pass", "env:" + StorePasswordVariable, "--key-pass", "env:" + KeyPasswordVariable, "--out", output, input])
        {
            Environment = JavaEnvironment(jdk, storePassword, keyPassword),
        };

    /// <summary><c>jarsigner</c> of an App Bundle in place (its earlier signature removed first, <see cref="RemoveJarSignatures"/>).</summary>
    public static ToolCommand SignBundleCommand(string jarsigner, AndroidKeystore keystore, string bundle, string storePassword, string keyPassword, string? jdk) =>
        new(jarsigner, ["-keystore", keystore.Path, "-storepass:env", StorePasswordVariable, "-keypass:env", KeyPasswordVariable, "-sigalg", "SHA256withRSA", "-digestalg", "SHA-256", bundle, keystore.Alias])
        {
            Environment = JavaEnvironment(jdk, storePassword, keyPassword),
        };

    private static Dictionary<string, string> JavaEnvironment(string? jdk, string storePassword, string keyPassword)
    {
        var environment = new Dictionary<string, string> { [StorePasswordVariable] = storePassword, [KeyPasswordVariable] = keyPassword };
        if (jdk is not null)
        {
            environment["JAVA_HOME"] = jdk;
        }

        return environment;
    }

    /// <summary>The JDK's <c>keytool</c> or <c>jarsigner</c>.</summary>
    public static string JdkTool(string jdk, string tool, PlatformKind platform) => Path.Combine(jdk, "bin", platform == PlatformKind.Windows ? tool + ".exe" : tool);

    /// <summary>The newest build-tools' <c>apksigner</c>, or null.</summary>
    public static string? ApkSigner(string sdk, PlatformKind platform)
    {
        var buildTools = Path.Combine(sdk, "build-tools");
        if (!Directory.Exists(buildTools))
        {
            return null;
        }

        var name = platform == PlatformKind.Windows ? "apksigner.bat" : "apksigner";
        return Directory.GetDirectories(buildTools)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var version) ? version : new Version(0, 0))
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Removes a JAR signature (<c>META-INF/*.SF</c>, <c>.RSA</c>, <c>.DSA</c>, <c>.EC</c> and the manifest) from a zip,
    /// so <c>jarsigner</c>'s is the only one: the archive's bundle carries the debug key's, and Google Play refuses a
    /// bundle signed twice.
    /// </summary>
    public static int RemoveJarSignatures(string file)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Update);
        var signatures = zip.Entries.Where(e => IsSignatureEntry(e.FullName)).ToList();
        foreach (var entry in signatures)
        {
            entry.Delete();
        }

        return signatures.Count;
    }

    public static bool IsSignatureEntry(string name)
    {
        if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || name.IndexOf('/', "META-INF/".Length) >= 0)
        {
            return false;
        }

        var file = name["META-INF/".Length..];
        return file.Equals("MANIFEST.MF", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(file).ToUpperInvariant() is ".SF" or ".RSA" or ".DSA" or ".EC";
    }

    /// <summary>The archive's package for an Android channel: the <c>.apk</c> for Ad Hoc, the <c>.aab</c> for Google Play (the signed one first).</summary>
    public static string? AndroidPackage(AppArchive archive, DistributionChannel channel)
    {
        var extension = channel == DistributionChannel.GooglePlay ? ".aab" : ".apk";
        return archive.Packages
            .Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).EndsWith("-Signed", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>The file name a distributed Android package gets: <c>&lt;id&gt;-&lt;version&gt;.apk</c>.</summary>
    public static string AndroidOutputName(AppArchive archive, DistributionChannel channel)
    {
        var name = archive.ApplicationId ?? archive.Name;
        var version = archive.Version is { Length: > 0 } v ? "-" + v : string.Empty;
        return name + version + (channel == DistributionChannel.GooglePlay ? ".aab" : ".apk");
    }

    // ------------------------------------------------------------------ Windows

    /// <summary><c>signtool sign</c> of an MSIX with a store certificate (by thumbprint) or a <c>.pfx</c> and its password.</summary>
    public static ToolCommand SignMsixCommand(string signtool, string package, StoreCertificate? store, CertificateFile? file, string? password)
    {
        var arguments = new List<string> { "sign", "/fd", "SHA256" };
        if (store is not null)
        {
            arguments.AddRange(["/sha1", store.Thumbprint, "/s", "My"]);
        }
        else if (file is not null)
        {
            arguments.AddRange(["/f", file.Path]);
            if (!string.IsNullOrEmpty(password))
            {
                arguments.AddRange(["/p", password]);
            }
        }

        arguments.Add(package);
        return new ToolCommand(signtool, arguments);
    }

    /// <summary>A command line to show (the output channel): a <c>/p</c> password hidden.</summary>
    public static string Shown(ToolCommand command)
    {
        var arguments = command.Arguments.ToList();
        var password = arguments.IndexOf("/p");
        if (password >= 0 && password + 1 < arguments.Count)
        {
            arguments[password + 1] = "********";
        }

        return (command with { Arguments = arguments }).Display;
    }

    /// <summary>
    /// The newest <c>signtool.exe</c> for <paramref name="architecture"/>: the Windows SDK's (<c>Windows Kits\10\bin</c>),
    /// else the <c>Microsoft.Windows.SDK.BuildTools</c> package's in the NuGet cache, which MAUI's Windows target restores.
    /// </summary>
    public static string? SignTool(string? programFilesX86, string nugetPackages, string architecture)
    {
        var candidates = new List<(Version Version, string Path)>();
        void Add(string bin)
        {
            if (!Directory.Exists(bin))
            {
                return;
            }

            foreach (var folder in Directory.GetDirectories(bin))
            {
                var tool = Path.Combine(folder, architecture, "signtool.exe");
                if (Version.TryParse(Path.GetFileName(folder), out var version) && File.Exists(tool))
                {
                    candidates.Add((version, tool));
                }
            }
        }

        if (programFilesX86 is not null)
        {
            Add(Path.Combine(programFilesX86, "Windows Kits", "10", "bin"));
        }

        var buildTools = Path.Combine(nugetPackages, "microsoft.windows.sdk.buildtools");
        if (Directory.Exists(buildTools))
        {
            foreach (var package in Directory.GetDirectories(buildTools))
            {
                Add(Path.Combine(package, "bin"));
            }
        }

        return candidates.OrderByDescending(c => c.Version).Select(c => c.Path).FirstOrDefault();
    }

    /// <summary>PowerShell that lists the user's code-signing certificates as JSON.</summary>
    public static ToolCommand ListCertificatesCommand() =>
        new("powershell", ["-NoProfile", "-NonInteractive", "-Command",
            "@(Get-ChildItem Cert:\\CurrentUser\\My -CodeSigningCert | Where-Object { $_.HasPrivateKey } | ForEach-Object { [pscustomobject]@{ Thumbprint = $_.Thumbprint; Subject = $_.Subject; NotAfter = $_.NotAfter.ToUniversalTime().ToString('o') } }) | ConvertTo-Json -Compress"]);

    /// <summary>The certificates <see cref="ListCertificatesCommand"/> printed (one object or an array).</summary>
    public static IReadOnlyList<StoreCertificate> ParseCertificates(string json)
    {
        var result = new List<StoreCertificate>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
            foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
            {
                if (item.TryGetProperty("Thumbprint", out var thumbprint) && thumbprint.GetString() is { Length: > 0 } print)
                {
                    DateTimeOffset? expires = item.TryGetProperty("NotAfter", out var notAfter) && DateTimeOffset.TryParse(notAfter.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
                    result.Add(new StoreCertificate(print, item.TryGetProperty("Subject", out var subject) ? subject.GetString() ?? string.Empty : string.Empty, expires));
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    /// <summary>
    /// PowerShell that makes a self-signed test certificate whose subject is the package's publisher (MSIX requires the
    /// match), in the user's store, and exports its public part to <paramref name="cerFile"/> (for Trusted People on the
    /// machines that install the package); it prints the thumbprint.
    /// </summary>
    public static ToolCommand CreateTestCertificateCommand(string publisher, string friendlyName, string cerFile)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return new ToolCommand("powershell", ["-NoProfile", "-NonInteractive", "-Command",
            "$c = New-SelfSignedCertificate -Type Custom -Subject " + Quote(publisher) + " -KeyUsage DigitalSignature -FriendlyName " + Quote(friendlyName)
            + " -CertStoreLocation Cert:\\CurrentUser\\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}');"
            + " Export-Certificate -Cert $c -FilePath " + Quote(cerFile) + " | Out-Null; $c.Thumbprint"]);
    }

    /// <summary>Whether a certificate's subject is the package's publisher (the same names, in any order and spacing).</summary>
    public static bool SubjectMatches(string subject, string? publisher)
    {
        if (publisher is null)
        {
            return true;
        }

        static IEnumerable<string> Parts(string name) => name.Split(',').Select(p => string.Concat(p.Where(c => !char.IsWhiteSpace(c)))).Where(p => p.Length > 0).Order(StringComparer.OrdinalIgnoreCase);
        return Parts(subject).SequenceEqual(Parts(publisher), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The <c>Identity/@Publisher</c> of a <c>Package.appxmanifest</c>, or null.</summary>
    public static string? ManifestPublisher(string manifest)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Load(manifest);
            return document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Publisher")?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>An <c>.msixupload</c>: a zip of the package, as Partner Center takes it.</summary>
    public static void WriteMsixUpload(string package, string upload)
    {
        File.Delete(upload);
        using var zip = ZipFile.Open(upload, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(package, Path.GetFileName(package), CompressionLevel.NoCompression);
    }
}
