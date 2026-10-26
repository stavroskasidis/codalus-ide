using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Codalus.Workbench.Setup;

/// <summary>
/// Windows (ADR 0084): everything under the user's classes (<c>HKCU\Software\Classes</c>), so no administrator rights.
/// <list type="bullet">
/// <item>Folders: <c>Directory\shell\Codalus.OpenFolder</c> (a folder's right-click, <c>%1</c>) and
/// <c>Directory\Background\shell\Codalus.OpenFolder</c> (a folder's empty space, <c>%V</c>), with the app's icon.</item>
/// <item>File types: a ProgID per type (<c>Codalus.sln</c>…) that opens it, listed in the type's <c>OpenWithProgids</c>.
/// Windows doesn't let a program make itself the default (the user's choice is hashed), so Codalus IDE is registered as a
/// handler: it's in Open with, and Windows may ask once which app to use.</item>
/// </list>
/// <paramref name="classesKey"/> is the key under <c>HKEY_CURRENT_USER</c> to write in; tests pass a temporary one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOsIntegration(OsLauncher launcher, string classesKey = @"Software\Classes", Action? associationsChanged = null) : OsIntegration(launcher)
{
    /// <summary>The verb's key name, under each <c>shell</c> key.</summary>
    public const string Verb = "Codalus.OpenFolder";

    private readonly Action _associationsChanged = associationsChanged ?? Platform.WindowsShellNotify.AssociationsChanged;

    public string ClassesKey { get; } = classesKey;

    /// <summary>The ProgID of a file type: <c>Codalus.sln</c> for <c>.sln</c>.</summary>
    public static string ProgId(string extension) => "Codalus" + extension;

    private string Icon => "\"" + Launcher.Program + "\",0";

    internal override IReadOnlyList<OsEntry> Entries(OsIntegrationFeature feature) => feature switch
    {
        OsIntegrationFeature.Folders =>
        [
            VerbEntry(@"Directory\shell\" + Verb, Launcher.WindowsCommand("%1")),
            VerbEntry(@"Directory\Background\shell\" + Verb, Launcher.WindowsCommand("%V")),
        ],
        _ => [.. FileExtensions.Select(FileType)],
    };

    protected override Task AfterChangeAsync(OsIntegrationFeature feature, bool registered, CancellationToken cancellationToken)
    {
        if (feature == OsIntegrationFeature.FileTypes)
        {
            _associationsChanged();
        }

        return Task.CompletedTask;
    }

    private OsEntry VerbEntry(string key, string command) => new(key,
        () =>
        {
            using var verb = Open(key);
            if (verb is null)
            {
                return (OsEntryState.Missing, null);
            }

            using var commandKey = verb.OpenSubKey("command");
            return commandKey?.GetValue(null) is string found
                ? (string.Equals(found, command, StringComparison.OrdinalIgnoreCase) ? OsEntryState.Current : OsEntryState.OtherCopy, found)
                : (OsEntryState.Damaged, null);
        },
        _ =>
        {
            using var verb = Create(key);
            verb.SetValue(null, OpenWithLabel);
            verb.SetValue("Icon", Icon);
            using var commandKey = verb.CreateSubKey("command", writable: true);
            commandKey.SetValue(null, command);
            return Task.CompletedTask;
        },
        () =>
        {
            Delete(key);
            Prune(key[..key.LastIndexOf('\\')]);
        });

    private OsEntry FileType(string extension)
    {
        var progId = ProgId(extension);
        var command = Launcher.WindowsCommand("%1");
        var openWith = extension + @"\OpenWithProgids";
        return new OsEntry(progId,
            () =>
            {
                using var commandKey = Open(progId + @"\shell\open\command");
                using var progIdKey = Open(progId);
                using var list = Open(openWith);
                var listed = list?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true;
                var found = commandKey?.GetValue(null) as string;
                if (progIdKey is null && !listed)
                {
                    return (OsEntryState.Missing, null);
                }

                return found is null || !listed ? (OsEntryState.Damaged, found)
                    : string.Equals(found, command, StringComparison.OrdinalIgnoreCase) ? (OsEntryState.Current, found)
                    : (OsEntryState.OtherCopy, found);
            },
            _ =>
            {
                using (var key = Create(progId))
                {
                    key.SetValue(null, TypeName(extension));
                    using var icon = key.CreateSubKey("DefaultIcon", writable: true);
                    icon.SetValue(null, Icon);
                    using var commandKey = key.CreateSubKey(@"shell\open\command", writable: true);
                    commandKey.SetValue(null, command);
                }

                using var list = Create(openWith);
                list.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                return Task.CompletedTask;
            },
            () =>
            {
                Delete(progId);
                using (var list = Registry.CurrentUser.OpenSubKey(ClassesKey + "\\" + openWith, writable: true))
                {
                    list?.DeleteValue(progId, throwOnMissingValue: false);
                }

                Prune(openWith);
            });
    }

    /// <summary>The type's name in Explorer while Codalus IDE opens it.</summary>
    private static string TypeName(string extension) => extension switch
    {
        ".sln" => Localization.Localizer.Get("msg.osIntegration.typeSln", "Solution File"),
        ".slnx" => Localization.Localizer.Get("msg.osIntegration.typeSlnx", "XML Solution File"),
        ".slnf" => Localization.Localizer.Get("msg.osIntegration.typeSlnf", "Solution Filter File"),
        ".csproj" => Localization.Localizer.Get("msg.osIntegration.typeCsproj", "C# Project File"),
        ".vbproj" => Localization.Localizer.Get("msg.osIntegration.typeVbproj", "Visual Basic Project File"),
        _ => Localization.Localizer.Get("msg.osIntegration.typeFsproj", "F# Project File"),
    };

    private RegistryKey? Open(string key) => Registry.CurrentUser.OpenSubKey(ClassesKey + "\\" + key);

    private RegistryKey Create(string key) => Registry.CurrentUser.CreateSubKey(ClassesKey + "\\" + key, writable: true);

    private void Delete(string key) => Registry.CurrentUser.DeleteSubKeyTree(ClassesKey + "\\" + key, throwOnMissingSubKey: false);

    /// <summary>Deletes <paramref name="key"/> and its parents while they have no values and no subkeys (never the classes key itself).</summary>
    private void Prune(string key)
    {
        while (key.Length > 0)
        {
            using (var current = Open(key))
            {
                if (current is null || current.SubKeyCount > 0 || current.ValueCount > 0)
                {
                    return;
                }
            }

            Registry.CurrentUser.DeleteSubKey(ClassesKey + "\\" + key, throwOnMissingSubKey: false);
            var parent = key.LastIndexOf('\\');
            key = parent < 0 ? string.Empty : key[..parent];
        }
    }
}
