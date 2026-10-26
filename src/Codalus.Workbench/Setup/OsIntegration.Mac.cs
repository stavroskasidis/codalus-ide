using System.Text;
using System.Xml;
using System.Xml.Linq;
using Codalus.Platform;

namespace Codalus.Workbench.Setup;

/// <summary>
/// macOS (ADR 0084):
/// <list type="bullet">
/// <item>Folders: a Finder Quick Action (<c>~/Library/Services/Open with Codalus IDE.workflow</c>, in a folder's
/// right-click under Quick Actions or Services), which starts the app with the folder (<c>open -n -a … --args</c>), so
/// the running window takes it when it has nothing open.</item>
/// <item>File types: the app bundle declares them (<c>CFBundleDocumentTypes</c>, <c>UTImportedTypeDeclarations</c>, as an
/// alternate handler, added when it's packaged); this makes Codalus IDE their default app through LaunchServices
/// (<c>LSSetDefaultRoleHandlerForContentType</c>), after <c>lsregister</c> has read the bundle. The types it took, with
/// the app that was the default before, are kept in <paramref name="stateFile"/> (one per user, not per profile), and
/// turning it off gives each type back to that app (or to another app that opens it: LaunchServices can't unset a
/// default). Only the app bundle can be a default app.</item>
/// </list>
/// </summary>
public sealed class MacOsIntegration(OsLauncher launcher, string servicesFolder, string stateFile, ILaunchServices? launchServices, ProcessRunner? run = null, Func<string, bool>? exists = null) : OsIntegration(launcher)
{
    /// <summary>The bundle identifier in <c>eng/packaging/macos/Info.plist</c>, when the bundle's own can't be read.</summary>
    public const string DefaultBundleId = "io.github.stavroskasidis.codalus";

    private const string Lsregister = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
    private const string Pbs = "/System/Library/CoreServices/pbs";

    private readonly ProcessRunner _run = run ?? EnvironmentCheck.RunProcessAsync;
    private readonly Func<string, bool> _exists = exists ?? File.Exists;

    public string ServicesFolder { get; } = servicesFolder;

    public string StateFile { get; } = stateFile;

    public string Workflow => Path.Combine(ServicesFolder, "Open with Codalus IDE.workflow");

    /// <summary>The bundle's identifier (<c>CFBundleIdentifier</c> of its <c>Info.plist</c>).</summary>
    public string BundleId => Launcher.Bundle is { } bundle ? ReadBundleId(Path.Combine(bundle, "Contents", "Info.plist")) ?? DefaultBundleId : DefaultBundleId;

    public override string? UnavailableReason(OsIntegrationFeature feature) =>
        feature == OsIntegrationFeature.FileTypes && (Launcher.Bundle is null || launchServices is null)
            ? Localization.Localizer.Get("msg.osIntegration.macNeedsBundle", "Only the installed app (Codalus IDE.app) can open these files from the Finder: this copy doesn't run from an app bundle.")
            : null;

    internal override IReadOnlyList<OsEntry> Entries(OsIntegrationFeature feature) => feature switch
    {
        OsIntegrationFeature.Folders => [FileEntry(Workflow, XmlEscape(WorkflowCommand), () => [("Contents/Info.plist", WorkflowInfoPlist()), ("Contents/document.wflow", WorkflowDocument())], stopAt: ServicesFolder, isFolder: true, contentFile: "Contents/document.wflow")],
        _ => [.. FileExtensions.Select(FileType)],
    };

    protected override async Task BeforeRegisterAsync(OsIntegrationFeature feature, CancellationToken cancellationToken)
    {
        if (feature == OsIntegrationFeature.FileTypes && Launcher.Bundle is { } bundle)
        {
            await RunIfPresentAsync(_run, _exists(Lsregister) ? Lsregister : null, ["-f", bundle], cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task AfterChangeAsync(OsIntegrationFeature feature, bool registered, CancellationToken cancellationToken)
    {
        if (feature == OsIntegrationFeature.Folders)
        {
            // The services menu reads ~/Library/Services again.
            await RunIfPresentAsync(_run, _exists(Pbs) ? Pbs : null, ["-update"], cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ folders

    /// <summary>What the Quick Action runs for each folder.</summary>
    private string WorkflowCommand => Launcher.Bundle is { } bundle
        ? $"open -n -a {LauncherQuote(bundle)} --args{string.Concat(Launcher.Arguments.Select(a => " " + LauncherQuote(a)))}"
        : Launcher.ShellCommand;

    private static string LauncherQuote(string text) => OsLauncher.QuoteShell(text);

    private string WorkflowScript => Launcher.Bundle is not null
        ? $"""
          # {Marker}
          for folder in "$@"; do
            {WorkflowCommand} "$folder"
          done
          """
        : $"""
          # {Marker}
          for folder in "$@"; do
            nohup {WorkflowCommand} "$folder" >/dev/null 2>&1 &
          done
          """;

    private string WorkflowInfoPlist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <!-- {Marker}: Codalus IDE wrote this Quick Action (Tools > Options > Environment > System Integration); turning that off removes it. -->
        <plist version="1.0">
        <dict>
        	<key>NSServices</key>
        	<array>
        		<dict>
        			<key>NSBackgroundColorName</key>
        			<string>background</string>
        			<key>NSIconName</key>
        			<string>NSActionTemplate</string>
        			<key>NSMenuItem</key>
        			<dict>
        				<key>default</key>
        				<string>{XmlEscape(OpenWithLabel)}</string>
        			</dict>
        			<key>NSMessage</key>
        			<string>runWorkflowAsService</string>
        			<key>NSRequiredContext</key>
        			<dict>
        				<key>NSApplicationIdentifier</key>
        				<string>com.apple.finder</string>
        			</dict>
        			<key>NSSendFileTypes</key>
        			<array>
        				<string>public.folder</string>
        			</array>
        		</dict>
        	</array>
        </dict>
        </plist>

        """;

    /// <summary>An Automator workflow with one Run Shell Script action (input as arguments), for folders in the Finder.</summary>
    private string WorkflowDocument() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>AMApplicationBuild</key>
        	<string>523</string>
        	<key>AMApplicationVersion</key>
        	<string>2.10</string>
        	<key>AMDocumentVersion</key>
        	<string>2</string>
        	<key>actions</key>
        	<array>
        		<dict>
        			<key>action</key>
        			<dict>
        				<key>AMAccepts</key>
        				<dict>
        					<key>Container</key>
        					<string>List</string>
        					<key>Optional</key>
        					<true/>
        					<key>Types</key>
        					<array>
        						<string>com.apple.cocoa.path</string>
        					</array>
        				</dict>
        				<key>AMActionVersion</key>
        				<string>2.0.3</string>
        				<key>AMApplication</key>
        				<array>
        					<string>Automator</string>
        				</array>
        				<key>AMParameterProperties</key>
        				<dict>
        					<key>COMMAND_STRING</key>
        					<dict/>
        					<key>CheckedForUserDefaultShell</key>
        					<dict/>
        					<key>inputMethod</key>
        					<dict/>
        					<key>shell</key>
        					<dict/>
        					<key>source</key>
        					<dict/>
        				</dict>
        				<key>AMProvides</key>
        				<dict>
        					<key>Container</key>
        					<string>List</string>
        					<key>Types</key>
        					<array>
        						<string>com.apple.cocoa.string</string>
        					</array>
        				</dict>
        				<key>ActionBundlePath</key>
        				<string>/System/Library/Automator/Run Shell Script.action</string>
        				<key>ActionName</key>
        				<string>Run Shell Script</string>
        				<key>ActionParameters</key>
        				<dict>
        					<key>COMMAND_STRING</key>
        					<string>{XmlEscape(WorkflowScript)}</string>
        					<key>CheckedForUserDefaultShell</key>
        					<true/>
        					<key>inputMethod</key>
        					<integer>1</integer>
        					<key>shell</key>
        					<string>/bin/sh</string>
        					<key>source</key>
        					<string></string>
        				</dict>
        				<key>BundleIdentifier</key>
        				<string>com.apple.RunShellScript</string>
        				<key>CFBundleVersion</key>
        				<string>2.0.3</string>
        				<key>CanShowSelectedItemsWhenRun</key>
        				<false/>
        				<key>CanShowWhenRun</key>
        				<true/>
        				<key>Category</key>
        				<array>
        					<string>AMCategoryUtilities</string>
        				</array>
        				<key>Class Name</key>
        				<string>RunShellScriptAction</string>
        				<key>InputUUID</key>
        				<string>4B5A9E5C-7D1F-4E8B-9C21-0F6C1D2A3B41</string>
        				<key>Keywords</key>
        				<array>
        					<string>Shell</string>
        					<string>Script</string>
        					<string>Command</string>
        					<string>Run</string>
        					<string>Unix</string>
        				</array>
        				<key>OutputUUID</key>
        				<string>9E2C4F1A-3B6D-4A7E-8F10-2D5C6B7A8E92</string>
        				<key>UUID</key>
        				<string>1C7D3E5F-6A8B-4C9D-A0E1-B2C3D4E5F603</string>
        				<key>UnlocalizedApplications</key>
        				<array>
        					<string>Automator</string>
        				</array>
        				<key>isViewVisible</key>
        				<integer>1</integer>
        				<key>location</key>
        				<string>309.000000:253.000000</string>
        				<key>nibPath</key>
        				<string>/System/Library/Automator/Run Shell Script.action/Contents/Resources/Base.lproj/main.nib</string>
        			</dict>
        			<key>isViewVisible</key>
        			<integer>1</integer>
        		</dict>
        	</array>
        	<key>connectors</key>
        	<dict/>
        	<key>workflowMetaData</key>
        	<dict>
        		<key>applicationBundleIDsByPath</key>
        		<dict/>
        		<key>applicationPaths</key>
        		<array/>
        		<key>inputTypeIdentifier</key>
        		<string>com.apple.Automator.fileSystemObject.folder</string>
        		<key>outputTypeIdentifier</key>
        		<string>com.apple.Automator.nothing</string>
        		<key>presentationMode</key>
        		<integer>15</integer>
        		<key>processesInput</key>
        		<false/>
        		<key>serviceApplicationBundleID</key>
        		<string>com.apple.finder</string>
        		<key>serviceApplicationPath</key>
        		<string>/System/Library/CoreServices/Finder.app</string>
        		<key>serviceInputTypeIdentifier</key>
        		<string>com.apple.Automator.fileSystemObject.folder</string>
        		<key>serviceOutputTypeIdentifier</key>
        		<string>com.apple.Automator.nothing</string>
        		<key>serviceProcessesInput</key>
        		<false/>
        		<key>systemImageName</key>
        		<string>NSActionTemplate</string>
        		<key>useAutomaticInputType</key>
        		<false/>
        		<key>workflowTypeIdentifier</key>
        		<string>com.apple.Automator.servicesMenu</string>
        	</dict>
        </dict>
        </plist>

        """;

    // ------------------------------------------------------------------ file types

    /// <summary>
    /// A type is Codalus IDE's when it made itself the default (it's in <see cref="StateFile"/>) and still is: a default the
    /// user chose in the Finder stays the user's, and isn't taken away by turning this off.
    /// </summary>
    private OsEntry FileType(string extension) => new(extension,
        () =>
        {
            if (TypeOf(extension) is not { } type || !ReadState().ContainsKey(type))
            {
                return (OsEntryState.Missing, null);
            }

            var handler = launchServices!.DefaultHandler(type);
            return string.Equals(handler, BundleId, StringComparison.OrdinalIgnoreCase) ? (OsEntryState.Current, null) : (OsEntryState.Damaged, handler);
        },
        _ =>
        {
            var type = TypeOf(extension) ?? throw new IOException(Localization.Localizer.Format("msg.osIntegration.noType", "macOS has no type for {0} files.", extension));
            var state = ReadState();
            if (!state.ContainsKey(type))
            {
                var previous = launchServices!.DefaultHandler(type);
                state[type] = string.Equals(previous, BundleId, StringComparison.OrdinalIgnoreCase) ? string.Empty : previous ?? string.Empty;
                WriteState(state);
            }

            if (!launchServices!.SetDefaultHandler(type, BundleId))
            {
                throw new IOException(Localization.Localizer.Format("msg.osIntegration.launchServicesRefused", "macOS didn't make Codalus IDE the app for {0} files.", extension));
            }

            return Task.CompletedTask;
        },
        () =>
        {
            if (TypeOf(extension) is not { } type)
            {
                return;
            }

            // The app before, or another app that opens it (LaunchServices can't unset a default); with none, Codalus IDE
            // stays the only app for it, as any app that declares a type is.
            var state = ReadState();
            if (string.Equals(launchServices!.DefaultHandler(type), BundleId, StringComparison.OrdinalIgnoreCase)
                && (state.GetValueOrDefault(type) is { Length: > 0 } before ? before : launchServices.Handlers(type).FirstOrDefault(h => !string.Equals(h, BundleId, StringComparison.OrdinalIgnoreCase))) is { } back)
            {
                _ = launchServices.SetDefaultHandler(type, back);
            }

            if (state.Remove(type))
            {
                WriteState(state);
            }
        });

    private string? TypeOf(string extension) => launchServices?.TypeForExtension(extension.TrimStart('.'));

    /// <summary>The types Codalus IDE made itself the default for, with the app before (empty: none): one <c>type&lt;TAB&gt;bundle id</c> per line.</summary>
    private Dictionary<string, string> ReadState() =>
        File.Exists(StateFile)
            ? File.ReadAllLines(StateFile).Select(l => l.Split('\t')).Where(p => p.Length == 2).GroupBy(p => p[0], StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal)
            : new(StringComparer.Ordinal);

    private void WriteState(Dictionary<string, string> state)
    {
        if (state.Count == 0)
        {
            File.Delete(StateFile);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, string.Concat(state.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\t" + p.Value + "\n")), new UTF8Encoding(false));
    }

    /// <summary>The <c>CFBundleIdentifier</c> of an XML property list, or null.</summary>
    internal static string? ReadBundleId(string plist)
    {
        try
        {
            if (!File.Exists(plist))
            {
                return null;
            }

            using var reader = XmlReader.Create(plist, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var keys = XDocument.Load(reader).Root?.Element("dict")?.Elements().ToList() ?? [];
            var index = keys.FindIndex(e => e.Name == "key" && e.Value == "CFBundleIdentifier");
            return index >= 0 && index + 1 < keys.Count && keys[index + 1].Name == "string" ? keys[index + 1].Value : null;
        }
        catch (XmlException)
        {
            return null; // a binary property list: the packaged identifier
        }
    }
}
