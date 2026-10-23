using System.Text.Json;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>A template package New Project offers to install while none of its templates is (<see cref="Prefix"/>).</summary>
public sealed record TemplatePack(string Package, string Prefix);

/// <summary>The template packs New Project knows: Avalonia's (M19b) and CoreWCF's (M42), both from nuget.org.</summary>
public static class TemplatePacks
{
    public static IReadOnlyList<TemplatePack> Known { get; } = [new("Avalonia.Templates", "avalonia"), new("CoreWCF.Templates", "corewcf")];

    /// <summary>The known packs none of whose templates' short names is in <paramref name="shortNames"/>.</summary>
    public static IEnumerable<TemplatePack> Missing(IEnumerable<string> shortNames)
    {
        var names = shortNames.ToList();
        return Known.Where(p => !names.Any(n => n.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)));
    }

    public static string NameOf(TemplatePack pack) => pack.Package switch
    {
        "Avalonia.Templates" => Localizer.Get("templates.pack.avalonia", "Avalonia templates"),
        "CoreWCF.Templates" => Localizer.Get("templates.pack.corewcf", "CoreWCF templates"),
        _ => pack.Package,
    };

    public static string DescriptionOf(TemplatePack pack) => pack.Package switch
    {
        "Avalonia.Templates" => Localizer.Get("templates.pack.avalonia.description", "Cross-platform desktop, mobile and browser apps with Avalonia UI, from the Avalonia.Templates package."),
        "CoreWCF.Templates" => Localizer.Get("templates.pack.corewcf.description", "WCF services on .NET with CoreWCF, from the CoreWCF.Templates package."),
        _ => pack.Package,
    };
}

/// <summary>
/// New Project's templates (ADR 0082). <c>dotnet new list --type project</c> (in the neutral folder) says which short
/// names and languages are installed and usable here (it refreshes the template cache and applies the templates'
/// constraints); the template cache (<see cref="TemplateCacheReader"/>) and each template's own files in its package
/// (<see cref="TemplatePackageReader"/>: <c>ide.host.json</c>, <c>template.json</c>, the localized strings) give the rest:
/// groups of members, parameters, tags, files to open. When the cache can't be read, the list's columns and each
/// template's <c>--help</c> (<see cref="TemplateHelp"/>) stand in. The .NET Framework templates, Blank Solution and the
/// known packs that aren't installed are rows too. The result is kept until the CLI's packages change.
/// </summary>
public sealed class ProjectTemplateCatalog
{
    /// <summary>Listed first, in this order, with readable names (<c>dotnet new list</c> truncates long names).</summary>
    private static readonly (string ShortName, string Name)[] s_known =
    [
        ("console", "Console App"),
        ("classlib", "Class Library"),
        ("xunit", "xUnit Test Project"),
        ("nunit", "NUnit Test Project"),
        ("mstest", "MSTest Test Project"),
        ("worker", "Worker Service"),
        ("web", "ASP.NET Core Empty"),
        ("webapi", "ASP.NET Core Web API"),
        ("webapp", "ASP.NET Core Web App (Razor Pages)"),
        ("mvc", "ASP.NET Core Web App (Model-View-Controller)"),
        ("blazor", "Blazor Web App"),
        ("grpc", "ASP.NET Core gRPC Service"),
        ("corewcf", "CoreWCF Service"), // CoreWCF.Templates (M42)
        ("razorclasslib", "Razor Class Library"),
        ("maui", ".NET MAUI App"), // with the MAUI workloads (M20a)
        ("maui-blazor", ".NET MAUI Blazor Hybrid App"),
        ("mauilib", ".NET MAUI Class Library"),
        ("android", "Android Application"),
        ("ios", "iOS Application"),
        ("wpf", "WPF Application"), // M40a: WPF and Windows Forms run on Windows only (D31)
        ("wpflib", "WPF Class Library"),
        ("wpfusercontrollib", "WPF User Control Library"),
        ("wpfcustomcontrollib", "WPF Custom Control Library"),
        ("winforms", "Windows Forms App"),
        ("winformslib", "Windows Forms Class Library"),
        ("winformscontrollib", "Windows Forms Control Library"),
    ];

    /// <summary>Parameters that are the CLI's plumbing, not choices for the page (templates without an IDE host file).</summary>
    private static readonly HashSet<string> s_plumbing = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "language", "type", "Framework", "TargetFrameworkOverride", "langVersion", "skipRestore", "UserSecretsId", "ExcludeLaunchSettings",
        "auth", "UseLocalDB",
    };

    /// <summary>
    /// Symbols an IDE host sets that <c>dotnet new</c> reads only through another option (the CLI host's conditions
    /// ignore them): their CLI option, inverted (Include sample pages unchecked is <c>--empty</c>).
    /// </summary>
    private static readonly Dictionary<string, string> s_inverseCliSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IncludeSampleContent"] = "Empty",
    };

    private static readonly string[] s_prereleaseMarkers = ["-preview", "-rc", "-alpha", "-beta", "-ci", "-dev"];

    private readonly ITemplateCli _cli;
    private readonly TemplateCacheLocator _locator;
    private readonly Func<DateOnly> _today;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProjectTemplateMember> _completed = new(StringComparer.Ordinal);
    private Task<IReadOnlyList<ProjectTemplateGroup>>? _load;

    public ProjectTemplateCatalog(ITemplateCli cli, TemplateCacheLocator? locator = null, Func<DateOnly>? today = null)
    {
        _cli = cli;
        _locator = locator ?? new TemplateCacheLocator();
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        cli.Changed += (_, _) => Invalidate();
    }

    /// <summary>The templates changed (a package was installed or removed): the next <see cref="LoadAsync"/> reads them again.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the last load's details came from: <c>cache</c>, <c>list</c> (the fallback) or <c>none</c>.</summary>
    public string Source { get; private set; } = "none";

    /// <summary>The cache file the last load read, if any.</summary>
    public string? CacheFile { get; private set; }

    /// <summary>Why the last load found no <c>dotnet new</c> templates (the CLI's last line), or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>The rows, read once until the installed packages change. It runs off the calling thread.</summary>
    public Task<IReadOnlyList<ProjectTemplateGroup>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_load is null || _load.IsFaulted || _load.IsCanceled)
            {
                _load = Task.Run(() => ReadAsync(cancellationToken), cancellationToken);
            }

            return _load;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _load = null;
            _completed.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The member with its frameworks and parameters: as it is when the cache gave them, else read from
    /// <c>dotnet new &lt;template&gt; [--language] --help</c> (once per row).
    /// </summary>
    public async Task<ProjectTemplateMember> CompleteAsync(ProjectTemplateGroup group, ProjectTemplateMember member, CancellationToken cancellationToken)
    {
        if (!member.NeedsHelp)
        {
            return member;
        }

        lock (_gate)
        {
            if (_completed.TryGetValue(group.Id, out var done))
            {
                return done;
            }
        }

        string[] args = group.Language.Length > 0 ? ["new", group.ShortName, "--language", group.Language, "--help"] : ["new", group.ShortName, "--help"];
        var help = await _cli.RunAsync(args, cancellationToken).ConfigureAwait(false);
        var completed = help.Succeeded ? FromHelp(member, help.Output, _today()) : member with { NeedsHelp = false };
        lock (_gate)
        {
            _completed[group.Id] = completed;
        }

        return completed;
    }

    /// <summary>The member a help text describes: its frameworks, its options (the host options taken out of them).</summary>
    internal static ProjectTemplateMember FromHelp(ProjectTemplateMember member, string help, DateOnly today)
    {
        var frameworks = TemplateHelp.Frameworks(help);
        var options = TemplateHelp.Parameters(help);
        string? Option(string cli) => options.FirstOrDefault(o => string.Equals(o.CliOption, cli, StringComparison.Ordinal))?.CliOption;
        var auth = options.FirstOrDefault(o => o.CliOption == "--auth");
        return member with
        {
            NeedsHelp = false,
            Frameworks = [.. frameworks.Select(f => new FrameworkChoice(f.Framework, DotnetSupport.FrameworkLabel(f.Framework, false, today), false)).OrderBy(f => f, FrameworkOrder.Instance)],
            DefaultFramework = frameworks.FirstOrDefault(f => f.IsDefault).Framework,
            Parameters = [.. options.Where(o => o.CliOption is not ("--auth" or "--no-https" or "--use-local-db"))],
            SkipRestoreOption = help.Contains("--no-restore", StringComparison.Ordinal) ? "--no-restore" : null,
            HostOptions = new MemberHostOptions
            {
                DisableHttpsOption = Option("--no-https"),
                DisableHttpsSymbol = Option("--no-https") is null ? null : "NoHttps",
                AuthChoices = auth is null ? null : SupportedAuth(auth.Choices.Select(c => c.Value)),
                AuthOption = auth?.CliOption,
                LocalDbOption = Option("--use-local-db"),
            },
        };
    }

    // ------------------------------------------------------------------ loading

    private async Task<IReadOnlyList<ProjectTemplateGroup>> ReadAsync(CancellationToken cancellationToken)
    {
        var list = await _cli.RunAsync(["new", "list", "--type", "project"], cancellationToken).ConfigureAwait(false);
        var listed = list.Succeeded ? TemplateService.ParseList(list.Output) : [];
        LoadError = listed.Count == 0 ? list.LastMeaningfulLine : null;
        var available = new HashSet<(string, string)>();
        foreach (var template in listed)
        {
            foreach (var language in template.Languages.DefaultIfEmpty(string.Empty))
            {
                available.Add((template.ShortName.ToLowerInvariant(), language));
            }
        }

        var groups = new List<ProjectTemplateGroup>();
        if (listed.Count > 0)
        {
            var version = await _cli.RunAsync(["--version"], cancellationToken).ConfigureAwait(false);
            var cacheFile = _locator.Find(_cli.Hive, version.Succeeded ? version.Output.Trim().Split('\n')[0].Trim() : null);
            var cached = cacheFile is null ? null : TemplateCacheReader.Read(cacheFile);
            CacheFile = cached is null ? null : cacheFile;
            if (cached is not null)
            {
                Source = "cache";
                groups.AddRange(FromCache(cached, available, Localizer.Locale, _today()));
            }
            else
            {
                Source = "list";
                var all = await _cli.RunAsync(["new", "list", "--type", "project", "--columns-all"], cancellationToken).ConfigureAwait(false);
                groups.AddRange(FromList(all.Succeeded ? all.Output : list.Output));
            }
        }
        else
        {
            Source = "none";
        }

        groups.AddRange(BuiltInRows(groups.Select(g => g.ShortName), _today()));
        return Sorted(groups);
    }

    /// <summary>The rows from the cache: project templates the list shows, grouped by group identity and language.</summary>
    internal static IReadOnlyList<ProjectTemplateGroup> FromCache(IReadOnlyList<CachedTemplate> cached, IReadOnlySet<(string ShortName, string Language)> available, string? culture, DateOnly today)
    {
        var templates = cached
            .Where(t => string.Equals(t.Type, "project", StringComparison.OrdinalIgnoreCase))
            .Where(t => t.ShortNames.Any(s => available.Contains((s.ToLowerInvariant(), t.Language))))
            .ToList();

        var previews = Previews(templates);

        var files = new Dictionary<string, TemplatePackageFiles>(StringComparer.Ordinal);
        foreach (var package in templates.Where(t => t.MountPointUri is not null && t.ConfigPlace is not null).GroupBy(t => t.MountPointUri!, StringComparer.Ordinal))
        {
            using var opened = TemplatePackageReader.Open(package.Key);
            foreach (var template in package)
            {
                files[template.Identity] = opened?.Files(template.ConfigPlace!, culture) ?? TemplatePackageFiles.Empty;
            }
        }

        var groups = new List<ProjectTemplateGroup>();
        foreach (var group in templates.GroupBy(t => (Group: t.GroupIdentity ?? t.Identity, t.Language)))
        {
            var members = group.OrderByDescending(t => t.Precedence)
                .Select(t => Member(t, files.GetValueOrDefault(t.Identity) ?? TemplatePackageFiles.Empty, group.Key.Group, previews, today))
                .ToList();
            var top = group.MaxBy(t => t.Precedence)!;
            var topFiles = files.GetValueOrDefault(top.Identity) ?? TemplatePackageFiles.Empty;
            var host = TemplateHostFile.Parse(topFiles.IdeHost);
            var strings = Strings(topFiles.Strings);
            var windowsOnly = RunsOnlyOnWindows(top.OperatingSystems, host.Tags, top.Classifications, top.ShortName);
            var platforms = TemplateTags.PlatformsOf(top.Classifications, host.Tags, windowsOnly);
            var types = TemplateTags.ProjectTypesOf(top.Classifications, host.Tags);
            var knownIndex = Array.FindIndex(s_known, k => top.ShortNames.Contains(k.ShortName, StringComparer.OrdinalIgnoreCase));
            groups.Add(new ProjectTemplateGroup
            {
                Id = $"dotnet:{group.Key.Group}|{group.Key.Language}",
                Kind = ProjectTemplateKind.DotnetNew,
                Name = strings.GetValueOrDefault("name") ?? top.Name,
                Description = strings.GetValueOrDefault("description") ?? top.Description,
                ShortName = top.ShortName,
                Language = top.Language,
                GroupIdentity = group.Key.Group,
                Classifications = top.Classifications,
                Platforms = platforms,
                ProjectTypes = types,
                Chips = TemplateTags.Chips(top.Language, platforms, types),
                IconKind = TemplateTags.IconKind(types, top.Classifications),
                RunsOnlyOnWindows = windowsOnly,
                Order = knownIndex >= 0 ? knownIndex : 1000 + (host.Order ?? 100000),
                Members = members,
            });
        }

        return groups;
    }

    /// <summary>One member: its frameworks, its visible parameters and host options, from the cache and its package's files.</summary>
    internal static ProjectTemplateMember Member(CachedTemplate template, TemplatePackageFiles files, string group, IReadOnlySet<string> previewFrameworks, DateOnly today)
    {
        var host = TemplateHostFile.Parse(files.IdeHost);
        var definition = TemplateDefinition.Parse(files.Definition);
        var strings = Strings(files.Strings);
        var framework = FrameworkParameter(template);
        var parameters = new List<TemplateParameter>();
        var hostOptionSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "auth", "UseLocalDB" };
        if (host.DisableHttpsSymbol is { } https)
        {
            hostOptionSymbols.Add(https);
        }

        if (host.Exists)
        {
            var order = 0;
            foreach (var visible in host.Symbols.Where(s => s.IsVisible))
            {
                if (hostOptionSymbols.Contains(visible.Id) || string.Equals(visible.Id, "Framework", StringComparison.OrdinalIgnoreCase)
                    || Find(template, visible.Id) is not { } parameter || Parameter(template, parameter, visible, definition, strings, group, order++) is not { } shown)
                {
                    continue;
                }

                parameters.Add(shown);
            }
        }
        else
        {
            var order = 0;
            foreach (var parameter in template.Parameters)
            {
                var hidden = template.CliSymbols.TryGetValue(parameter.Name, out var cli) && cli.IsHidden;
                if (hidden || s_plumbing.Contains(parameter.Name) || parameter.Name.EndsWith("Port", StringComparison.OrdinalIgnoreCase)
                    || DataType(parameter.DataType) is null || Parameter(template, parameter, null, definition, strings, group, order++) is not { } shown)
                {
                    continue;
                }

                parameters.Add(shown);
            }
        }

        var auth = Find(template, "auth");
        var classes = template.Classifications;
        var isWeb = classes.Contains("Web", StringComparer.OrdinalIgnoreCase);
        return new ProjectTemplateMember
        {
            Identity = template.Identity,
            Precedence = template.Precedence,
            Frameworks = framework is null
                ? []
                : [.. framework.Choices.Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(m => new FrameworkChoice(m, DotnetSupport.FrameworkLabel(m, previewFrameworks.Contains(m), today), previewFrameworks.Contains(m)))
                    .OrderBy(f => f, FrameworkOrder.Instance)],
            DefaultFramework = framework?.DefaultValue,
            FrameworkOption = framework is null ? "--framework" : CliOption(template, framework.Name),
            Parameters = parameters,
            HostOptions = new MemberHostOptions
            {
                DisableHttpsSymbol = host.DisableHttpsSymbol is { } symbol && Find(template, symbol) is not null ? symbol : null,
                DisableHttpsOption = host.DisableHttpsSymbol is { } httpsSymbol && Find(template, httpsSymbol) is not null ? CliOption(template, httpsSymbol) : null,
                AuthChoices = auth is null ? null : SupportedAuth(auth.Choices.Select(c => c.Value)),
                AuthOption = auth is null ? null : CliOption(template, auth.Name),
                LocalDbOption = Find(template, "UseLocalDB") is { } localDb ? CliOption(template, localDb.Name) : null,
                Docker = host.SupportsDocker,
                // Aspire enlists a C# web app with a server: not a test or library, not Aspire's own, not standalone WebAssembly.
                Aspire = template.Language == "C#" && isWeb
                    && !classes.Any(c => c.Equals("Test", StringComparison.OrdinalIgnoreCase) || c.Equals("Library", StringComparison.OrdinalIgnoreCase) || c.Equals("Aspire", StringComparison.OrdinalIgnoreCase))
                    && !template.Identity.Contains("Aspire", StringComparison.OrdinalIgnoreCase)
                    && !template.ShortNames.Any(n => n.StartsWith("blazorwasm", StringComparison.OrdinalIgnoreCase)),
            },
            DefaultName = template.DefaultName ?? DefaultNameFor(template.ShortName, template.Name),
            SourceName = definition.SourceName,
            OpenFileRule = OpenFileRule.From(definition),
            LongestContentPath = files.LongestContentPath,
            SkipRestoreOption = Find(template, "skipRestore") is { } skip ? CliOption(template, skip.Name) : null,
            ComputedSymbols = definition.ComputedSymbols,
            MountPoint = template.MountPointUri,
            ConfigPlace = template.ConfigPlace,
        };
    }

    /// <summary>A parameter as the page shows it, or null when its type isn't one the page shows.</summary>
    private static TemplateParameter? Parameter(CachedTemplate template, CachedParameter parameter, HostSymbol? symbol, TemplateDefinition definition, Dictionary<string, string> strings, string group, int order)
    {
        if (DataType(parameter.DataType) is not { } type)
        {
            return null;
        }

        var invert = symbol?.InvertBoolean == true && type == TemplateDataType.Bool;
        var cliOption = CliOption(template, parameter.Name);
        var inverseCli = false;
        if (s_inverseCliSymbols.TryGetValue(parameter.Name, out var other) && Find(template, other) is { } otherParameter)
        {
            cliOption = CliOption(template, otherParameter.Name);
            inverseCli = true;
        }

        var key = $"symbols/{parameter.Name}/";
        var choices = parameter.Choices.Select(c => c with
        {
            Label = strings.GetValueOrDefault($"{key}choices/{c.Value}/displayName") is { Length: > 0 } label ? label : c.Label,
            Description = strings.GetValueOrDefault($"{key}choices/{c.Value}/description") ?? c.Description,
        }).ToList();
        var templateDefault = parameter.DefaultValue;
        var displayDefault = symbol?.DefaultValue ?? (invert ? Negate(templateDefault) : templateDefault);
        return new TemplateParameter
        {
            Name = parameter.Name,
            CliOption = cliOption,
            InverseCliValue = inverseCli,
            DataType = type,
            DefaultValue = templateDefault,
            DisplayDefault = displayDefault,
            Label = symbol?.Name ?? strings.GetValueOrDefault(key + "displayName") ?? parameter.DisplayName ?? parameter.Name,
            Description = symbol?.Description ?? strings.GetValueOrDefault(key + "description") ?? parameter.Description,
            Choices = choices,
            EnabledCondition = parameter.EnabledCondition ?? definition.EnabledConditions.GetValueOrDefault(parameter.Name),
            Invert = invert,
            PersistenceKey = symbol is null ? null : TemplateHostFile.PersistenceKey(symbol, group),
            Order = order,
        };
    }

    private static string? Negate(string? value) => bool.TryParse(value, out var parsed) ? (!parsed ? "true" : "false") : value is null ? "true" : value;

    private static TemplateDataType? DataType(string dataType) => dataType.Trim().ToLowerInvariant() switch
    {
        "bool" or "boolean" => TemplateDataType.Bool,
        "choice" => TemplateDataType.Choice,
        "text" or "string" => TemplateDataType.Text,
        "int" or "integer" => TemplateDataType.Integer,
        "float" or "double" => TemplateDataType.Float,
        _ => null,
    };

    private static CachedParameter? Find(CachedTemplate template, string name) =>
        template.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static CachedParameter? FrameworkParameter(CachedTemplate template) =>
        Find(template, "Framework") is { DataType: var type } framework && type.Equals("choice", StringComparison.OrdinalIgnoreCase) ? framework : null;

    /// <summary>The CLI option of a symbol: its long name in the CLI host file, else <c>--&lt;symbol&gt;</c> (the CLI takes hidden symbols that way).</summary>
    private static string CliOption(CachedTemplate template, string symbol) =>
        "--" + (template.CliSymbols.TryGetValue(symbol, out var cli) && cli.LongName is { } longName ? longName : symbol);

    /// <summary>The authentication choices the classic IDE offers, in its order: None, Individual Accounts, Microsoft identity platform, Windows.</summary>
    private static IReadOnlyList<string>? SupportedAuth(IEnumerable<string> choices)
    {
        var values = choices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> supported = [.. TemplateHostOptions.AuthOrder.Where(values.Contains)];
        return supported.Count > 1 ? supported : null;
    }

    /// <summary>
    /// The frameworks that are previews: the newest .NET a template offers, when only prerelease template packages
    /// (<c>…-rc.2…</c>, <c>…-preview…</c>) offer it. Older frameworks a prerelease package also offers aren't (WPF's
    /// package for .NET 11 offers .NET 6.0 to 11.0).
    /// </summary>
    internal static IReadOnlySet<string> Previews(IEnumerable<CachedTemplate> templates)
    {
        var offers = templates.SelectMany(t => (FrameworkParameter(t)?.Choices ?? []).Select(c => (Moniker: c.Value, Released: !IsPrerelease(t.MountPointUri)))).ToList();
        var dotnet = offers.Where(o => FrameworkOrder.Key(o.Moniker).Kind == 0).ToList();
        if (dotnet.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var newest = dotnet.Max(o => FrameworkOrder.Key(o.Moniker).Version)!;
        var released = offers.Where(o => o.Released).Select(o => o.Moniker).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return dotnet.Where(o => FrameworkOrder.Key(o.Moniker).Version == newest && !released.Contains(o.Moniker)).Select(o => o.Moniker).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPrerelease(string? mountPoint) =>
        mountPoint is not null && s_prereleaseMarkers.Any(m => Path.GetFileName(mountPoint).Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>A template's localized strings (<c>templatestrings.&lt;culture&gt;.json</c>): key → text.</summary>
    private static Dictionary<string, string> Strings(string? text)
    {
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var document = TemplateJson.Parse(text);
        if (document?.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (TemplateJson.AsText(property.Value) is { Length: > 0 } value)
                {
                    strings[property.Name] = value;
                }
            }
        }

        return strings;
    }

    /// <summary>
    /// Whether a template's projects run only on Windows: an <c>os</c> constraint naming only Windows, IDE host platform
    /// tags that add only Windows, a WinForms, WPF or WinUI classification, or a <c>wpf</c>/<c>winforms</c> short name.
    /// </summary>
    internal static bool RunsOnlyOnWindows(IReadOnlyList<string> operatingSystems, IReadOnlyList<HostTags> hostTags, IReadOnlyList<string> classifications, string shortName)
    {
        if (operatingSystems.Count > 0)
        {
            return operatingSystems.All(os => os.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        }

        var platformAdds = hostTags.Where(t => t.Type.Equals("platform", StringComparison.OrdinalIgnoreCase)).SelectMany(t => t.Add).ToList();
        if (platformAdds.Count > 0)
        {
            return platformAdds.All(p => p.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        }

        return classifications.Any(c => c.Equals("WinForms", StringComparison.OrdinalIgnoreCase) || c.Equals("WPF", StringComparison.OrdinalIgnoreCase) || c.Equals("WinUI", StringComparison.OrdinalIgnoreCase))
            || shortName.StartsWith("wpf", StringComparison.OrdinalIgnoreCase)
            || shortName.StartsWith("winforms", StringComparison.OrdinalIgnoreCase)
            || NetFrameworkTemplates.Is(shortName);
    }

    /// <summary>
    /// The rows from <c>dotnet new list --type project --columns-all</c> alone (no cache): one per short name and
    /// language, with one member whose parameters come from its help when it is chosen.
    /// </summary>
    internal static IReadOnlyList<ProjectTemplateGroup> FromList(string output)
    {
        var groups = new List<ProjectTemplateGroup>();
        foreach (var row in Table(output))
        {
            var shortName = row.GetValueOrDefault("Short Name", string.Empty).Split(',')[0].Trim();
            if (shortName.Length == 0)
            {
                continue;
            }

            var knownIndex = Array.FindIndex(s_known, k => k.ShortName == shortName);
            var name = knownIndex >= 0 ? s_known[knownIndex].Name : row.GetValueOrDefault("Template Name", shortName).TrimEnd('.');
            var classifications = row.GetValueOrDefault("Tags", string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var languages = row.GetValueOrDefault("Language", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Trim('[', ']')).ToList();
            foreach (var language in languages.DefaultIfEmpty(string.Empty))
            {
                var windowsOnly = RunsOnlyOnWindows([], [], classifications, shortName);
                var platforms = TemplateTags.PlatformsOf(classifications, [], windowsOnly);
                var types = TemplateTags.ProjectTypesOf(classifications, []);
                groups.Add(new ProjectTemplateGroup
                {
                    Id = $"dotnet:{shortName}|{language}",
                    Kind = ProjectTemplateKind.DotnetNew,
                    Name = name,
                    ShortName = shortName,
                    Language = language,
                    Classifications = classifications,
                    Platforms = platforms,
                    ProjectTypes = types,
                    Chips = TemplateTags.Chips(language, platforms, types),
                    IconKind = TemplateTags.IconKind(types, classifications),
                    RunsOnlyOnWindows = windowsOnly,
                    Order = knownIndex >= 0 ? knownIndex : int.MaxValue - 1,
                    Members = [new ProjectTemplateMember { Identity = shortName, DefaultName = DefaultNameFor(shortName, name), NeedsHelp = true }],
                });
            }
        }

        return groups;
    }

    /// <summary>A <c>dotnet new list</c> table as rows of header → cell (columns from the dashed rule, headers from the line above it).</summary>
    internal static IReadOnlyList<Dictionary<string, string>> Table(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rule = Array.FindIndex(lines, l => l.Trim().Length > 0 && l.Trim().All(c => c is '-' or ' ') && l.Contains("--", StringComparison.Ordinal));
        if (rule < 1)
        {
            return [];
        }

        var columns = new List<int>();
        for (var i = 0; i < lines[rule].Length; i++)
        {
            if (lines[rule][i] == '-' && (i == 0 || lines[rule][i - 1] == ' '))
            {
                columns.Add(i);
            }
        }

        string Cell(string line, int column)
        {
            var start = columns[column];
            var end = column + 1 < columns.Count ? columns[column + 1] : line.Length;
            return start >= line.Length ? string.Empty : line[start..Math.Min(end, line.Length)].Trim();
        }

        var headers = Enumerable.Range(0, columns.Count).Select(c => Cell(lines[rule - 1], c)).ToList();
        return [.. lines.Skip(rule + 1).Where(l => l.Trim().Length > 0)
            .Select(l => Enumerable.Range(0, columns.Count).ToDictionary(c => headers[c], c => Cell(l, c), StringComparer.OrdinalIgnoreCase))];
    }

    /// <summary>The .NET Framework templates, Blank Solution, and the known packs whose templates aren't installed.</summary>
    internal static IEnumerable<ProjectTemplateGroup> BuiltInRows(IEnumerable<string> shortNames, DateOnly today)
    {
        var order = s_known.Length;
        foreach (var (shortName, name, description, defaultName) in NetFrameworkTemplates.All)
        {
            var console = shortName == NetFrameworkTemplates.ConsoleShortName;
            IReadOnlyList<string> types = [console ? "Console" : "Library"];
            yield return new ProjectTemplateGroup
            {
                Id = "netfx:" + shortName,
                Kind = ProjectTemplateKind.NetFramework,
                Name = name,
                Description = description,
                ShortName = shortName,
                Language = "C#",
                Classifications = ["Common", console ? "Console" : "Library"],
                Platforms = ["Windows"],
                ProjectTypes = types,
                Chips = TemplateTags.Chips("C#", ["Windows"], types),
                IconKind = console ? "template-console" : "template-library",
                RunsOnlyOnWindows = true,
                Order = order++,
                Members =
                [
                    new ProjectTemplateMember
                    {
                        Identity = "Codalus.NetFramework." + shortName,
                        Frameworks = [.. NetFrameworkTemplates.Frameworks.Select(f => new FrameworkChoice(f, DotnetSupport.FrameworkLabel(f, false, today), false)).OrderBy(f => f, FrameworkOrder.Instance)],
                        DefaultFramework = NetFrameworkTemplates.Frameworks[0],
                        DefaultName = defaultName,
                    },
                ],
            };
        }

        var names = shortNames.ToList();
        foreach (var pack in TemplatePacks.Missing(names))
        {
            yield return new ProjectTemplateGroup
            {
                Id = "pack:" + pack.Package,
                Kind = ProjectTemplateKind.Pack,
                Name = TemplatePacks.NameOf(pack),
                Description = TemplatePacks.DescriptionOf(pack),
                Package = pack.Package,
                Platforms = TemplateTags.Platforms,
                ProjectTypes = ["Other"],
                Chips = [Localizer.Get("msg.newProject.notInstalled", "Not installed")],
                IconKind = "template-install",
                Order = int.MaxValue - 2,
            };
        }

        yield return new ProjectTemplateGroup
        {
            Id = BlankSolutionId,
            Kind = ProjectTemplateKind.BlankSolution,
            Name = Localizer.Get("templates.blankSolution", "Blank Solution"),
            Description = Localizer.Get("templates.blankSolution.description", "An empty solution with no projects, to add projects to later."),
            ShortName = "sln",
            Platforms = TemplateTags.Platforms,
            ProjectTypes = ["Other"],
            Chips = [TemplateTags.ProjectTypeLabel("Other")],
            IconKind = "template-solution",
            Order = int.MaxValue - 1,
            Members = [new ProjectTemplateMember { Identity = "Codalus.BlankSolution", DefaultName = "Solution1" }],
        };
    }

    /// <summary>The id of the Blank Solution row (New Solution opens the wizard on it).</summary>
    public const string BlankSolutionId = "builtin:blank-solution";

    /// <summary>Known templates first, then the IDE order, then by name; C# before F# before Visual Basic.</summary>
    private static IReadOnlyList<ProjectTemplateGroup> Sorted(IEnumerable<ProjectTemplateGroup> groups) =>
        [.. groups
            .OrderBy(g => g.Order)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Language switch { "C#" => 0, "F#" => 1, "VB" => 2, _ => 3 })];

    /// <summary>The name New Project suggests for a template without one in the cache.</summary>
    internal static string DefaultNameFor(string shortName, string name) => shortName switch
    {
        "console" or NetFrameworkTemplates.ConsoleShortName => "ConsoleApp1",
        "classlib" or NetFrameworkTemplates.LibraryShortName => "ClassLibrary1",
        "xunit" or "nunit" or "mstest" => "TestProject1",
        "web" or "webapi" or "mvc" or "webapp" or "blazor" or "grpc" => "WebApplication1",
        "worker" => "WorkerService1",
        "wpf" => "WpfApp1",
        "wpflib" => "WpfLibrary1",
        "wpfusercontrollib" or "wpfcustomcontrollib" => "WpfControlLibrary1",
        "winforms" => "WinFormsApp1",
        "winformslib" => "WinFormsLibrary1",
        "winformscontrollib" => "WinFormsControlLibrary1",
        "corewcf" => "CoreWCFService1",
        _ => string.Concat(name.Where(char.IsLetterOrDigit)) is { Length: > 0 } letters && char.IsLetter(letters[0]) ? letters + "1" : "Project1",
    };

    /// <summary>The readable name of a known short name (tests).</summary>
    internal static string? KnownName(string shortName) => s_known.FirstOrDefault(k => k.ShortName == shortName).Name;
}
