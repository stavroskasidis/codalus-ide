using System.Xml;
using System.Xml.Linq;

namespace DaedalusStudio.Workbench.Publishing;

/// <summary>Where a publish profile publishes: a folder, or a container image (the SDK's <c>PublishContainer</c>).</summary>
public enum PublishTarget
{
    Folder,
    Container,
}

/// <summary>
/// A publish profile (<c>Properties/PublishProfiles/*.pubxml</c>, M17c, ADR 0042): the MSBuild properties a publish
/// takes, in the format Visual Studio and <c>dotnet publish -p:PublishProfile=…</c> read. Properties this model doesn't
/// know stay in the file as they are.
/// </summary>
public sealed class PublishProfile
{
    public required string Name { get; set; }

    public PublishTarget Target { get; set; }

    public string Configuration { get; set; } = "Release";

    public string? TargetFramework { get; set; }

    /// <summary>A runtime identifier (<c>linux-x64</c>); null: portable.</summary>
    public string? RuntimeIdentifier { get; set; }

    public bool SelfContained { get; set; }

    public bool SingleFile { get; set; }

    public bool Trimmed { get; set; }

    public bool ReadyToRun { get; set; }

    public bool NativeAot { get; set; }

    /// <summary>The folder a folder profile publishes to (relative to the project, as the file has it).</summary>
    public string Folder { get; set; } = "bin/Release/publish/";

    public bool DeleteExistingFiles { get; set; }

    /// <summary>A container profile's registry (null: the local Docker or Podman).</summary>
    public string? ContainerRegistry { get; set; }

    public string? ContainerRepository { get; set; }

    public string? ContainerImageTag { get; set; }

    public string? ContainerBaseImage { get; set; }

    /// <summary>The file's XML, kept so unknown properties survive a save.</summary>
    public XDocument? Document { get; set; }

    /// <summary>What can't be published as asked, or null.</summary>
    public string? Problem() =>
        (SingleFile || Trimmed || ReadyToRun || NativeAot || SelfContained) && string.IsNullOrWhiteSpace(RuntimeIdentifier)
            ? "Self-contained, single-file, trimmed, ReadyToRun and Native AOT publishing need a target runtime (RID)."
            : NativeAot && SingleFile ? "Native AOT already makes one file: turn off single-file."
            : Target == PublishTarget.Container && string.IsNullOrWhiteSpace(ContainerRepository) ? "A container image needs a repository name."
            : null;
}

/// <summary>Reads, writes and lists publish profiles, and gives the MSBuild properties of a publish.</summary>
public static class PublishProfiles
{
    public static string Folder(string projectPath) => Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties", "PublishProfiles");

    /// <summary>A project's profiles, by name.</summary>
    public static IReadOnlyList<PublishProfile> List(string projectPath)
    {
        var folder = Folder(projectPath);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var profiles = new List<PublishProfile>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.pubxml").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                profiles.Add(Parse(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                // A broken profile isn't listed; the editor opens it.
            }
        }

        return profiles;
    }

    public static PublishProfile Parse(string name, string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        string? Get(string property) =>
            document.Root?.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()).LastOrDefault(e => e.Name.LocalName == property)?.Value.Trim() is { Length: > 0 } value ? value : null;
        bool Flag(string property) => string.Equals(Get(property), "true", StringComparison.OrdinalIgnoreCase);

        var container = Get("PublishProvider") == "ContainerRegistry" || Get("ContainerRepository") is not null;
        return new PublishProfile
        {
            Name = name,
            Target = container ? PublishTarget.Container : PublishTarget.Folder,
            Configuration = Get("LastUsedBuildConfiguration") ?? Get("Configuration") ?? "Release",
            TargetFramework = Get("TargetFramework"),
            RuntimeIdentifier = Get("RuntimeIdentifier"),
            SelfContained = Flag("SelfContained"),
            SingleFile = Flag("PublishSingleFile"),
            Trimmed = Flag("PublishTrimmed"),
            ReadyToRun = Flag("PublishReadyToRun"),
            NativeAot = Flag("PublishAot"),
            Folder = Get("PublishUrl") ?? Get("PublishDir") ?? "bin/Release/publish/",
            DeleteExistingFiles = Flag("DeleteExistingFiles"),
            ContainerRegistry = Get("ContainerRegistry"),
            ContainerRepository = Get("ContainerRepository"),
            ContainerImageTag = Get("ContainerImageTag") ?? Get("ContainerImageTags"),
            ContainerBaseImage = Get("ContainerBaseImage"),
            Document = document,
        };
    }

    /// <summary>The profile's file text: its properties set (or removed), anything else of the file kept.</summary>
    public static string Write(PublishProfile profile)
    {
        var document = profile.Document ?? new XDocument(
            new XComment(" A publish profile: Build → Publish uses it, and so does 'dotnet publish -p:PublishProfile=" + profile.Name + "'. "),
            new XElement("Project", new XElement("PropertyGroup")));
        var root = document.Root!;
        var group = root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup") ?? new XElement(root.Name.Namespace + "PropertyGroup");
        if (group.Parent is null)
        {
            root.Add(group);
        }

        void Set(string property, string? value)
        {
            foreach (var existing in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()).Where(e => e.Name.LocalName == property).ToList())
            {
                existing.Remove();
            }

            if (!string.IsNullOrEmpty(value))
            {
                group.Add(new XElement(root.Name.Namespace + property, value));
            }
        }

        static string? True(bool value) => value ? "true" : null;

        var container = profile.Target == PublishTarget.Container;
        Set("WebPublishMethod", container ? null : "FileSystem");
        Set("PublishProvider", container ? "ContainerRegistry" : "FileSystem");
        Set("LastUsedBuildConfiguration", profile.Configuration);
        Set("Configuration", null);
        Set("TargetFramework", profile.TargetFramework);
        Set("RuntimeIdentifier", profile.RuntimeIdentifier);
        Set("SelfContained", profile.RuntimeIdentifier is null ? null : profile.SelfContained || profile.NativeAot ? "true" : "false");
        Set("PublishSingleFile", True(profile.SingleFile));
        Set("PublishTrimmed", True(profile.Trimmed));
        Set("PublishReadyToRun", True(profile.ReadyToRun));
        Set("PublishAot", True(profile.NativeAot));
        Set("PublishUrl", container ? null : profile.Folder);
        Set("PublishDir", container ? null : profile.Folder); // the SDK publishes there directly (Web SDK projects copy to PublishUrl)
        Set("DeleteExistingFiles", container ? null : profile.DeleteExistingFiles ? "true" : "false");
        Set("ContainerRegistry", container ? profile.ContainerRegistry : null);
        Set("ContainerRepository", container ? profile.ContainerRepository : null);
        Set("ContainerImageTag", container ? profile.ContainerImageTag : null);
        Set("ContainerImageTags", null);
        Set("ContainerBaseImage", container ? profile.ContainerBaseImage : null);
        profile.Document = document;
        return document.Declaration is null ? document.ToString() + "\n" : document.Declaration + "\n" + document.ToString() + "\n";
    }

    /// <summary>Saves a profile into the project's PublishProfiles folder and returns its path.</summary>
    public static string Save(string projectPath, PublishProfile profile)
    {
        var file = Path.Combine(Folder(projectPath), profile.Name + ".pubxml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, Write(profile));
        return file;
    }

    /// <summary>
    /// The global properties of a publish with <paramref name="profile"/> (saved as a file first): the profile by name
    /// (the SDK imports it early enough for restore), <c>_IsPublishing</c> as <c>dotnet publish</c> sets it, and the
    /// configuration. A container publish to the local engine names it (<c>LocalRegistry</c>).
    /// </summary>
    public static Dictionary<string, string> Properties(PublishProfile profile, string? localEngine = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PublishProfile"] = profile.Name,
            ["_IsPublishing"] = "true",
            ["Configuration"] = profile.Configuration,
        };
        // The runtime as global properties too, as 'dotnet publish -r' passes it: the restore before the publish then
        // writes the RID's target into the assets file (NETSDK1047 otherwise, seen on macOS).
        if (profile.RuntimeIdentifier is { Length: > 0 } rid)
        {
            properties["RuntimeIdentifier"] = rid;
            properties["SelfContained"] = profile.SelfContained || profile.NativeAot ? "true" : "false";
        }

        if (profile.Target == PublishTarget.Container && string.IsNullOrEmpty(profile.ContainerRegistry) && localEngine is not null)
        {
            properties["LocalRegistry"] = localEngine.Equals("podman", StringComparison.OrdinalIgnoreCase) ? "Podman" : "Docker";
        }

        return properties;
    }

    /// <summary>The folder a folder profile publishes into, as a full path.</summary>
    public static string OutputFolder(string projectPath, PublishProfile profile) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, profile.Folder.Replace('\\', Path.DirectorySeparatorChar)));

    /// <summary>The runtime identifier of this machine (<c>win-x64</c>, <c>linux-arm64</c>, <c>osx-arm64</c>).</summary>
    public static string CurrentRuntimeIdentifier() =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}
