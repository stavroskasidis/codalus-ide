using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codalus.Workbench.Projects;

/// <summary>What converting a project to SDK-style changes: the project file's new text, other files' new text, the files deleted, and notes.</summary>
public sealed record SdkConversion(string ProjectPath, string ProjectText, IReadOnlyList<SdkConversionFile> ChangedFiles, IReadOnlyList<string> DeletedFiles, IReadOnlyList<string> Notes);

/// <summary>A file a conversion rewrites (<c>AssemblyInfo.cs</c> without the attributes the SDK now generates).</summary>
public sealed record SdkConversionFile(string Path, string Text);

/// <summary>Why a project can't be converted (already SDK-style, Visual Studio's web targets, not .NET Framework).</summary>
public sealed class SdkConversionException(string message) : Exception(message);

/// <summary>
/// Converts a non-SDK-style ("old-style") .NET Framework project to an SDK-style one (M43a, ADR 0075), in the spirit of
/// try-convert (MIT; its approach, none of its code): the project file becomes <c>&lt;Project Sdk="Microsoft.NET.Sdk"&gt;</c>
/// with <c>TargetFramework</c> from <c>TargetFrameworkVersion</c>, the properties the SDK sets the same way are left out
/// (configuration defaults, <c>ProjectGuid</c>, <c>FileAlignment</c>…), files the SDK's globs include are left out
/// while files in the folder that weren't in the project are removed from them, framework references the SDK adds are
/// left out, <c>packages.config</c> becomes <c>PackageReference</c> items (the packages other listed packages depend on
/// left out, when their <c>.nuspec</c> is in the repository path) and its hint-path references go, NuGet's imports of
/// package targets go, and <c>AssemblyInfo.cs</c>'s attributes that the SDK generates become properties. Pure: it
/// reads the files and returns what would change; the IDE previews it and applies it.
/// </summary>
public static partial class SdkStyleConverter
{
    private const string Indent = "  ";

    /// <summary>The framework assemblies an SDK-style .NET Framework project references by itself.</summary>
    private static readonly HashSet<string> s_implicitReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "System", "System.Core", "System.Data", "System.Drawing", "System.IO.Compression.FileSystem",
        "System.Numerics", "System.Runtime.Serialization", "System.Xml", "System.Xml.Linq",
    };

    private static readonly HashSet<string> s_windowsFormsReferences = new(StringComparer.OrdinalIgnoreCase) { "System.Windows.Forms", "System.Deployment" };

    private static readonly HashSet<string> s_wpfReferences = new(StringComparer.OrdinalIgnoreCase) { "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml" };

    /// <summary>Properties that say nothing in an SDK-style project (identity for Visual Studio, ClickOnce defaults, the SDK's own defaults).</summary>
    private static readonly HashSet<string> s_droppedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProjectGuid", "ProjectTypeGuids", "FileAlignment", "AppDesignerFolder", "SchemaVersion", "ProductVersion", "NuGetPackageImportStamp",
        "TargetFrameworkVersion", "TargetFrameworkProfile", "TargetFrameworkIdentifier", "OldToolsVersion", "UpgradeBackupLocation", "FileUpgradeFlags",
        "SccProjectName", "SccLocalPath", "SccAuxPath", "SccProvider", "ProjectTypeGuid",
        "VisualStudioVersion", "VSToolsPath", "ReferencePath", "IsCodedUITest", "TestProjectType", // Visual Studio's test project targets
    };

    private static readonly HashSet<string> s_clickOnceProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsWebBootstrapper", "PublishUrl", "Install", "InstallFrom", "UpdateEnabled", "UpdateMode", "UpdateInterval", "UpdateIntervalUnits",
        "UpdatePeriodically", "UpdateRequired", "MapFileExtensions", "ApplicationRevision", "ApplicationVersion", "UseApplicationTrust", "BootstrapperEnabled",
    };

    /// <summary>The attributes of <c>AssemblyInfo.cs</c> the SDK generates, and the property each comes from.</summary>
    private static readonly Dictionary<string, string?> s_assemblyAttributes = new(StringComparer.Ordinal)
    {
        ["AssemblyTitle"] = "AssemblyTitle",
        ["AssemblyDescription"] = "Description",
        ["AssemblyCompany"] = "Company",
        ["AssemblyProduct"] = "Product",
        ["AssemblyCopyright"] = "Copyright",
        ["AssemblyVersion"] = "AssemblyVersion",
        ["AssemblyFileVersion"] = "FileVersion",
        ["AssemblyInformationalVersion"] = "InformationalVersion",
        ["AssemblyConfiguration"] = null, // the SDK writes $(Configuration)
    };

    private const string TestProjectTypeGuid = "{3AC096D0-A1C2-E12C-1390-A8335801FDAB}";

    /// <summary>What a folder of another project (or <c>packages</c>) inside the project's folder is removed from.</summary>
    private static readonly string[] s_folderRemovals = ["Compile", "EmbeddedResource", "None"];

    /// <summary>Converts the project at <paramref name="projectPath"/>.</summary>
    /// <param name="repositoryPath">Where its packages.config packages are (their <c>.nuspec</c> files tell which are dependencies of others); null: <c>packages</c> next to the solution isn't known, so all are kept.</param>
    public static SdkConversion Convert(string projectPath, string? repositoryPath = null)
    {
        var text = File.ReadAllText(projectPath);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        XDocument document;
        try
        {
            document = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new SdkConversionException($"{Path.GetFileName(projectPath)} isn't valid XML: {ex.Message}");
        }

        var root = document.Root ?? throw new SdkConversionException($"{Path.GetFileName(projectPath)} has no Project element.");
        if (root.Attribute("Sdk") is not null || root.Elements().Any(e => e.Name.LocalName == "Sdk") || root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null))
        {
            throw new SdkConversionException($"{Path.GetFileName(projectPath)} is SDK-style already.");
        }

        return new Converter(projectPath, root, newline, repositoryPath).Run();
    }

    private sealed class Converter(string projectPath, XElement root, string newline, string? repositoryPath)
    {
        private readonly string _directory = Path.GetDirectoryName(projectPath)!;
        private readonly string _name = Path.GetFileNameWithoutExtension(projectPath);
        private readonly List<string> _notes = [];
        private readonly List<(string Name, string Value)> _main = [];
        private readonly List<(string Name, string Value)> _trailing = [];
        private readonly Dictionary<string, List<XElement>> _configurations = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<XElement> _otherGroups = [];
        private readonly List<XNode> _others = [];
        private readonly List<string> _removes = [];
        private readonly List<XElement> _items = [];
        private readonly List<XElement> _references = [];
        private readonly List<XElement> _packages = [];
        private readonly List<XElement> _projectReferences = [];
        private readonly List<SdkConversionFile> _changed = [];
        private readonly List<string> _deleted = [];
        private readonly HashSet<string> _platforms = new(StringComparer.OrdinalIgnoreCase);
        private bool _clickOnce;

        public SdkConversion Run()
        {
            var properties = UnconditionalProperties();
            if (Imports().Any(i => IsVisualStudioImport(i.Project) && !IsExistsCondition(i.Condition)))
            {
                throw new SdkConversionException(
                    $"{_name} imports proprietary MSBuild targets ($(VSToolsPath)): an SDK-style project of its kind (an ASP.NET web application, for one) needs an SDK of its own, so it isn't converted. Convert it by hand.");
            }

            var version = properties.GetValueOrDefault("TargetFrameworkVersion");
            var moniker = ShortName(version) ?? throw new SdkConversionException($"{_name} isn't a .NET Framework project (no TargetFrameworkVersion), so it isn't converted.");
            var references = root.Descendants().Where(e => e.Name.LocalName == "Reference").Select(e => ReferenceName(e)).ToList();
            var windowsForms = references.Contains("System.Windows.Forms", StringComparer.OrdinalIgnoreCase);
            var wpf = references.Contains("PresentationFramework", StringComparer.OrdinalIgnoreCase);
            var outputType = properties.GetValueOrDefault("OutputType");
            if (outputType is { Length: > 0 } && !outputType.Equals("Library", StringComparison.OrdinalIgnoreCase))
            {
                _main.Add(("OutputType", outputType));
            }

            _main.Add(("TargetFramework", moniker));
            if (properties.TryGetValue("TargetFrameworkProfile", out var profile) && profile.Length > 0)
            {
                _notes.Add($"TargetFrameworkProfile {profile} was left out: SDK-style projects target the full framework.");
            }

            if (properties.GetValueOrDefault("RootNamespace") is { Length: > 0 } rootNamespace && rootNamespace != _name)
            {
                _main.Add(("RootNamespace", rootNamespace));
            }

            if (properties.GetValueOrDefault("AssemblyName") is { Length: > 0 } assemblyName && assemblyName != _name)
            {
                _main.Add(("AssemblyName", assemblyName));
            }

            if (windowsForms)
            {
                _main.Add(("UseWindowsForms", "true"));
            }

            if (wpf)
            {
                _main.Add(("UseWPF", "true"));
            }

            Properties();
            var packages = PackagesConfig();
            Items(windowsForms, wpf, packages);
            DiskRemovals(wpf);
            AssemblyInfo();
            if (properties.GetValueOrDefault("ProjectTypeGuids") is { } guids && guids.Contains(TestProjectTypeGuid, StringComparison.OrdinalIgnoreCase)
                && !packages.Any(p => p.Id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)))
            {
                _notes.Add("It's a test project: add the Microsoft.NET.Test.Sdk package (and its test framework's adapter) for dotnet test and the Test Explorer to run its tests.");
            }

            if (_clickOnce)
            {
                _notes.Add("ClickOnce's publishing properties were left out (ClickOnce publishing isn't supported for SDK-style .NET Framework projects here).");
            }

            if (_platforms.Count > 0)
            {
                _main.Add(("Platforms", "AnyCPU;" + string.Join(';', _platforms.Where(p => !p.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))));
            }

            return new SdkConversion(projectPath, Write(), _changed, _deleted, _notes);
        }

        // ------------------------------------------------------------------ properties

        private Dictionary<string, string> UnconditionalProperties()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && Condition(e) is null))
            {
                foreach (var property in group.Elements().Where(p => Condition(p) is null))
                {
                    result[property.Name.LocalName] = property.Value.Trim();
                }
            }

            return result;
        }

        private IEnumerable<(string Project, string? Condition)> Imports() =>
            root.Descendants().Where(e => e.Name.LocalName == "Import" && Condition(e)?.Trim() is not "false").Select(e => ((string?)e.Attribute("Project") ?? string.Empty, Condition(e)));

        private void Properties()
        {
            var debugType = false;
            foreach (var group in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                var condition = Condition(group);
                if (condition is null)
                {
                    foreach (var property in group.Elements())
                    {
                        var name = property.Name.LocalName;
                        var value = property.Value.Trim();
                        if (Condition(property) is { } own)
                        {
                            if (name is "Configuration" or "Platform" && own.Replace(" ", string.Empty, StringComparison.Ordinal).EndsWith("==''", StringComparison.Ordinal))
                            {
                                continue; // the defaults the SDK sets too
                            }

                            if (!s_droppedProperties.Contains(name))
                            {
                                _otherGroups.Add(new XElement("PropertyGroup", Strip(property))); // kept with its condition
                            }

                            continue;
                        }

                        if (s_droppedProperties.Contains(name) || name is "OutputType" or "RootNamespace" or "AssemblyName")
                        {
                            continue;
                        }

                        if (s_clickOnceProperties.Contains(name))
                        {
                            _clickOnce = true;
                            continue;
                        }

                        if ((name == "Deterministic" && value.Equals("true", StringComparison.OrdinalIgnoreCase))
                            || (name == "AutoGenerateBindingRedirects" && value.Equals("true", StringComparison.OrdinalIgnoreCase))
                            || (name == "StartupObject" && value.Length == 0)
                            || (name == "WarningLevel" && value == "4")
                            || (name == "ErrorReport" && value == "prompt"))
                        {
                            continue; // the SDK's defaults
                        }

                        _main.Add((name, value));
                    }

                    continue;
                }

                if (ConfigurationOf(condition) is not { } configuration)
                {
                    _otherGroups.Add(Strip(group));
                    continue;
                }

                if (!configuration.Platform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase))
                {
                    _platforms.Add(configuration.Platform);
                }

                var kept = new List<XElement>();
                foreach (var property in group.Elements())
                {
                    var name = property.Name.LocalName;
                    var value = property.Value.Trim();
                    var isDebug = configuration.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase);
                    var isRelease = configuration.Configuration.Equals("Release", StringComparison.OrdinalIgnoreCase);
                    var defaultValue = name switch
                    {
                        "PlatformTarget" => value.Equals(configuration.Platform, StringComparison.OrdinalIgnoreCase),
                        "DebugSymbols" => true,
                        "DebugType" => true,
                        "Optimize" => (isDebug && value.Equals("false", StringComparison.OrdinalIgnoreCase)) || (isRelease && value.Equals("true", StringComparison.OrdinalIgnoreCase)),
                        "OutputPath" => SameOutputPath(value, configuration),
                        "DefineConstants" => (isDebug && value == "DEBUG;TRACE") || (isRelease && value == "TRACE"),
                        "ErrorReport" => value == "prompt",
                        "WarningLevel" => value == "4",
                        _ => false,
                    };
                    if (name == "DebugType" && value is "full" or "pdbonly")
                    {
                        debugType = true;
                    }

                    if (!defaultValue)
                    {
                        kept.Add(Strip(property));
                    }
                }

                if (kept.Count > 0)
                {
                    var key = $"'$(Configuration)|$(Platform)' == '{configuration.Configuration}|{configuration.Platform}'";
                    if (!_configurations.TryGetValue(key, out var list))
                    {
                        _configurations[key] = list = [];
                    }

                    list.AddRange(kept);
                }
            }

            if (debugType)
            {
                _notes.Add("DebugType full and pdbonly were left out: SDK-style projects write portable PDBs, which the debuggers and stack traces of .NET Framework 4.7.2 and later read.");
            }
        }

        private static bool SameOutputPath(string value, (string Configuration, string Platform) configuration)
        {
            var normalized = value.Replace('/', '\\').TrimEnd('\\');
            return normalized.Equals($"bin\\{configuration.Configuration}", StringComparison.OrdinalIgnoreCase)
                   || (!configuration.Platform.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase) && normalized.Equals($"bin\\{configuration.Platform}\\{configuration.Configuration}", StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ packages.config

        private sealed record Package(string Id, string Version, bool Development);

        private List<Package> PackagesConfig()
        {
            var path = File.Exists(Path.Combine(_directory, "packages." + _name + ".config")) ? Path.Combine(_directory, "packages." + _name + ".config") : Path.Combine(_directory, "packages.config");
            if (!File.Exists(path))
            {
                return [];
            }

            _deleted.Add(path);
            List<Package> packages;
            try
            {
                packages = [.. (XDocument.Parse(File.ReadAllText(path)).Root?.Elements("package") ?? [])
                    .Select(p => new Package(((string?)p.Attribute("id"))?.Trim() ?? string.Empty, ((string?)p.Attribute("version"))?.Trim() ?? string.Empty,
                        string.Equals((string?)p.Attribute("developmentDependency"), "true", StringComparison.OrdinalIgnoreCase)))
                    .Where(p => p.Id.Length > 0 && p.Version.Length > 0)];
            }
            catch (System.Xml.XmlException ex)
            {
                throw new SdkConversionException($"{Path.GetFileName(path)} isn't valid XML: {ex.Message}");
            }

            // packages.config lists every package, its dependencies' too; PackageReference lists the top ones.
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unknown = 0;
            foreach (var package in packages)
            {
                if (repositoryPath is null || Nuspec(package) is not { } nuspec)
                {
                    unknown++;
                    continue;
                }

                dependencies.UnionWith(nuspec.Descendants().Where(e => e.Name.LocalName == "dependency").Select(e => (string?)e.Attribute("id")).OfType<string>());
            }

            foreach (var package in packages)
            {
                if (dependencies.Contains(package.Id))
                {
                    _notes.Add($"{package.Id} was left out of the package references: another package depends on it, so it comes with that one.");
                    continue;
                }

                var reference = new XElement("PackageReference", new XAttribute("Include", package.Id), new XAttribute("Version", package.Version));
                if (package.Development)
                {
                    reference.Add(new XElement("PrivateAssets", "all"));
                }

                _packages.Add(reference);
            }

            if (packages.Count > 0)
            {
                _notes.Add($"packages.config's {packages.Count} package(s) became PackageReference items, and packages.config goes."
                           + (unknown > 0 ? " Restore first (the .nuspec files in the packages folder tell which packages depend on others) to leave out the ones that come with others." : string.Empty));
            }

            return packages;
        }

        private XDocument? Nuspec(Package package)
        {
            var file = Path.Combine(repositoryPath!, package.Id + "." + package.Version, package.Id + ".nuspec");
            try
            {
                return File.Exists(file) ? XDocument.Parse(File.ReadAllText(file)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ items

        private readonly HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);

        private void Items(bool windowsForms, bool wpf, List<Package> packages)
        {
            foreach (var node in root.Nodes())
            {
                if (node is XComment comment)
                {
                    if (!comment.Value.Contains("To modify your build process", StringComparison.Ordinal))
                    {
                        _others.Add(new XComment(comment.Value));
                    }

                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "PropertyGroup":
                        break;
                    case "ItemGroup" when Condition(element) is null:
                        foreach (var item in element.Elements())
                        {
                            Item(item, windowsForms, wpf, packages);
                        }

                        break;
                    case "Import":
                        Import(element);
                        break;
                    case "Target" when (string?)element.Attribute("Name") == "EnsureNuGetPackageBuildImports":
                        break;
                    case "Target" when (string?)element.Attribute("Name") is "BeforeBuild" or "AfterBuild":
                        _notes.Add($"The target {(string?)element.Attribute("Name")} is kept, but the SDK's own empty one, imported after the project, replaces it: rename it and give it {((string?)element.Attribute("Name") == "BeforeBuild" ? "BeforeTargets=\"Build\"" : "AfterTargets=\"Build\"")}.");
                        _others.Add(Strip(element));
                        break;
                    default:
                        _others.Add(Strip(element));
                        break;
                }
            }
        }

        private void Import(XElement element)
        {
            var project = ((string?)element.Attribute("Project") ?? string.Empty).Replace('/', '\\');
            if (project.EndsWith("\\Microsoft.Common.props", StringComparison.OrdinalIgnoreCase)
                || project.EndsWith("\\Microsoft.CSharp.targets", StringComparison.OrdinalIgnoreCase)
                || project.EndsWith("\\Microsoft.VisualBasic.targets", StringComparison.OrdinalIgnoreCase))
            {
                return; // the SDK imports them
            }

            if (IsVisualStudioImport(project))
            {
                _notes.Add($"The import of {project[(project.LastIndexOf('\\') + 1)..]} was left out: it's one of the proprietary MSBuild targets ($(VSToolsPath)), imported only where it exists.");
                return;
            }

            if (project.Contains("packages\\", StringComparison.OrdinalIgnoreCase) && project.Contains("\\build\\", StringComparison.OrdinalIgnoreCase))
            {
                _notes.Add($"The import of a package's targets ({project}) was left out: a PackageReference imports them.");
                return;
            }

            _others.Add(Strip(element));
        }

        private void Item(XElement item, bool windowsForms, bool wpf, List<Package> packages)
        {
            var type = item.Name.LocalName;
            var include = ((string?)item.Attribute("Include") ?? string.Empty).Trim();
            if (item.Attribute("Remove") is not null || item.Attribute("Update") is not null || Condition(item) is not null)
            {
                _items.Add(Strip(item));
                return;
            }

            switch (type)
            {
                case "Reference":
                    Reference(item, include, windowsForms, wpf, packages);
                    return;
                case "ProjectReference":
                    var reference = new XElement("ProjectReference", new XAttribute("Include", include));
                    reference.Add(item.Elements().Where(m => m.Name.LocalName is not ("Project" or "Name")).Select(Strip));
                    _projectReferences.Add(reference);
                    return;
                case "Folder":
                case "Service":
                case "BootstrapperPackage":
                    return;
                case "Analyzer" when IsUnder((string?)include, "packages"):
                    return; // a package's analyzer: its PackageReference brings it
                case "Compile":
                case "EmbeddedResource":
                case "None":
                case "Page" when wpf:
                case "ApplicationDefinition" when wpf:
                    Globbed(item, type, include, wpf);
                    return;
                default:
                    _items.Add(Strip(item));
                    return;
            }
        }

        /// <summary>An item type the SDK's globs include: left out when it's a plain file in the folder, an Update when it has metadata.</summary>
        private void Globbed(XElement item, string type, string include, bool wpf)
        {
            var relative = include.Replace('/', '\\');
            var full = Path.GetFullPath(Path.Combine(_directory, relative.Replace('\\', Path.DirectorySeparatorChar)));
            var inside = full.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var file = Path.GetFileName(relative);
            if (type == "None" && (file.Equals("packages.config", StringComparison.OrdinalIgnoreCase) || file.Equals("packages." + _name + ".config", StringComparison.OrdinalIgnoreCase)))
            {
                return; // it goes
            }

            if (!inside || include.Contains('*', StringComparison.Ordinal) || include.Contains(';', StringComparison.Ordinal) || item.Elements().Any(m => m.Name.LocalName == "Link")
                || (type == "None" && file.Equals("App.config", StringComparison.OrdinalIgnoreCase) && item.HasElements))
            {
                _items.Add(Strip(item)); // linked, outside the folder, a wildcard of its own: as it was
                if (inside && !include.Contains('*', StringComparison.Ordinal))
                {
                    _listed.Add(type + "|" + full);
                }

                return;
            }

            _listed.Add(type + "|" + full);
            if (type == "None" && file.Equals("App.config", StringComparison.OrdinalIgnoreCase))
            {
                return; // the SDK uses App.config by itself
            }

            var metadata = item.Elements().Where(m => !IsConventional(m, type, relative, wpf)).ToList();
            if (metadata.Count == 0)
            {
                return; // the glob includes it
            }

            var update = new XElement(type, new XAttribute("Update", relative));
            update.Add(metadata.Select(Strip));
            _items.Add(update);
        }

        /// <summary>Metadata the SDK (or the IDE's nesting) gives anyway: <c>SubType</c>, <c>X.Designer.cs</c> upon <c>X.cs</c>, WPF's page generator.</summary>
        private static bool IsConventional(XElement metadata, string type, string relative, bool wpf)
        {
            var name = metadata.Name.LocalName;
            var value = metadata.Value.Trim();
            if (name == "SubType")
            {
                return true;
            }

            if (name == "DependentUpon")
            {
                var file = Path.GetFileName(relative);
                var stem = Path.GetFileNameWithoutExtension(file);
                return (type == "Compile" && stem.EndsWith(".Designer", StringComparison.OrdinalIgnoreCase) && value.Equals(stem[..^".Designer".Length] + Path.GetExtension(file), StringComparison.OrdinalIgnoreCase))
                       || (type == "EmbeddedResource" && Path.GetFileNameWithoutExtension(value).Equals(stem, StringComparison.OrdinalIgnoreCase))
                       || (type == "Compile" && wpf && value.Equals(stem, StringComparison.OrdinalIgnoreCase) && stem.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
            }

            return wpf && type is "Page" or "ApplicationDefinition" && name == "Generator" && value == "MSBuild:Compile";
        }

        private void Reference(XElement item, string include, bool windowsForms, bool wpf, List<Package> packages)
        {
            var name = include.Split(',')[0].Trim();
            var hint = item.Elements().FirstOrDefault(m => m.Name.LocalName == "HintPath")?.Value.Trim().Replace('/', '\\');
            if (hint is null)
            {
                if (s_implicitReferences.Contains(name) || (windowsForms && s_windowsFormsReferences.Contains(name)) || (wpf && s_wpfReferences.Contains(name)))
                {
                    return;
                }

                var reference = new XElement("Reference", new XAttribute("Include", name));
                reference.Add(item.Elements().Where(m => m.Name.LocalName != "RequiredTargetFramework").Select(Strip));
                _references.Add(reference);
                return;
            }

            if (packages.Any(p => hint.Contains("\\" + p.Id + "." + p.Version + "\\", StringComparison.OrdinalIgnoreCase) || hint.StartsWith(p.Id + "." + p.Version + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                return; // the PackageReference brings it
            }

            var kept = new XElement("Reference", new XAttribute("Include", include));
            kept.Add(item.Elements().Select(Strip));
            _references.Add(kept);
        }

        // ------------------------------------------------------------------ files in the folder that weren't in the project

        private void DiskRemovals(bool wpf)
        {
            var globs = new List<(string Type, string Extension)> { ("Compile", Path.GetExtension(projectPath).Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ? ".vb" : ".cs"), ("EmbeddedResource", ".resx") };
            if (wpf)
            {
                globs.Add(("Page", ".xaml"));
            }

            var excludedFolders = new List<string>();
            foreach (var (relative, full) in Files(_directory, string.Empty, excludedFolders))
            {
                foreach (var (type, extension) in globs)
                {
                    if (full.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && !_listed.Contains(type + "|" + full)
                        && !(type == "Page" && Path.GetFileName(full).Equals("App.xaml", StringComparison.OrdinalIgnoreCase) && _listed.Contains("ApplicationDefinition|" + full)))
                    {
                        _removes.Add($"<{type} Remove=\"{Escape(relative)}\" />");
                    }
                }
            }

            foreach (var folder in excludedFolders)
            {
                foreach (var type in s_folderRemovals)
                {
                    _removes.Add($"<{type} Remove=\"{Escape(folder)}\\**\" />");
                }
            }
        }

        /// <summary>The files under the project's folder, but not in <c>bin</c>, <c>obj</c>, hidden folders, or folders of other projects and <c>packages</c> (listed in <paramref name="excludedFolders"/>).</summary>
        private static IEnumerable<(string Relative, string Full)> Files(string directory, string prefix, List<string> excludedFolders)
        {
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                yield return (prefix + Path.GetFileName(file), file);
            }

            foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (prefix.Length == 0 && name is "bin" or "obj" || name.StartsWith('.'))
                {
                    continue;
                }

                if (name.Equals("packages", StringComparison.OrdinalIgnoreCase) || Directory.EnumerateFiles(child, "*.*proj").Any())
                {
                    excludedFolders.Add(prefix + name);
                    continue;
                }

                foreach (var entry in Files(child, prefix + name + "\\", excludedFolders))
                {
                    yield return entry;
                }
            }
        }

        // ------------------------------------------------------------------ AssemblyInfo.cs

        private void AssemblyInfo()
        {
            var source = _listed.Where(l => l.StartsWith("Compile|", StringComparison.Ordinal)).Select(l => l["Compile|".Length..])
                .FirstOrDefault(f => Path.GetFileName(f).Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase));
            if (source is null || !File.Exists(source))
            {
                if (Path.GetExtension(projectPath).Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    _trailing.Add(("GenerateAssemblyInfo", "false"));
                    _notes.Add("GenerateAssemblyInfo is false: the project's AssemblyInfo.vb keeps its attributes.");
                }

                return;
            }

            var text = File.ReadAllText(source);
            var newlineOfFile = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var moved = new List<string>();
            var keep = new bool[lines.Length];
            var paragraphs = new List<(int Start, int End)>();
            var start = 0;
            for (var i = 0; i <= lines.Length; i++)
            {
                if (i == lines.Length || lines[i].Trim().Length == 0)
                {
                    if (i > start)
                    {
                        paragraphs.Add((start, i));
                    }

                    start = i + 1;
                }
            }

            Array.Fill(keep, true);
            foreach (var (first, end) in paragraphs)
            {
                var attributes = 0;
                var removed = 0;
                for (var i = first; i < end; i++)
                {
                    if (AssemblyAttribute().Match(lines[i]) is not { Success: true } match)
                    {
                        continue;
                    }

                    attributes++;
                    var attribute = match.Groups["name"].Value.Replace("Attribute", string.Empty, StringComparison.Ordinal);
                    var argument = match.Groups["value"].Value.Trim();
                    if (attribute is "AssemblyTrademark" or "AssemblyCulture" && Literal(argument) is "")
                    {
                        keep[i] = false;
                        removed++;
                        continue;
                    }

                    if (!s_assemblyAttributes.TryGetValue(attribute, out var property))
                    {
                        continue;
                    }

                    var literal = Literal(argument);
                    if (literal is null || (attribute == "AssemblyVersion" && literal.Contains('*', StringComparison.Ordinal)))
                    {
                        _trailing.Add(("Generate" + attribute + "Attribute", "false"));
                        if (attribute == "AssemblyVersion" && literal is not null)
                        {
                            _trailing.Add(("Deterministic", "false"));
                            _notes.Add($"AssemblyVersion(\"{literal}\") stays in {Path.GetFileName(source)}: a wildcard version needs Deterministic false.");
                        }

                        continue;
                    }

                    keep[i] = false;
                    removed++;
                    if (property is not null && literal.Length > 0)
                    {
                        _trailing.Add((property, literal));
                        moved.Add(attribute);
                    }
                }

                if (attributes > 0 && removed == attributes)
                {
                    for (var i = first; i < end; i++)
                    {
                        keep[i] = false; // its comment goes with the attributes it described
                    }
                }
            }

            var remaining = lines.Where((_, i) => keep[i]).ToList();
            if (moved.Count > 0)
            {
                _notes.Add($"{string.Join(", ", moved)} moved from {Path.GetFileName(source)} into the project's properties: the SDK generates them.");
            }

            if (remaining.All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("//", StringComparison.Ordinal) || l.TrimStart().StartsWith("using ", StringComparison.Ordinal)))
            {
                _deleted.Add(source);
                return;
            }

            // Blank lines collapsed where paragraphs went.
            var collapsed = new List<string>();
            foreach (var line in remaining)
            {
                if (line.Trim().Length == 0 && (collapsed.Count == 0 || collapsed[^1].Trim().Length == 0))
                {
                    continue;
                }

                collapsed.Add(line);
            }

            while (collapsed.Count > 0 && collapsed[^1].Trim().Length == 0)
            {
                collapsed.RemoveAt(collapsed.Count - 1);
            }

            _changed.Add(new SdkConversionFile(source, string.Join(newlineOfFile, collapsed) + newlineOfFile));
        }

        /// <summary>A C# string literal's value (<c>"a \"b\""</c>, <c>@"c:\x"</c>), or null for anything else (a constant, an expression).</summary>
        private static string? Literal(string argument)
        {
            if (argument.StartsWith("@\"", StringComparison.Ordinal) && argument.EndsWith('"') && argument.Length >= 3)
            {
                return argument[2..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
            }

            if (!argument.StartsWith('"') || !argument.EndsWith('"') || argument.Length < 2)
            {
                return null;
            }

            var body = argument[1..^1];
            var result = new StringBuilder();
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '"')
                {
                    return null; // two literals, or an expression
                }

                if (body[i] != '\\' || i + 1 >= body.Length)
                {
                    result.Append(body[i]);
                    continue;
                }

                i++;
                result.Append(body[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    _ => body[i],
                });
            }

            return result.ToString();
        }

        // ------------------------------------------------------------------ writing

        private string Write()
        {
            var output = new StringBuilder();
            output.Append("<Project Sdk=\"Microsoft.NET.Sdk\">").Append(newline);
            void Group(string? condition, IEnumerable<string> lines)
            {
                var list = lines.ToList();
                if (list.Count == 0)
                {
                    return;
                }

                output.Append(newline).Append(Indent).Append(condition is null ? "<PropertyGroup>" : $"<PropertyGroup Condition=\"{Escape(condition)}\">").Append(newline);
                foreach (var line in list)
                {
                    output.Append(Indent).Append(Indent).Append(line).Append(newline);
                }

                output.Append(Indent).Append("</PropertyGroup>").Append(newline);
            }

            void Items(IEnumerable<string> lines)
            {
                var list = lines.ToList();
                if (list.Count == 0)
                {
                    return;
                }

                output.Append(newline).Append(Indent).Append("<ItemGroup>").Append(newline);
                foreach (var line in list)
                {
                    output.Append(Indent).Append(Indent).Append(line).Append(newline);
                }

                output.Append(Indent).Append("</ItemGroup>").Append(newline);
            }

            var main = _main.Concat(_trailing.Where(t => !_main.Any(m => m.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase))))
                .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
            Group(null, main.Select(p => $"<{p.Name}>{Escape(p.Value)}</{p.Name}>"));
            foreach (var (condition, properties) in _configurations)
            {
                Group(condition, properties.SelectMany(p => Lines(p, 0)));
            }

            foreach (var group in _otherGroups)
            {
                output.Append(newline);
                foreach (var line in Lines(group, 1))
                {
                    output.Append(line).Append(newline);
                }
            }

            Items(_removes);
            Items(_items.SelectMany(i => Lines(i, 0)));
            Items(_references.SelectMany(i => Lines(i, 0)));
            Items(_packages.SelectMany(i => Lines(i, 0)));
            Items(_projectReferences.SelectMany(i => Lines(i, 0)));
            foreach (var node in _others)
            {
                output.Append(newline);
                foreach (var line in node is XElement element ? Lines(element, 1) : [Indent + node.ToString()])
                {
                    output.Append(line).Append(newline);
                }
            }

            output.Append(newline).Append("</Project>").Append(newline);
            return output.ToString();
        }

        /// <summary>An element as indented lines, at <paramref name="depth"/> indents.</summary>
        private static IEnumerable<string> Lines(XElement element, int depth) =>
            element.ToString(SaveOptions.None).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => string.Concat(Enumerable.Repeat(Indent, depth)) + l);
    }

    // ------------------------------------------------------------------ helpers

    private static string? Condition(XElement element) => (string?)element.Attribute("Condition") is { } condition && condition.Trim().Length > 0 ? condition : null;

    /// <summary><c>'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'</c> → (Debug, AnyCPU).</summary>
    private static (string Configuration, string Platform)? ConfigurationOf(string condition) =>
        ConfigurationCondition().Match(condition) is { Success: true } match ? (match.Groups["configuration"].Value, match.Groups["platform"].Value) : null;

    private static bool IsVisualStudioImport(string project) =>
        project.Contains("$(VSToolsPath)", StringComparison.OrdinalIgnoreCase) || project.Replace('/', '\\').Contains("\\Microsoft\\VisualStudio\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsExistsCondition(string? condition) => condition?.Trim().StartsWith("Exists(", StringComparison.OrdinalIgnoreCase) == true;

    private static string ReferenceName(XElement reference) => (((string?)reference.Attribute("Include")) ?? string.Empty).Split(',')[0].Trim();

    private static bool IsUnder(string? path, string folder) =>
        path is not null && path.Replace('/', '\\').Split('\\').Contains(folder, StringComparer.OrdinalIgnoreCase);

    /// <summary>The element and its content without the MSBuild namespace (SDK-style projects have none).</summary>
    private static XElement Strip(XElement element) =>
        new(element.Name.LocalName,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name.LocalName, a.Value)),
            element.Nodes().Select(n => n switch
            {
                XElement child => (object)Strip(child),
                XComment comment => new XComment(comment.Value),
                XText text when text.Value.Trim().Length > 0 || !element.HasElements => new XText(text.Value),
                _ => null!,
            }).Where(n => n is not null));

    /// <summary>Text for an element or a double-quoted attribute (apostrophes stay as they are, as in MSBuild conditions).</summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string? ShortName(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var text = version.Trim().TrimStart('v', 'V');
        return Version.TryParse(text.Contains('.', StringComparison.Ordinal) ? text : text + ".0", out var parsed) && parsed.Major is >= 1 and <= 4
            ? "net" + parsed.Major + Math.Max(0, parsed.Minor) + (parsed.Build > 0 ? parsed.Build.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
            : null;
    }

    [GeneratedRegex(@"^\s*'\$\(Configuration\)\|\$\(Platform\)'\s*==\s*'(?<configuration>[^|']+)\|(?<platform>[^']+)'\s*$")]
    private static partial Regex ConfigurationCondition();

    [GeneratedRegex(@"^\s*\[\s*assembly\s*:\s*(?:System\.Reflection\.)?(?<name>Assembly\w+?)(?:Attribute)?\s*\((?<value>.*)\)\s*\]\s*(//.*)?$")]
    private static partial Regex AssemblyAttribute();
}
