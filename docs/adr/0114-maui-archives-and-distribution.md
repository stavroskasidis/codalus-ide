# 0114. .NET MAUI: archives, distribution and signing

- **Status:** accepted (2026-11-28)
- **Date:** 2026-11-28
- **Deciders:** maintainer
- **Amends:** [ADR 0042](0042-container-tooling-and-publishing.md) (Publish on a MAUI app), [ADR 0064](0064-maui-devices-and-mobile-runs.md) (`AppleSigning` is now used)

## Context

Until now, Publish on a MAUI app opened the folder publish page every project gets (ADR 0042): configuration, framework, runtime, single-file, trimming, Native AOT. None of that is how a mobile app ships. A store wants a signed package in its own format with a build number it hasn't seen. Signing needs keys and certificates that belong to the developer, not to the repository. The classic IDE's answer is the Archive Manager: Archive builds a package, and Distribute re-signs it for a channel (ad hoc, Google Play, the App Store) with a signing identity chosen there.

The platform SDKs and their tools already do the work:
- **Android:** `dotnet publish -f net10.0-android` copies `$(OutputPath)*.aab` and `*.apk` into `PublishDir` (`Microsoft.Android.Sdk.Publish.targets`). With `AndroidKeyStore=false` they're signed with the debug key. `apksigner` (build-tools) re-signs an APK and replaces its signatures. `jarsigner` (the JDK) signs an App Bundle, but adds its signature beside any earlier one. `keytool` makes keystores. All three take passwords from the environment (`env:`, `:env`).
- **iOS and Mac Catalyst:** `ArchiveOnBuild=true` makes an Xcode archive in `~/Library/Developer/Xcode/Archives/<date>/` (the SDK's `Archive` task), and its `Info.plist` names the app, its bundle id and its versions. `xcodebuild -exportArchive -exportOptionsPlist` signs a copy for a method (`app-store-connect`, `release-testing`, `enterprise`, `debugging`, `developer-id`) with a named certificate and profile.
- **Windows:** `GenerateAppxPackageOnBuild` with `AppxPackageSigningEnabled=false` makes an unsigned MSIX. `signtool sign /fd SHA256` signs it with a store certificate (`/sha1`) or a `.pfx` (`/f` and `/p`). Partner Center takes an `.msixupload`, which is a zip of the package, and signs it itself. An MSIX signs only with a certificate whose subject is the manifest's publisher.

## Options

The maintainer's answers to the open questions, 2026-11-28:

| Question | Options | Verdict |
|---|---|---|
| Platforms | Android, iOS and Mac Catalyst; all four; Android first | **All four**, Windows' MSIX included |
| Shape | Platform profiles on the Publish page (`.pubxml`); the classic IDE's Archive Manager and Distribute; both | **Archive Manager and Distribute**: one archive, many distributions |
| Stores | Sign and upload (Play's API, `altool`, `msstore`); sign and hand off; upload for Apple only | **Sign, then hand off**: the file, its folder and the store's upload page. No store credentials in the IDE |
| Android keys | User-level identities; in the `.csproj`; ask each time | **User-level**: path and alias in the user-data folder, passwords in the OS credential store; Create (`keytool`) and Import |
| Apple signing | Installed certificates and profiles; Xcode's automatic provisioning (`-allowProvisioningUpdates`); an App Store Connect API key | **Installed only**, through `xcodebuild -exportArchive`: pickers of the keychain's identities and the profiles that fit, no Apple account in the IDE |
| Windows certificates | The store, a `.pfx` and a test certificate; `.pfx` only; the store only | **All three**: the user's store (by thumbprint), `.pfx` files (password in the credential store), and a self-signed test certificate with the manifest's publisher as its subject |
| Versions | Shown, edited, +1 offered; shown only; overridden per archive | **Shown, edited, and the build number +1** when this version was archived already, written into the project file (`ProjectManagementService.SetPropertyAsync`) before the build |
| Entry point | Publish opens the Archive Manager; a separate command; a switch on the Publish page | **Publish opens it** for a MAUI app. Build → Archive Manager lists every app's archives. The folder page isn't offered for MAUI apps |

## Decision

**Workbench (`Mobile/`).**
- `AppArchives` keeps the IDE's archives in `<data>/archives/<app>/<date> <platform>/`, each with an `archive.json` (the platform, the versions, the project, the packages relative to it), and reads Xcode's archives from their `Info.plist`.
- `PropertyList` reads and writes XML property lists.
- `SigningIdentities` keeps the user's Android keystores and Windows `.pfx` files in `<data>/signing.json`, with their passwords in `SecretStorage` under `codalus.signing`.
- `Distribution` holds the channels and the commands that sign for them.
- `ArchiveService` archives through the build host (`BuildTargets.Publish`, as every publish, ADR 0042) and distributes through a `ToolProcessRunner`, so tests replay stand-in tools.
- `ProvisioningProfile` gains its kind (development, ad hoc, App Store, enterprise, Developer ID, from its devices and entitlements) and its platforms.

**An archive** is the platform's own Release publish, with these global properties:
- Android: `AndroidPackageFormats=aab;apk` and `AndroidKeyStore=false`, into the archive's `packages/` folder.
- iOS: `ArchiveOnBuild=true` and `RuntimeIdentifier=ios-arm64`. The archive is the newest Xcode archive of the app's bundle id made since the build started.
- Mac Catalyst: `ArchiveOnBuild=true`.
- Windows: `RuntimeIdentifier=win-<arch>`. A packaged app gets `WindowsPackageType=MSIX`, `GenerateAppxPackageOnBuild=true`, `AppxPackageSigningEnabled=false` and `AppxBundle=Never`. An unpackaged one gets `SelfContained=true` and `WindowsAppSDKSelfContained=true`, so it runs on a machine without the Windows App SDK runtime.

Apple's archives stay in Xcode's folder, so Xcode's Organizer lists them too, and the Archive Manager lists Xcode's archives of the solution's bundle ids. Apple archives are made only on a Mac and Windows archives only on Windows (`MobileTargets.RunsOn`). Android's are made everywhere.

**A distribution** signs a copy into `Distribution/<channel> <date>/`. That folder is inside the IDE's archive, or for an Xcode archive inside the IDE's folder beside it, since Xcode's folder holds archives only.
- **Android:** `apksigner sign --ks … --ks-pass env:… --key-pass env:…` for Ad Hoc, with the newest build-tools. For Google Play, `jarsigner -sigalg SHA256withRSA -digestalg SHA-256` runs on a copy of the bundle whose JAR signature (`META-INF/MANIFEST.MF`, `*.SF`, `*.RSA`, `*.DSA`, `*.EC`) is removed first: Google Play refuses a bundle signed twice.
- **Apple:** `xcrun xcodebuild -exportArchive` with manual signing. The export options name the method, the team, the certificate by its SHA-1, the app's profile by its UUID and, for a Mac App Store package, the installer certificate. The pickers list only what fits: a certificate by its name's kind, and profiles for the platform, the channel's kind and the bundle id, unexpired, newest expiry first.
- **Windows:** `signtool` from the newest Windows SDK, or from the `Microsoft.Windows.SDK.BuildTools` package the MAUI build restores. The `.pfx` password goes in `/p`, since `signtool` has no other way, and is masked in the output channel. Store certificates are listed through PowerShell (`Cert:\CurrentUser\My -CodeSigningCert`, with private keys), keeping those whose subject is the publisher. The test certificate comes from `New-SelfSignedCertificate` with the code-signing EKU, and its `.cer` is exported beside the package. The Microsoft Store's `.msixupload` is written as a zip. An unpackaged app is zipped.

The tools' output and the command lines go to Output → Archive. A notification offers Open Folder and Open Upload Page (the Play Console, App Store Connect, Partner Center). Nothing is uploaded.

**Shell.** The Archive Manager is `ArchiveManagerViewModel` and `ArchiveManagerView`, a management document (`archives`). It has an app picker ("All apps" when the solution has more than one), the new archive's platform, version and build number with the +1, the archives' list, and Distribute: the channel, then what that platform signs with, with inline forms to create or import keystores and import `.pfx` files. The command is `mobile.archiveManager` (Build menu, when the solution has mobile projects). `OpenPublish` sends MAUI apps there.

## Consequences

- Signing keys and passwords never enter a repository, a `.pubxml` or a command line, `signtool`'s `/p` excepted. A keystore made here is the user's to back up. The page and the user guide say so, since Google Play needs the same upload key for every update.
- Archives aren't `dotnet publish -p:PublishProfile=` profiles, so CI can't replay them as they are. The archive's properties are the SDK's documented ones, and the user guide lists them. A CI recipe (or exporting one as a `.pubxml`) is a candidate: `TODO(issue)`.
- Only the Android path's tools run on every CI runner. Apple's export and Windows' `signtool` are tested with stand-in tools and recorded output, and the smoke step `maui.devices` opens the Archive Manager in the shipped app (`maui-archives.png`) without building an archive: the runners have no MAUI workloads. The website's `maui-archives.png` comes from that step's first CI run: `TODO(issue)`.
- Not done: uploading to the stores (Google Play's Publishing API, `xcrun altool`, `msstore`); notarizing a Developer ID app (`notarytool`); signing an app's extensions with their own profiles (the export names the app's profile only); timestamping Windows signatures; Xcode's automatic provisioning. Each is a `TODO(issue)`.
