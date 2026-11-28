using System.Runtime.InteropServices;
using Codalus.Platform;
using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Mobile;

/// <summary>What an archive is built from: the project, the platform's framework, and the app's names and versions.</summary>
public sealed record ArchiveRequest(string Project, string Framework, MobilePlatform Platform)
{
    public string? Name { get; init; }

    public string? ApplicationId { get; init; }

    public string? DisplayVersion { get; init; }

    public string? Version { get; init; }

    /// <summary>The project's <c>WindowsPackageType</c> (<c>MSIX</c> or <c>None</c>) for the Windows framework.</summary>
    public string? WindowsPackageType { get; init; }
}

/// <summary>An archive's result: the archive, or why there is none.</summary>
public sealed record ArchiveResult(AppArchive? Archive, string Message, bool Canceled = false);

/// <summary>How a distribution is signed (ADR 0114): what the channel's platform needs, picked by the user.</summary>
public sealed record DistributionSigning
{
    public AndroidKeystore? Keystore { get; init; }

    public SigningIdentity? AppleIdentity { get; init; }

    public ProvisioningProfile? Profile { get; init; }

    /// <summary>The Mac App Store package's installer identity.</summary>
    public SigningIdentity? Installer { get; init; }

    public StoreCertificate? StoreCertificate { get; init; }

    public CertificateFile? CertificateFile { get; init; }

    /// <summary>A test certificate's public part (<c>.cer</c>) to put beside the package, or null.</summary>
    public string? PublicCertificate { get; init; }
}

/// <summary>
/// Archives and distributes .NET MAUI apps (ADR 0114). An archive is the platform SDK's own Release publish, in the build
/// host as every publish is (ADR 0042): Android's <c>.aab</c> and <c>.apk</c> signed with the debug key, Apple's
/// <c>.xcarchive</c> (<c>ArchiveOnBuild</c>), Windows' unsigned <c>.msix</c> or self-contained unpackaged app.
/// Distributing signs a copy for a channel with the platform's own tools (<c>apksigner</c>, <c>jarsigner</c>,
/// <c>xcodebuild -exportArchive</c>, <c>signtool</c>) and stops there: the upload is the user's (the hand-off).
/// </summary>
public sealed class ArchiveService(
    PlatformKind platform,
    AppArchives archives,
    SigningIdentities identities,
    Func<AndroidToolchain.Locations> android,
    ToolProcessRunner run,
    Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<BuildResultInfo>> build,
    string home,
    Func<string, string?> environment,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AppArchives Archives => archives;

    public SigningIdentities Identities => identities;

    public PlatformKind Platform => platform;

    /// <summary>Why <paramref name="target"/>'s archives can't be made on this machine, or null.</summary>
    public string? CannotArchive(MobilePlatform target) =>
        MobileTargets.RunsOn(target, platform) ? null
        : target is MobilePlatform.iOS or MobilePlatform.MacCatalyst ? $"{MobileTargets.Title(target)} apps are archived on a Mac (Xcode signs them)."
        : target == MobilePlatform.Windows ? "Windows apps are archived on Windows."
        : $"{MobileTargets.Title(target)} apps can't be archived.";

    /// <summary>
    /// The global properties of an archive's publish: the framework, Release, <c>_IsPublishing</c>, and the platform's
    /// own: Android's both package formats with the debug key (Distribute signs them), Apple's <c>ArchiveOnBuild</c>
    /// (iOS for devices, <c>ios-arm64</c>), Windows' MSIX without signing or the unpackaged app self-contained, for
    /// this machine's architecture.
    /// </summary>
    public static Dictionary<string, string> Properties(ArchiveRequest request, string packages, Architecture architecture)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetFramework"] = request.Framework,
            ["Configuration"] = "Release",
            ["_IsPublishing"] = "true",
        };
        var folder = packages.EndsWith(Path.DirectorySeparatorChar) ? packages : packages + Path.DirectorySeparatorChar;
        switch (request.Platform)
        {
            case MobilePlatform.Android:
                properties["AndroidPackageFormats"] = "aab;apk";
                properties["AndroidKeyStore"] = "false";
                properties["PublishDir"] = folder;
                break;
            case MobilePlatform.iOS:
                properties["RuntimeIdentifier"] = "ios-arm64";
                properties["ArchiveOnBuild"] = "true";
                break;
            case MobilePlatform.MacCatalyst:
                properties["ArchiveOnBuild"] = "true";
                break;
            case MobilePlatform.Windows:
                properties["RuntimeIdentifier"] = "win-" + (architecture == Architecture.Arm64 ? "arm64" : "x64");
                if (string.Equals(request.WindowsPackageType, "MSIX", StringComparison.OrdinalIgnoreCase))
                {
                    properties["WindowsPackageType"] = "MSIX";
                    properties["GenerateAppxPackageOnBuild"] = "true";
                    properties["AppxPackageSigningEnabled"] = "false";
                    properties["AppxBundle"] = "Never";
                    properties["AppxPackageDir"] = folder;
                }
                else
                {
                    properties["SelfContained"] = "true";
                    properties["WindowsAppSDKSelfContained"] = "true";
                    properties["PublishDir"] = Path.Combine(folder, "app") + Path.DirectorySeparatorChar;
                }

                break;
        }

        return properties;
    }

    /// <summary>Builds an archive of <paramref name="request"/> (the versions already in the project) and records it.</summary>
    public async Task<ArchiveResult> ArchiveAsync(ArchiveRequest request, CancellationToken cancellationToken)
    {
        if (CannotArchive(request.Platform) is { } why)
        {
            return new ArchiveResult(null, why);
        }

        var name = request.Name ?? Path.GetFileNameWithoutExtension(request.Project);
        var started = _time.GetUtcNow();
        var folder = archives.NewFolder(name, request.Platform, started);
        var packages = Path.Combine(folder, "packages");
        var result = await build(BuildTargets.Publish, request.Project, Properties(request, packages, RuntimeInformation.OSArchitecture), cancellationToken).ConfigureAwait(false);
        if (result.Canceled)
        {
            DeleteQuietly(folder);
            return new ArchiveResult(null, "The archive was cancelled.", Canceled: true);
        }

        if (!result.Succeeded)
        {
            DeleteQuietly(folder);
            return new ArchiveResult(null, $"The archive failed: {result.Errors} error(s), see the Error List and Output → Build.");
        }

        if (request.Platform is MobilePlatform.iOS or MobilePlatform.MacCatalyst)
        {
            DeleteQuietly(folder);
            return archives.NewestXcodeArchive(started, request.ApplicationId) is { } xcode
                ? new ArchiveResult(xcode with { Project = request.Project, Framework = request.Framework }, $"Archived {name} {xcode.VersionLabel} in Xcode's archives.")
                : new ArchiveResult(null, "The build succeeded but made no Xcode archive (see Output → Build).");
        }

        var packaged = request.Platform == MobilePlatform.Windows && string.Equals(request.WindowsPackageType, "MSIX", StringComparison.OrdinalIgnoreCase);
        var made = !Directory.Exists(packages) ? [] : request.Platform switch
        {
            MobilePlatform.Android => Directory.GetFiles(packages, "*.aab", SearchOption.TopDirectoryOnly).Concat(Directory.GetFiles(packages, "*.apk", SearchOption.TopDirectoryOnly)).Order(StringComparer.Ordinal).ToList(),
            MobilePlatform.Windows when packaged => Directory.GetFiles(packages, "*.msix", SearchOption.AllDirectories).ToList(),
            MobilePlatform.Windows => Directory.Exists(Path.Combine(packages, "app")) ? [Path.Combine(packages, "app")] : [],
            _ => [],
        };
        if (made.Count == 0)
        {
            DeleteQuietly(folder);
            return new ArchiveResult(null, "The build succeeded but made no package (see Output → Build).");
        }

        var archive = new AppArchive(folder, request.Platform, name, started)
        {
            ApplicationId = request.ApplicationId,
            DisplayVersion = request.DisplayVersion,
            Version = request.Version,
            Project = request.Project,
            Framework = request.Framework,
            Packages = made,
            IsPackaged = packaged,
            Publisher = packaged ? Distribution.ManifestPublisher(Path.Combine(Path.GetDirectoryName(request.Project)!, "Platforms", "Windows", "Package.appxmanifest")) : null,
        };
        AppArchives.Save(archive);
        return new ArchiveResult(archive, $"Archived {name} {archive.VersionLabel} for {MobileTargets.Title(request.Platform)}.");
    }

    // ------------------------------------------------------------------ signing identities

    /// <summary>The keychain's identities: code signing, or all valid ones (installers aren't code signing).</summary>
    public async Task<IReadOnlyList<SigningIdentity>> AppleIdentitiesAsync(bool codeSigningOnly, CancellationToken cancellationToken)
    {
        if (platform != PlatformKind.MacOS)
        {
            return [];
        }

        var output = new System.Text.StringBuilder();
        var exit = await run(Distribution.FindIdentitiesCommand(codeSigningOnly), line => output.AppendLine(line), cancellationToken).ConfigureAwait(false);
        return exit == 0 ? AppleSigning.ParseIdentities(output.ToString()) : [];
    }

    public IReadOnlyList<ProvisioningProfile> InstalledProfiles() => platform == PlatformKind.MacOS ? AppleSigning.InstalledProfiles(home) : [];

    /// <summary>The user's code-signing certificates in the Windows certificate store.</summary>
    public async Task<IReadOnlyList<StoreCertificate>> StoreCertificatesAsync(CancellationToken cancellationToken)
    {
        if (platform != PlatformKind.Windows)
        {
            return [];
        }

        var output = new System.Text.StringBuilder();
        var exit = await run(Distribution.ListCertificatesCommand(), line => output.Append(line), cancellationToken).ConfigureAwait(false);
        return exit == 0 ? Distribution.ParseCertificates(output.ToString()) : [];
    }

    /// <summary>The JDK's folder, or why there is none.</summary>
    private (string? Jdk, string? Problem) Jdk() =>
        android().Jdk is { } jdk ? (jdk, null) : (null, "Signing Android apps needs a JDK (keytool, jarsigner): install it from the Devices window or set Mobile → Java Development Kit Location.");

    /// <summary>Creates a keystore with a new upload key (<c>keytool</c>) and adds it to the user's identities.</summary>
    public async Task<(AndroidKeystore? Keystore, string Message)> CreateKeystoreAsync(string name, string path, string alias, string password, string distinguishedName, int years, Action<string>? onLine, CancellationToken cancellationToken)
    {
        if (Jdk() is (null, var problem))
        {
            return (null, problem!);
        }

        if (File.Exists(path))
        {
            return (null, $"{path} already exists: import it instead.");
        }

        if (password.Length < 6)
        {
            return (null, "A keystore's password needs at least 6 characters.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var command = Distribution.CreateKeystoreCommand(Distribution.JdkTool(Jdk().Jdk!, "keytool", platform), path, alias, distinguishedName, years, password);
        onLine?.Invoke("> " + command.Display);
        var exit = await run(command, onLine, cancellationToken).ConfigureAwait(false);
        if (exit != 0 || !File.Exists(path))
        {
            return (null, $"keytool couldn't create the keystore (exit code {exit}; see the output).");
        }

        var keystore = await identities.AddKeystoreAsync(name, path, alias, password, null, cancellationToken).ConfigureAwait(false);
        return (keystore, $"Created {Path.GetFileName(path)} with the key {alias}. Keep it and its password safe: Google Play needs the same upload key for every update.");
    }

    /// <summary>Adds an existing keystore once <c>keytool</c> opens it with the password and finds the alias.</summary>
    public async Task<(AndroidKeystore? Keystore, string Message)> ImportKeystoreAsync(string name, string path, string alias, string storePassword, string? keyPassword, CancellationToken cancellationToken)
    {
        if (Jdk() is (null, var problem))
        {
            return (null, problem!);
        }

        if (!File.Exists(path))
        {
            return (null, $"{path} doesn't exist.");
        }

        var output = new System.Text.StringBuilder();
        var exit = await run(Distribution.CheckKeystoreCommand(Distribution.JdkTool(Jdk().Jdk!, "keytool", platform), path, alias, storePassword), line => output.AppendLine(line), cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            var text = output.ToString();
            return (null, text.Contains("password", StringComparison.OrdinalIgnoreCase) ? "The keystore's password is wrong."
                : text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ? $"The keystore has no key {alias}."
                : $"keytool couldn't open the keystore: {text.Trim()}");
        }

        var keystore = await identities.AddKeystoreAsync(name, path, alias, storePassword, string.IsNullOrEmpty(keyPassword) ? null : keyPassword, cancellationToken).ConfigureAwait(false);
        return (keystore, $"Added {name}.");
    }

    /// <summary>Creates a self-signed test certificate for a Windows archive's publisher; its thumbprint, or why not.</summary>
    public async Task<(StoreCertificate? Certificate, string? PublicCertificate, string Message)> CreateTestCertificateAsync(AppArchive archive, Action<string>? onLine, CancellationToken cancellationToken)
    {
        if (platform != PlatformKind.Windows)
        {
            return (null, null, "Test certificates are made on Windows.");
        }

        if (archive.Publisher is not { Length: > 0 } publisher)
        {
            return (null, null, "The package's manifest names no publisher (Identity/@Publisher in Package.appxmanifest).");
        }

        var cer = Path.Combine(archive.Path, Safe(archive.Name) + " test certificate.cer");
        var output = new List<string>();
        var exit = await run(Distribution.CreateTestCertificateCommand(publisher, archive.Name + " test certificate", cer), line => { output.Add(line); onLine?.Invoke(line); }, cancellationToken).ConfigureAwait(false);
        var thumbprint = output.Select(l => l.Trim()).LastOrDefault(l => l.Length == 40 && l.All(char.IsAsciiHexDigit));
        return exit == 0 && thumbprint is not null
            ? (new StoreCertificate(thumbprint, publisher, _time.GetUtcNow().AddYears(1)), cer, $"Created a test certificate for {publisher}. Machines that install the package need its .cer in Trusted People.")
            : (null, null, "PowerShell couldn't create the certificate (see the output).");
    }

    // ------------------------------------------------------------------ distributing

    /// <summary>What's missing to distribute <paramref name="archive"/> to <paramref name="channel"/> with <paramref name="signing"/>, or null.</summary>
    public string? Missing(AppArchive archive, DistributionChannel channel, DistributionSigning signing) => (archive.Platform, channel) switch
    {
        (MobilePlatform.Android, _) when signing.Keystore is null => "Choose a signing key, or create one.",
        (MobilePlatform.Android, _) when Distribution.AndroidPackage(archive, channel) is null => channel == DistributionChannel.GooglePlay ? "The archive has no App Bundle (.aab)." : "The archive has no .apk.",
        (MobilePlatform.iOS or MobilePlatform.MacCatalyst, _) when platform != PlatformKind.MacOS => "Apple apps are distributed on a Mac.",
        (MobilePlatform.iOS or MobilePlatform.MacCatalyst, _) when signing.AppleIdentity is null => "Choose a signing certificate: none in the keychain fits this channel.",
        (MobilePlatform.iOS or MobilePlatform.MacCatalyst, _) when signing.Profile is null => $"Choose a provisioning profile: install a {Distribution.ProfileKind(channel)} profile for {archive.ApplicationId} (the Apple Developer site, or Xcode → Settings → Accounts).",
        (MobilePlatform.MacCatalyst, DistributionChannel.AppStore) when signing.Installer is null => "Choose an installer certificate (Mac Installer Distribution).",
        (MobilePlatform.Windows, DistributionChannel.Sideload) when platform != PlatformKind.Windows && archive.IsPackaged => "MSIX packages are signed on Windows.",
        (MobilePlatform.Windows, DistributionChannel.Sideload) when archive.IsPackaged && signing.StoreCertificate is null && signing.CertificateFile is null => "Choose a certificate, or create a test certificate.",
        _ => null,
    };

    /// <summary>Signs a copy of <paramref name="archive"/> for <paramref name="channel"/>; the tools' output to <paramref name="onLine"/>.</summary>
    public async Task<DistributionResult> DistributeAsync(AppArchive archive, DistributionChannel channel, DistributionSigning signing, Action<string>? onLine, CancellationToken cancellationToken)
    {
        if (Missing(archive, channel, signing) is { } missing)
        {
            return new DistributionResult(false, string.Empty, [], missing);
        }

        var folder = archives.DistributionFolder(archive, channel, _time.GetUtcNow());
        Directory.CreateDirectory(folder);
        try
        {
            var result = archive.Platform switch
            {
                MobilePlatform.Android => await DistributeAndroidAsync(archive, channel, signing.Keystore!, folder, onLine, cancellationToken).ConfigureAwait(false),
                MobilePlatform.iOS or MobilePlatform.MacCatalyst => await DistributeAppleAsync(archive, channel, signing, folder, onLine, cancellationToken).ConfigureAwait(false),
                MobilePlatform.Windows => await DistributeWindowsAsync(archive, channel, signing, folder, onLine, cancellationToken).ConfigureAwait(false),
                _ => new DistributionResult(false, folder, [], $"{MobileTargets.Title(archive.Platform)} apps can't be distributed."),
            };
            if (!result.Succeeded)
            {
                DeleteQuietly(folder);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(folder);
            return new DistributionResult(false, folder, [], "The distribution was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            DeleteQuietly(folder);
            return new DistributionResult(false, folder, [], ex.Message);
        }
    }

    private async Task<DistributionResult> DistributeAndroidAsync(AppArchive archive, DistributionChannel channel, AndroidKeystore keystore, string folder, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var (store, key) = await identities.KeystorePasswordsAsync(keystore, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return new DistributionResult(false, folder, [], $"The credential store has no password for {keystore.Name}: remove the key and import it again.");
        }

        if (Jdk() is (null, var problem))
        {
            return new DistributionResult(false, folder, [], problem!);
        }

        var jdk = Jdk().Jdk!;
        var input = Distribution.AndroidPackage(archive, channel)!;
        var output = Path.Combine(folder, Distribution.AndroidOutputName(archive, channel));
        ToolCommand command;
        if (channel == DistributionChannel.GooglePlay)
        {
            File.Copy(input, output, overwrite: true);
            Distribution.RemoveJarSignatures(output);
            command = Distribution.SignBundleCommand(Distribution.JdkTool(jdk, "jarsigner", platform), keystore, output, store, key ?? store, jdk);
        }
        else
        {
            if (android().Sdk is not { } sdk || Distribution.ApkSigner(sdk, platform) is not { } apksigner)
            {
                return new DistributionResult(false, folder, [], "Signing an .apk needs the Android SDK's build-tools (apksigner): install them in the Android SDK Manager.");
            }

            command = Distribution.SignApkCommand(apksigner, keystore, input, output, store, key ?? store, jdk);
        }

        onLine?.Invoke("> " + command.Display);
        var exit = await run(command, onLine, cancellationToken).ConfigureAwait(false);
        return exit == 0 && File.Exists(output)
            ? new DistributionResult(true, folder, [output], $"Signed {Path.GetFileName(output)} with {keystore.Name}.")
            : new DistributionResult(false, folder, [], $"Signing failed (exit code {exit}; see the output).");
    }

    private async Task<DistributionResult> DistributeAppleAsync(AppArchive archive, DistributionChannel channel, DistributionSigning signing, string folder, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var options = Path.Combine(folder, "ExportOptions.plist");
        await File.WriteAllTextAsync(options, Distribution.ExportOptions(archive, channel, signing.AppleIdentity!, signing.Profile!, signing.Installer), cancellationToken).ConfigureAwait(false);
        var command = Distribution.ExportCommand(archive, folder, options);
        onLine?.Invoke("> " + command.Display);
        var exit = await run(command, onLine, cancellationToken).ConfigureAwait(false);
        var files = Directory.GetFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ipa" or ".pkg").Concat(Directory.GetDirectories(folder, "*.app")).ToList();
        return exit == 0 && files.Count > 0
            ? new DistributionResult(true, folder, files, $"Exported {string.Join(", ", files.Select(Path.GetFileName))} for {Distribution.Title(channel)}.")
            : new DistributionResult(false, folder, [], $"xcodebuild couldn't export the archive (exit code {exit}; see the output).");
    }

    private async Task<DistributionResult> DistributeWindowsAsync(AppArchive archive, DistributionChannel channel, DistributionSigning signing, string folder, Action<string>? onLine, CancellationToken cancellationToken)
    {
        if (!archive.IsPackaged)
        {
            var app = archive.Packages[0];
            var zip = Path.Combine(folder, $"{Safe(archive.Name)}-{archive.Version ?? "app"}-{Path.GetFileName(archive.Framework) ?? "windows"}.zip");
            await Task.Run(() => System.IO.Compression.ZipFile.CreateFromDirectory(app, zip), cancellationToken).ConfigureAwait(false);
            return new DistributionResult(true, folder, [zip], $"Wrote {Path.GetFileName(zip)}.");
        }

        var package = archive.Packages[0];
        if (channel == DistributionChannel.MicrosoftStore)
        {
            var upload = Path.Combine(folder, Path.GetFileNameWithoutExtension(package) + ".msixupload");
            await Task.Run(() => Distribution.WriteMsixUpload(package, upload), cancellationToken).ConfigureAwait(false);
            return new DistributionResult(true, folder, [upload], $"Wrote {Path.GetFileName(upload)} for Partner Center.");
        }

        var signtool = Distribution.SignTool(environment("ProgramFiles(x86)"), environment("NUGET_PACKAGES") ?? Path.Combine(home, ".nuget", "packages"), RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        if (signtool is null)
        {
            return new DistributionResult(false, folder, [], "Signing an MSIX needs signtool.exe: install the Windows SDK (or build the app once, which restores Microsoft.Windows.SDK.BuildTools).");
        }

        var password = signing.CertificateFile is { } file ? await identities.CertificatePasswordAsync(file, cancellationToken).ConfigureAwait(false) : null;
        var signed = Path.Combine(folder, Path.GetFileName(package));
        File.Copy(package, signed, overwrite: true);
        var command = Distribution.SignMsixCommand(signtool, signed, signing.StoreCertificate, signing.CertificateFile, password);
        onLine?.Invoke("> " + Distribution.Shown(command));
        var exit = await run(command, onLine, cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return new DistributionResult(false, folder, [], $"signtool couldn't sign the package (exit code {exit}; see the output). The certificate's subject must be the manifest's publisher, {archive.Publisher}.");
        }

        var files = new List<string> { signed };
        if (signing.PublicCertificate is { } cer && File.Exists(cer))
        {
            var copy = Path.Combine(folder, Path.GetFileName(cer));
            File.Copy(cer, copy, overwrite: true);
            files.Add(copy);
        }

        return new DistributionResult(true, folder, files, $"Signed {Path.GetFileName(signed)}.");
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
