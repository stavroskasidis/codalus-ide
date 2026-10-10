namespace DaedalusStudio.Markup.Host.Xml;

/// <summary>A well-known MSBuild property: what it does and, when it is a choice, its values.</summary>
internal sealed record MsBuildProperty(string Name, string Description, string[]? Values = null);

/// <summary>A well-known item type and the metadata it takes.</summary>
internal sealed record MsBuildItem(string Name, string Description, string[] Metadata);

/// <summary>
/// The MSBuild vocabulary the editor offers (ADR 0016): the properties, items, metadata and tasks .NET projects use
/// most. Values that are TFMs, runtime identifiers or booleans are offered as such; anything else is free text.
/// Evaluated values come from the build host, not from here.
/// </summary>
internal static class MsBuildKnowledge
{
    public static readonly string[] Booleans = ["true", "false"];

    public static readonly string[] TargetFrameworks =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0", "net48", "net472", "net462",
        "net10.0-windows", "net10.0-android", "net10.0-ios", "net10.0-maccatalyst", "net10.0-macos", "net10.0-browser",
        "net9.0-windows", "net8.0-windows",
    ];

    public static readonly string[] RuntimeIdentifiers =
    [
        "win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64",
        "browser-wasm", "android-arm64", "ios-arm64",
    ];

    /// <summary>Elements allowed directly under <c>Project</c>.</summary>
    public static readonly string[] ProjectChildren = ["PropertyGroup", "ItemGroup", "Target", "Import", "ImportGroup", "ItemDefinitionGroup", "Choose", "Sdk", "UsingTask", "ProjectExtensions"];

    public static readonly MsBuildProperty[] Properties =
    [
        new("TargetFramework", "The target framework moniker the project builds for, e.g. `net10.0`."),
        new("TargetFrameworks", "Several target frameworks, separated by `;` (multi-targeting)."),
        new("OutputType", "What the project builds.", ["Exe", "Library", "WinExe", "Module"]),
        new("Nullable", "Nullable reference types.", ["enable", "disable", "warnings", "annotations"]),
        new("ImplicitUsings", "Global usings for the project's SDK (`System`, `System.Linq`, …).", ["enable", "disable"]),
        new("LangVersion", "The C# language version.", ["latest", "preview", "default", "latestMajor", "14", "13", "12", "11", "10"]),
        new("RootNamespace", "The default namespace of new files."),
        new("AssemblyName", "The output assembly's name."),
        new("Version", "The package and assembly version (`1.2.3` or `1.2.3-beta.1`)."),
        new("VersionPrefix", "The version without its suffix."),
        new("VersionSuffix", "The prerelease suffix, e.g. `beta.1`."),
        new("AssemblyVersion", "The assembly version (`1.0.0.0`)."),
        new("FileVersion", "The file version (`1.0.0.0`)."),
        new("InformationalVersion", "The product version shown in file properties."),
        new("Authors", "The package authors."),
        new("Company", "The company name in assembly attributes."),
        new("Product", "The product name in assembly attributes."),
        new("Description", "The package description."),
        new("Copyright", "The copyright notice."),
        new("PackageId", "The NuGet package id (default: the assembly name)."),
        new("PackageVersion", "The package version (default: `Version`)."),
        new("PackageLicenseExpression", "An SPDX license expression, e.g. `MIT`."),
        new("PackageProjectUrl", "The project's web page."),
        new("PackageReadmeFile", "The package README, a file packed into the package."),
        new("PackageIcon", "The package icon, a file packed into the package."),
        new("PackageTags", "Search tags, separated by `;`."),
        new("RepositoryUrl", "The source repository URL."),
        new("IsPackable", "Whether `dotnet pack` packs the project.", Booleans),
        new("GeneratePackageOnBuild", "Whether a build also packs.", Booleans),
        new("IncludeSymbols", "Whether to produce a symbols package.", Booleans),
        new("SymbolPackageFormat", "The symbols package format.", ["snupkg", "symbols.nupkg"]),
        new("TreatWarningsAsErrors", "Whether compiler warnings fail the build.", Booleans),
        new("WarningsAsErrors", "Warnings that fail the build, separated by `;`."),
        new("WarningsNotAsErrors", "Warnings that don't fail the build even with TreatWarningsAsErrors."),
        new("NoWarn", "Warnings to suppress, separated by `;`."),
        new("WarningLevel", "The compiler warning level.", ["0", "1", "2", "3", "4", "5", "9999"]),
        new("AnalysisLevel", "The .NET analyzers' rule set version.", ["latest", "latest-recommended", "latest-all", "preview", "10.0", "9.0", "8.0"]),
        new("AnalysisMode", "Which .NET analyzer rules are on.", ["Default", "Minimum", "Recommended", "All", "None"]),
        new("EnableNETAnalyzers", "Whether the .NET analyzers run.", Booleans),
        new("EnforceCodeStyleInBuild", "Whether code-style (IDE) rules run in builds.", Booleans),
        new("GenerateDocumentationFile", "Whether to produce the XML documentation file.", Booleans),
        new("DebugType", "The kind of debug symbols.", ["portable", "embedded", "full", "pdbonly", "none"]),
        new("Optimize", "Whether the compiler optimizes.", Booleans),
        new("DefineConstants", "Conditional compilation symbols, separated by `;`."),
        new("AllowUnsafeBlocks", "Whether `unsafe` code is allowed.", Booleans),
        new("Deterministic", "Whether builds are deterministic.", Booleans),
        new("ContinuousIntegrationBuild", "Normalizes paths for reproducible CI builds.", Booleans),
        new("RuntimeIdentifier", "The runtime the app is built for, e.g. `win-x64`."),
        new("RuntimeIdentifiers", "Runtimes to restore for, separated by `;`."),
        new("PlatformTarget", "The processor architecture.", ["AnyCPU", "x64", "x86", "ARM64"]),
        new("SelfContained", "Whether the app ships the .NET runtime.", Booleans),
        new("PublishSingleFile", "Whether publish produces one file.", Booleans),
        new("PublishTrimmed", "Whether publish trims unused code.", Booleans),
        new("PublishAot", "Whether publish compiles to native code (Native AOT).", Booleans),
        new("PublishReadyToRun", "Whether publish precompiles (ReadyToRun).", Booleans),
        new("InvariantGlobalization", "Culture-invariant globalization (no ICU).", Booleans),
        new("IsAotCompatible", "Enables the trimming and AOT analyzers for a library.", Booleans),
        new("IsTrimmable", "Marks a library as trimmable.", Booleans),
        new("UseWPF", "Whether the project uses WPF (Windows).", Booleans),
        new("UseWindowsForms", "Whether the project uses Windows Forms (Windows).", Booleans),
        new("IsTestProject", "Whether the project is a test project.", Booleans),
        new("IsPublishable", "Whether `dotnet publish` publishes the project.", Booleans),
        new("UserSecretsId", "The user secrets store's id."),
        new("ManagePackageVersionsCentrally", "Central Package Management (versions in Directory.Packages.props).", Booleans),
        new("CentralPackageTransitivePinningEnabled", "Whether central versions also pin transitive packages.", Booleans),
        new("RestorePackagesWithLockFile", "Whether restore writes packages.lock.json.", Booleans),
        new("BaseOutputPath", "The base of the output folder (default `bin\\`)."),
        new("BaseIntermediateOutputPath", "The base of the intermediate folder (default `obj\\`)."),
        new("OutputPath", "The output folder."),
        new("AppendTargetFrameworkToOutputPath", "Whether the output folder includes the target framework.", Booleans),
        new("ApplicationIcon", "The `.ico` file of the executable."),
        new("ApplicationManifest", "The Windows application manifest."),
        new("StartupObject", "The type whose `Main` runs."),
        new("EnableDefaultCompileItems", "Whether `**/*.cs` is included automatically.", Booleans),
        new("EnableDefaultItems", "Whether default items are included automatically.", Booleans),
        new("ServerGarbageCollection", "Server garbage collection.", Booleans),
        new("ConcurrentGarbageCollection", "Background garbage collection.", Booleans),
        new("TieredPGO", "Dynamic profile-guided optimization.", Booleans),
        new("Configuration", "The build configuration.", ["Debug", "Release"]),
        new("Platform", "The build platform.", ["AnyCPU", "x64", "x86", "ARM64"]),
        new("SatelliteResourceLanguages", "The cultures whose satellite assemblies are kept, e.g. `en`."),
        new("NeutralLanguage", "The assembly's neutral culture."),
        new("SignAssembly", "Whether to strong-name sign the assembly.", Booleans),
        new("AssemblyOriginatorKeyFile", "The strong-name key file."),
        new("EmitCompilerGeneratedFiles", "Whether source generators' output is written to disk.", Booleans),
        new("UseArtifactsOutput", "Whether outputs go under an `artifacts` folder.", Booleans),
    ];

    public static readonly MsBuildItem[] Items =
    [
        new("PackageReference", "A NuGet package reference.", ["Version", "VersionOverride", "PrivateAssets", "IncludeAssets", "ExcludeAssets", "GeneratePathProperty", "Aliases", "NoWarn"]),
        new("PackageVersion", "A central package version (Directory.Packages.props).", ["Version"]),
        new("ProjectReference", "A reference to another project.", ["ReferenceOutputAssembly", "Private", "PrivateAssets", "SetTargetFramework", "OutputItemType", "Aliases", "SkipGetTargetFrameworkProperties", "GlobalPropertiesToRemove"]),
        new("Reference", "An assembly reference.", ["HintPath", "Private", "Aliases", "SpecificVersion", "EmbedInteropTypes"]),
        new("Compile", "A source file to compile.", ["Link", "DependentUpon", "AutoGen", "DesignTime", "Visible"]),
        new("None", "A file in the project that isn't built.", ["CopyToOutputDirectory", "CopyToPublishDirectory", "Link", "Pack", "PackagePath", "Visible"]),
        new("Content", "A content file.", ["CopyToOutputDirectory", "CopyToPublishDirectory", "Link", "Pack", "PackagePath"]),
        new("EmbeddedResource", "A file embedded in the assembly.", ["LogicalName", "Link", "DependentUpon", "Generator", "LastGenOutput", "WithCulture"]),
        new("AdditionalFiles", "A file passed to analyzers and source generators.", ["Link"]),
        new("Analyzer", "An analyzer assembly.", []),
        new("Using", "A global using.", ["Alias", "Static"]),
        new("InternalsVisibleTo", "Generates `[InternalsVisibleTo]`.", ["Key"]),
        new("AssemblyAttribute", "Generates an assembly attribute.", ["_Parameter1", "_Parameter2"]),
        new("FrameworkReference", "A shared framework reference, e.g. `Microsoft.AspNetCore.App`.", []),
        new("Folder", "An empty folder shown in the project.", []),
        new("Protobuf", "A gRPC .proto file.", ["GrpcServices", "Access", "ProtoRoot"]),
        new("Watch", "A file `dotnet watch` watches.", []),
        new("Service", "A service reference.", []),
        new("ProjectCapability", "A project capability.", []),
        new("TrimmerRootAssembly", "An assembly the trimmer keeps.", []),
    ];

    /// <summary>Metadata values that are choices.</summary>
    public static readonly Dictionary<string, string[]> MetadataValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CopyToOutputDirectory"] = ["PreserveNewest", "Always", "Never"],
        ["CopyToPublishDirectory"] = ["PreserveNewest", "Always", "Never"],
        ["PrivateAssets"] = ["all", "none", "compile", "contentfiles", "build", "buildtransitive", "analyzers", "native", "runtime"],
        ["IncludeAssets"] = ["all", "none", "compile", "contentfiles", "build", "buildtransitive", "analyzers", "native", "runtime"],
        ["ExcludeAssets"] = ["all", "none", "compile", "contentfiles", "build", "buildtransitive", "analyzers", "native", "runtime"],
        ["ReferenceOutputAssembly"] = Booleans,
        ["Private"] = Booleans,
        ["Pack"] = Booleans,
        ["Visible"] = Booleans,
        ["GeneratePathProperty"] = Booleans,
        ["GrpcServices"] = ["Both", "Server", "Client", "None"],
    };

    public static readonly string[] Tasks =
    [
        "Message", "Warning", "Error", "Exec", "Copy", "Delete", "MakeDir", "RemoveDir", "Touch", "WriteLinesToFile", "ReadLinesFromFile",
        "CallTarget", "MSBuild", "CreateProperty", "CreateItem", "ZipDirectory", "Unzip", "DownloadFile", "WriteCodeFragment", "XmlPeek", "XmlPoke",
        "PropertyGroup", "ItemGroup",
    ];

    public static readonly Dictionary<string, string[]> Attributes = new(StringComparer.Ordinal)
    {
        ["Project"] = ["Sdk", "DefaultTargets", "InitialTargets", "TreatAsLocalProperty"],
        ["PropertyGroup"] = ["Condition", "Label"],
        ["ItemGroup"] = ["Condition", "Label"],
        ["ItemDefinitionGroup"] = ["Condition", "Label"],
        ["Import"] = ["Project", "Sdk", "Condition", "Label"],
        ["ImportGroup"] = ["Condition", "Label"],
        ["Sdk"] = ["Name", "Version"],
        ["Target"] = ["Name", "DependsOnTargets", "BeforeTargets", "AfterTargets", "Condition", "Inputs", "Outputs", "Returns", "KeepDuplicateOutputs", "Label"],
        ["UsingTask"] = ["TaskName", "AssemblyFile", "AssemblyName", "TaskFactory", "Condition"],
        ["Exec"] = ["Command", "WorkingDirectory", "ContinueOnError", "IgnoreExitCode", "Condition", "EnvironmentVariables"],
        ["Message"] = ["Text", "Importance", "Condition"],
        ["Copy"] = ["SourceFiles", "DestinationFolder", "DestinationFiles", "SkipUnchangedFiles", "Condition"],
    };

    /// <summary>Attributes every item takes.</summary>
    public static readonly string[] ItemAttributes = ["Include", "Exclude", "Remove", "Update", "Condition", "KeepMetadata", "RemoveMetadata"];

    public static MsBuildProperty? Property(string name) => Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static MsBuildItem? Item(string name) => Items.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The values offered for a property: its choices, TFMs, runtime identifiers, or none.</summary>
    public static string[] ValuesFor(string property) => property switch
    {
        "TargetFramework" or "TargetFrameworks" => TargetFrameworks,
        "RuntimeIdentifier" or "RuntimeIdentifiers" => RuntimeIdentifiers,
        _ => Property(property)?.Values ?? [],
    };
}
