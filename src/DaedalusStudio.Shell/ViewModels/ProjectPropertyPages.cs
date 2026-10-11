namespace DaedalusStudio.Shell.ViewModels;

/// <summary>How a property is edited on a page.</summary>
public enum PropertyEditor
{
    Text,
    Bool,
    Choice,

    /// <summary>A semicolon-separated list (NoWarn, DefineConstants…).</summary>
    List,

    /// <summary>A path relative to the project.</summary>
    File,
}

/// <summary>
/// A property on a page (ADR 0013): the MSBuild property, how to edit it, and whether it can differ per configuration.
/// <see cref="Options"/> are the choices of <see cref="PropertyEditor.Choice"/> (value, label).
/// </summary>
public sealed record PropertyDefinition(string Name, string Label, string Description, PropertyEditor Editor = PropertyEditor.Text, bool PerConfiguration = false)
{
    public IReadOnlyList<(string Value, string Label)> Options { get; init; } = [];
}

public sealed record PropertySection(string Title, IReadOnlyList<PropertyDefinition> Properties);

/// <summary>
/// A property page. <see cref="IsLaunchProfiles"/> pages show the launch profile editor instead of properties,
/// <see cref="IsBuildEvents"/> pages the pre-build and post-build events (ADR 0034).
/// </summary>
public sealed record PropertyPage(string Id, string Title, IReadOnlyList<PropertySection> Sections, bool IsLaunchProfiles = false, bool IsBuildEvents = false)
{
    public IEnumerable<PropertyDefinition> Properties => Sections.SelectMany(s => s.Properties);
}

/// <summary>
/// The C# project property pages, as data (ADR 0013). Visual Basic and F# pages reuse the same view with their own
/// schema. <c>TargetFrameworks</c> is a pseudo property: the editor writes <c>TargetFramework</c> or
/// <c>TargetFrameworks</c> depending on how many frameworks there are.
/// </summary>
public static class ProjectPropertyPages
{
    public const string TargetFrameworksPseudo = "TargetFrameworks*";

    private static (string, string) O(string value, string? label = null) => (value, label ?? value);

    private static readonly (string, string)[] s_bool = [O("true", "Yes"), O("false", "No")];

    public static IReadOnlyList<PropertyPage> CSharp { get; } =
    [
        new("application", "Application",
        [
            new("General",
            [
                new(TargetFrameworksPseudo, "Target frameworks", "The frameworks the project builds for, e.g. net11.0, or several separated by semicolons (net10.0;net11.0)."),
                new("OutputType", "Output type", "What the project builds.", PropertyEditor.Choice) { Options = [O("Exe", "Console application"), O("WinExe", "Windows application"), O("Library", "Class library")] },
                new("AssemblyName", "Assembly name", "The name of the output assembly."),
                new("RootNamespace", "Default namespace", "The namespace of new files."),
                new("StartupObject", "Startup object", "The class whose Main method starts the program, when there is more than one."),
                new("ApplicationIcon", "Icon", "The application's .ico file.", PropertyEditor.File),
            ]),
            new("Language",
            [
                new("Nullable", "Nullable", "Nullable reference types.", PropertyEditor.Choice) { Options = [O("enable", "Enable"), O("disable", "Disable"), O("warnings", "Warnings only"), O("annotations", "Annotations only")] },
                new("ImplicitUsings", "Implicit global usings", "Adds common global using directives for the project's SDK.", PropertyEditor.Choice) { Options = [O("enable", "Enable"), O("disable", "Disable")] },
                new("LangVersion", "Language version", "The C# language version.", PropertyEditor.Choice) { Options = [O("default", "Default for the framework"), O("latest", "Latest"), O("latestMajor", "Latest major"), O("preview", "Preview"), O("15.0", "C# 15"), O("14.0", "C# 14"), O("13.0", "C# 13"), O("12.0", "C# 12"), O("11.0", "C# 11"), O("10.0", "C# 10")] },
            ]),
        ]),
        new("build", "Build",
        [
            new("General",
            [
                new("DefineConstants", "Conditional compilation symbols", "Symbols for #if, separated by semicolons. Keep $(DefineConstants) to add to the SDK's (DEBUG, TRACE, the framework).", PropertyEditor.List, PerConfiguration: true),
                new("PlatformTarget", "Platform target", "The processor architecture.", PropertyEditor.Choice, PerConfiguration: true) { Options = [O("AnyCPU", "Any CPU"), O("x64"), O("x86"), O("ARM64")] },
                new("Optimize", "Optimize code", "Enables compiler optimizations.", PropertyEditor.Bool, PerConfiguration: true) { Options = s_bool },
                new("AllowUnsafeBlocks", "Allow unsafe code", "Allows the unsafe keyword.", PropertyEditor.Bool) { Options = s_bool },
                new("Deterministic", "Deterministic", "Produces identical output for identical inputs.", PropertyEditor.Bool) { Options = s_bool },
                new("OutputPath", "Output path", "Where the build writes its output (default bin/<configuration>/).", PropertyEditor.File, PerConfiguration: true),
            ]),
            new("Errors and warnings",
            [
                new("WarningLevel", "Warning level", "How many warnings the compiler reports.", PropertyEditor.Choice) { Options = [O("0"), O("1"), O("2"), O("3"), O("4"), O("9999", "All (9999)")] },
                new("TreatWarningsAsErrors", "Treat warnings as errors", "Every warning fails the build.", PropertyEditor.Bool, PerConfiguration: true) { Options = s_bool },
                new("WarningsAsErrors", "Specific warnings as errors", "Warning codes that fail the build, separated by semicolons.", PropertyEditor.List),
                new("NoWarn", "Suppress warnings", "Warning codes to suppress, separated by semicolons. Keep $(NoWarn) to add to the SDK's.", PropertyEditor.List),
            ]),
            new("Output",
            [
                new("DebugType", "Debug symbols", "What kind of debugging information the build produces.", PropertyEditor.Choice, PerConfiguration: true) { Options = [O("portable", "Portable PDB"), O("embedded", "Embedded in the assembly"), O("full", "Full"), O("none", "None")] },
                new("GenerateDocumentationFile", "Documentation file", "Writes the XML documentation file.", PropertyEditor.Bool) { Options = s_bool },
                new("ProduceReferenceAssembly", "Reference assembly", "Produces a reference assembly (faster incremental builds).", PropertyEditor.Bool) { Options = s_bool },
            ]),
        ]),
        new("package", "Package",
        [
            new("General",
            [
                new("IsPackable", "Packable", "Whether dotnet pack produces a package for the project.", PropertyEditor.Bool) { Options = s_bool },
                new("GeneratePackageOnBuild", "Generate package on build", "Packs on every build.", PropertyEditor.Bool) { Options = s_bool },
                new("PackageId", "Package ID", "The package's id (default: the assembly name)."),
                new("Version", "Version", "The package and assembly version, e.g. 1.2.3 or 1.2.3-preview.1."),
                new("Authors", "Authors", "Separated by commas."),
                new("Company", "Company", string.Empty),
                new("Product", "Product", string.Empty),
                new("Description", "Description", "Shown in package managers."),
                new("Copyright", "Copyright", string.Empty),
                new("PackageTags", "Tags", "Separated by semicolons.", PropertyEditor.List),
            ]),
            new("Links and files",
            [
                new("PackageProjectUrl", "Project URL", string.Empty),
                new("RepositoryUrl", "Repository URL", string.Empty),
                new("PackageIcon", "Icon", "A .png in the package (pack it with the project).", PropertyEditor.File),
                new("PackageReadmeFile", "README", "A Markdown file in the package.", PropertyEditor.File),
            ]),
            new("License",
            [
                new("PackageLicenseExpression", "License expression", "An SPDX expression, e.g. MIT or Apache-2.0."),
                new("PackageLicenseFile", "License file", "A license file in the package (instead of an expression).", PropertyEditor.File),
                new("PackageRequireLicenseAcceptance", "Require license acceptance", "Asks consumers to accept the license when they install the package.", PropertyEditor.Bool) { Options = s_bool },
            ]),
            new("Symbols",
            [
                new("IncludeSymbols", "Symbol package", "Also produces a symbol package.", PropertyEditor.Bool) { Options = s_bool },
                new("SymbolPackageFormat", "Symbol package format", string.Empty, PropertyEditor.Choice) { Options = [O("snupkg"), O("symbols.nupkg")] },
            ]),
        ]),
        new("codeAnalysis", "Code Analysis",
        [
            new("Analyzers",
            [
                new("EnableNETAnalyzers", ".NET analyzers", "Runs the .NET SDK's code analyzers.", PropertyEditor.Bool) { Options = s_bool },
                new("AnalysisLevel", "Analysis level", "Which set of rules is enabled, by .NET version.", PropertyEditor.Choice) { Options = [O("latest", "Latest"), O("latest-recommended", "Latest, recommended"), O("latest-all", "Latest, all"), O("preview", "Preview"), O("none", "None"), O("10.0"), O("9.0"), O("8.0")] },
                new("AnalysisMode", "Analysis mode", "How many rules are enabled as build warnings.", PropertyEditor.Choice) { Options = [O("Default"), O("Minimum"), O("Recommended"), O("All"), O("None")] },
                new("EnforceCodeStyleInBuild", "Enforce code style on build", "Reports IDExxxx code style rules from .editorconfig in builds.", PropertyEditor.Bool) { Options = s_bool },
                new("RunAnalyzersDuringBuild", "Run on build", "Runs analyzers when building.", PropertyEditor.Bool) { Options = s_bool },
                new("RunAnalyzersDuringLiveAnalysis", "Run live", "Runs analyzers while editing.", PropertyEditor.Bool) { Options = s_bool },
            ]),
        ]),
        new("signing", "Signing",
        [
            new("Strong naming",
            [
                new("SignAssembly", "Sign the assembly", "Gives the assembly a strong name.", PropertyEditor.Bool) { Options = s_bool },
                new("AssemblyOriginatorKeyFile", "Strong name key file", "The .snk file.", PropertyEditor.File),
                new("DelaySign", "Delay sign only", "Reserves space for the signature, to be signed later.", PropertyEditor.Bool) { Options = s_bool },
                new("PublicSign", "Public sign", "Signs with the public key only (open-source projects).", PropertyEditor.Bool) { Options = s_bool },
            ]),
        ]),
        new("buildEvents", "Build Events", [], IsBuildEvents: true),
        new("debug", "Debug", [], IsLaunchProfiles: true),
    ];
}
