namespace Codalus.Workbench.Projects;

/// <summary>What a row of New Project's template list makes (ADR 0082).</summary>
public enum ProjectTemplateKind
{
    /// <summary>A <c>dotnet new</c> project template.</summary>
    DotnetNew,

    /// <summary>A .NET Framework project the IDE writes itself (<see cref="NetFrameworkTemplates"/>).</summary>
    NetFramework,

    /// <summary>An empty solution (<c>dotnet new sln</c>).</summary>
    BlankSolution,

    /// <summary>A template package that isn't installed: choosing it installs it (<see cref="TemplatePacks"/>).</summary>
    Pack,
}

/// <summary>A target framework a template offers, with its support label (<see cref="DotnetSupport.FrameworkLabel"/>).</summary>
public sealed record FrameworkChoice(string Moniker, string Label, bool IsPreview);

/// <summary>The type of a template's parameter, as New Project shows it.</summary>
public enum TemplateDataType
{
    Bool,
    Choice,
    Text,
    Integer,
    Float,
}

/// <summary>A choice of a choice parameter: its value, its label (with an access key <c>_</c> or none) and a description.</summary>
public sealed record TemplateChoice(string Value, string Label, string? Description = null);

/// <summary>
/// A parameter of a template that New Project shows on its Additional information page: from the template's symbols,
/// with the IDE host file's overrides (<c>ide.host.json</c>) and the template's localized strings applied.
/// </summary>
public sealed record TemplateParameter
{
    /// <summary>The symbol's name in <c>template.json</c> (<c>UseProgramMain</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The option <c>dotnet new</c> takes it as (<c>--use-program-main</c>).</summary>
    public required string CliOption { get; init; }

    /// <summary>
    /// <see cref="CliOption"/> is another symbol's that means the opposite on the CLI: the value is passed negated
    /// (Include sample pages unchecked is <c>--empty true</c>).
    /// </summary>
    public bool InverseCliValue { get; init; }

    public required TemplateDataType DataType { get; init; }

    /// <summary>The template's default, as text (<c>false</c>, <c>Server</c>); for an inverted bool, the template's own value.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>The label, possibly with an access key (<c>Do not use _top-level statements</c>).</summary>
    public required string Label { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<TemplateChoice> Choices { get; init; } = [];

    /// <summary>A template condition (<see cref="TemplateCondition"/>) that enables it, or null (always enabled).</summary>
    public string? EnabledCondition { get; init; }

    /// <summary>The check box shows the opposite of the symbol (<c>invertBoolean</c>: "Enable OpenAPI support" for <c>DisableOpenAPI</c>).</summary>
    public bool Invert { get; init; }

    /// <summary>
    /// Where its last value is remembered (<see cref="NewProjectHistory"/>): <c>group:&lt;group&gt;</c>,
    /// <c>shared:&lt;scope&gt;</c>, <c>shared::&lt;symbol&gt;</c>, or null (not remembered).
    /// </summary>
    public string? PersistenceKey { get; init; }

    /// <summary>Its place on the page (the IDE host file's order, else the template's).</summary>
    public int Order { get; init; }

    /// <summary>The value the page starts with: the IDE host file's default, else the template's (inverted for an inverted bool).</summary>
    public string? DisplayDefault { get; init; }
}

/// <summary>
/// What the IDE adds to a template's page itself (the classic IDE's host options): HTTPS, authentication, container
/// support and Aspire orchestration. Null options aren't offered.
/// </summary>
public sealed record MemberHostOptions
{
    public static MemberHostOptions None { get; } = new();

    /// <summary>The CLI option of the symbol that turns HTTPS off (<c>disableHttpsSymbol</c>, <c>--no-https</c>).</summary>
    public string? DisableHttpsOption { get; init; }

    /// <summary>The symbol <see cref="DisableHttpsOption"/> sets.</summary>
    public string? DisableHttpsSymbol { get; init; }

    /// <summary>The template's <c>auth</c> choices, or null.</summary>
    public IReadOnlyList<string>? AuthChoices { get; init; }

    /// <summary>The CLI option of <c>auth</c> (<c>--auth</c>).</summary>
    public string? AuthOption { get; init; }

    /// <summary>The CLI option of <c>UseLocalDB</c> (<c>--use-local-db</c>), or null when the template has none.</summary>
    public string? LocalDbOption { get; init; }

    /// <summary>Container support (<c>supportsDocker</c>): added through the build host after the project is.</summary>
    public bool Docker { get; init; }

    /// <summary>Enlist in Aspire orchestration: C# web templates that aren't Aspire's own.</summary>
    public bool Aspire { get; init; }

    public bool IsEmpty => DisableHttpsOption is null && AuthChoices is null && !Docker && !Aspire;
}

/// <summary>
/// One template identity of a row (<see cref="ProjectTemplateGroup"/>): the SDK installs one per channel
/// (<c>Microsoft.Test.xUnit.CSharp.10.0</c> and <c>…11.0</c>), each with its own frameworks, parameters, host file,
/// localized strings and files to open. The chosen framework picks the member (<see cref="ProjectTemplateGroup.MemberFor"/>).
/// </summary>
public sealed record ProjectTemplateMember
{
    public required string Identity { get; init; }

    public int Precedence { get; init; }

    /// <summary>Its frameworks, newest first (empty: the template has no Framework parameter).</summary>
    public IReadOnlyList<FrameworkChoice> Frameworks { get; init; } = [];

    /// <summary>The template's own default framework.</summary>
    public string? DefaultFramework { get; init; }

    /// <summary>The CLI option of the Framework parameter (<c>--framework</c>).</summary>
    public string FrameworkOption { get; init; } = "--framework";

    /// <summary>The parameters the Additional information page shows, in order.</summary>
    public IReadOnlyList<TemplateParameter> Parameters { get; init; } = [];

    public MemberHostOptions HostOptions { get; init; } = MemberHostOptions.None;

    /// <summary>The name New Project suggests (<c>ConsoleApp1</c>).</summary>
    public string? DefaultName { get; init; }

    /// <summary>The text the template replaces with the project's name (<c>Company.ConsoleApplication1</c>).</summary>
    public string? SourceName { get; init; }

    /// <summary>Which files to open after Create, from the template's <c>template.json</c>; null: none.</summary>
    public OpenFileRule? OpenFileRule { get; init; }

    /// <summary>The longest path of a file the template writes, relative to the project folder, with <see cref="SourceName"/> in it.</summary>
    public string? LongestContentPath { get; init; }

    /// <summary>The template has <c>skipRestore</c> (<c>--no-restore</c>); creation always passes it.</summary>
    public string? SkipRestoreOption { get; init; }

    /// <summary>The CLI options of IDE-only symbols that need another option on the CLI (<see cref="ProjectTemplateCatalog"/>).</summary>
    public IReadOnlyDictionary<string, string> CliOptionsBySymbol { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Values of the template's computed symbols, as conditions (<c>UseWebAssembly</c>), for <see cref="TemplateCondition"/>.</summary>
    public IReadOnlyDictionary<string, string> ComputedSymbols { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The template package (a <c>.nupkg</c> or a folder).</summary>
    public string? MountPoint { get; init; }

    /// <summary>The template's <c>template.json</c> inside <see cref="MountPoint"/>.</summary>
    public string? ConfigPlace { get; init; }

    /// <summary>Its parameters still need <c>dotnet new &lt;template&gt; --help</c> (a row from the list without the cache).</summary>
    public bool NeedsHelp { get; init; }
}

/// <summary>
/// A row of New Project's template list (ADR 0082): one template group in one language (Console App in C#), or a .NET
/// Framework template, Blank Solution, or a template package to install. Its <see cref="Members"/> are the template
/// identities of the group, highest precedence first.
/// </summary>
public sealed record ProjectTemplateGroup
{
    /// <summary>
    /// A stable id, the key of recent templates and remembered values: <c>dotnet:&lt;group&gt;|&lt;language&gt;</c>,
    /// <c>netfx:&lt;short name&gt;</c>, <c>builtin:blank-solution</c> or <c>pack:&lt;package&gt;</c>.
    /// </summary>
    public required string Id { get; init; }

    public required ProjectTemplateKind Kind { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>The short name <c>dotnet new</c> takes (<c>console</c>).</summary>
    public string ShortName { get; init; } = string.Empty;

    /// <summary>The language code the CLI takes (<c>C#</c>, <c>F#</c>, <c>VB</c>), or empty for language-free rows.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>The group identity in the template cache (<c>Microsoft.Common.Console</c>).</summary>
    public string? GroupIdentity { get; init; }

    public IReadOnlyList<string> Classifications { get; init; } = [];

    /// <summary>Platform vocabulary ids (<see cref="TemplateTags.Platforms"/>).</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Project-type vocabulary ids (<see cref="TemplateTags.ProjectTypes"/>).</summary>
    public IReadOnlyList<string> ProjectTypes { get; init; } = [];

    /// <summary>The tag chips under the row, in the display language: the language, the platforms and the project types.</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>The icon kind (<c>template-console</c>, …; <see cref="TemplateTags.IconKind"/>).</summary>
    public string IconKind { get; init; } = TemplateTags.GenericIcon;

    /// <summary>Its projects build anywhere but run only on Windows (WPF, Windows Forms, .NET Framework).</summary>
    public bool RunsOnlyOnWindows { get; init; }

    /// <summary>Known templates first (their order), then the IDE host file's order, then by name.</summary>
    public int Order { get; init; } = int.MaxValue;

    /// <summary>The package a <see cref="ProjectTemplateKind.Pack"/> row installs.</summary>
    public string? Package { get; init; }

    public IReadOnlyList<ProjectTemplateMember> Members { get; init; } = [];

    /// <summary>The frameworks of every member, newest first, each once (the first member that offers it labels it).</summary>
    public IReadOnlyList<FrameworkChoice> Frameworks =>
        [.. Members.SelectMany(m => m.Frameworks).DistinctBy(f => f.Moniker, StringComparer.OrdinalIgnoreCase).OrderBy(f => f, FrameworkOrder.Instance)];

    /// <summary>
    /// The member a framework picks: the highest-precedence member that offers <paramref name="moniker"/>; with no
    /// framework (or none offers it), the highest-precedence member.
    /// </summary>
    public ProjectTemplateMember? MemberFor(string? moniker) =>
        (moniker is null ? null : Members.Where(m => m.Frameworks.Any(f => string.Equals(f.Moniker, moniker, StringComparison.OrdinalIgnoreCase))).MaxBy(m => m.Precedence))
        ?? Members.MaxBy(m => m.Precedence);

    /// <summary>
    /// The framework New Project selects first: <paramref name="remembered"/> when the template offers it, else the newest
    /// .NET that isn't a preview, else the template's default (.NET Framework rows: <c>net48</c>), else the newest.
    /// </summary>
    public string? DefaultFramework(string? remembered = null)
    {
        var frameworks = Frameworks;
        if (frameworks.Count == 0)
        {
            return null;
        }

        if (remembered is not null && frameworks.FirstOrDefault(f => string.Equals(f.Moniker, remembered, StringComparison.OrdinalIgnoreCase)) is { } kept)
        {
            return kept.Moniker;
        }

        return frameworks.FirstOrDefault(f => !f.IsPreview && FrameworkOrder.Key(f.Moniker).Kind == 0)?.Moniker
            ?? MemberFor(null)?.DefaultFramework
            ?? frameworks[0].Moniker;
    }
}

/// <summary>Frameworks newest first: .NET (by version), then .NET Standard, then .NET Framework, then anything else.</summary>
internal sealed class FrameworkOrder : IComparer<FrameworkChoice>
{
    public static FrameworkOrder Instance { get; } = new();

    public int Compare(FrameworkChoice? x, FrameworkChoice? y)
    {
        var (kx, vx) = Key(x?.Moniker ?? string.Empty);
        var (ky, vy) = Key(y?.Moniker ?? string.Empty);
        return kx != ky ? kx.CompareTo(ky) : vy.CompareTo(vx);
    }

    /// <summary>The kind (0 .NET, 1 .NET Standard, 2 .NET Framework, 3 other) and the version.</summary>
    internal static (int Kind, Version Version) Key(string moniker)
    {
        if (moniker.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return (1, Version.TryParse(moniker[11..], out var v) ? v : new Version());
        }

        if (moniker.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            return (0, Version.TryParse(moniker[10..], out var v) ? v : new Version());
        }

        if (moniker.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var rest = moniker[3..].Split('-')[0];
            if (!rest.Contains('.', StringComparison.Ordinal) && rest.Length is >= 2 and <= 3 && rest.All(char.IsAsciiDigit))
            {
                return (2, new Version(rest[0] - '0', rest[1] - '0', rest.Length > 2 ? rest[2] - '0' : 0)); // net48, net472
            }

            return (0, Version.TryParse(rest, out var v) ? v : new Version());
        }

        return (3, new Version());
    }
}
