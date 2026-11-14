using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Data;
using Codalus.Workbench.Packages;
using SolutionPaths = Codalus.Workbench.Projects.SolutionService;

namespace Codalus.Shell;

/// <summary>
/// M60's tools for agents: the shell's side of <see cref="IdePackageTools"/>. NuGet packages go through
/// <see cref="PackageService"/> (the build host, the solution's sources, mappings and credentials), the one path the NuGet
/// Package Manager takes, so its windows and the restore follow; migrations through <see cref="EntityFrameworkService"/>
/// (the startup project built, then the user's <c>dotnet ef</c>), as the Entity Framework window does, and that window
/// reloads when they change. A package whose licence must be accepted is left to the user, and project files excluded
/// from AI are left out and can't be changed.
/// </summary>
public sealed partial class ShellHost
{
    private const int PackageSearchDefaultLimit = 20;

    /// <summary>The most SQL apply_migrations returns (script_migrations returns up to the dispatcher's limit).</summary>
    private const int EfAppliedSqlLength = 12_000;

    private async Task<IdeToolResult> PackageToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if (root is null || Solutions.Solution is not { } solution)
        {
            throw new IdeToolException("No solution is open.");
        }

        if (!solution.Trusted)
        {
            throw new IdeToolException("Packages and migrations need a trusted solution (MSBuild and dotnet ef run only there): the user trusts its folder first.");
        }

        try
        {
            return tool switch
            {
                "list_packages" => new(await ListPackagesTextAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "search_packages" => new(await SearchPackagesTextAsync(args, cancellationToken).ConfigureAwait(true)),
                "add_package" => new(await AddPackageAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "update_package" => new(await UpdatePackageAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "remove_package" => new(await RemovePackageAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "list_migrations" => new(await ListMigrationsTextAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "add_migration" => new(await AddMigrationToolAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "apply_migrations" => new(await ApplyMigrationsToolAsync(args, root, cancellationToken).ConfigureAwait(true)),
                "script_migrations" => new(await ScriptMigrationsToolAsync(args, root, cancellationToken).ConfigureAwait(true)),
                _ => throw new McpException($"There is no tool '{tool}'."),
            };
        }
        catch (EfBuildFailedException ex)
        {
            return IdeToolResult.Error(ex.Message + " get_diagnostics lists the errors.");
        }
        catch (Codalus.Rpc.JsonRpcConnectionLostException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return IdeToolResult.Error("The build host stopped: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ NuGet packages

    /// <summary>A project named by the agent, whose file the tools may read (or, with <paramref name="change"/>, change).</summary>
    private async Task<string> PackageProjectAsync(string name, string root, bool change, CancellationToken cancellationToken)
    {
        var project = ProjectPath(name);
        await PackageEnsureAllowedAsync(project, root, change, cancellationToken).ConfigureAwait(true);
        return project;
    }

    private async Task PackageEnsureAllowedAsync(string file, string root, bool change, CancellationToken cancellationToken)
    {
        if (await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
        {
            throw new IdeToolException($"'{Relative(file, root)}' is excluded from AI{(change ? ": the tools can't change it." : ".")}");
        }
    }

    private static string? PackageId(JsonElement args, bool required) => Str(args, "id")?.Trim() switch
    {
        { Length: > 0 } id when id.StartsWith('-') || id.Any(char.IsWhiteSpace) => throw new IdeToolException($"'{id}' isn't a package id."),
        { Length: > 0 } id => id,
        _ => required ? throw new IdeToolException("A package id is needed.") : null,
    };

    private static string PackageKindNote(InstalledPackage package) => package.Kind switch
    {
        InstalledPackageKinds.Global => ", a GlobalPackageReference (Directory.Packages.props)",
        InstalledPackageKinds.Implicit => ", implicit (from the SDK)",
        _ when package.VersionOverride is not null => ", a VersionOverride in the project",
        _ when package.CentralVersion is not null => ", its version set centrally",
        _ => string.Empty,
    };

    private async Task<string> ListPackagesTextAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var project = Str(args, "project") is { Length: > 0 } name ? await PackageProjectAsync(name, root, change: false, cancellationToken).ConfigureAwait(true) : null;
        var projects = new List<ProjectPackages>();
        var hidden = 0;
        foreach (var packages in await Packages.InstalledAsync(project, cancellationToken).ConfigureAwait(true))
        {
            if (await IsExcludedAsync(packages.Project, root, cancellationToken).ConfigureAwait(true))
            {
                hidden++;
            }
            else
            {
                projects.Add(packages);
            }
        }

        var sb = new StringBuilder();
        var latest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (Bool(args, "updates") == true)
        {
            var queries = projects.SelectMany(p => p.Packages)
                .Where(p => p.Kind != InstalledPackageKinds.Implicit)
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PackageStatusQuery(g.Key, g.First().Resolved ?? g.First().Requested))
                .ToList();
            if (queries.Count > 0)
            {
                var status = await Packages.StatusAsync(queries, Bool(args, "prerelease") == true, cancellationToken).ConfigureAwait(true);
                foreach (var package in status.Packages)
                {
                    latest[package.Id] = package.Latest;
                }

                foreach (var error in status.Errors)
                {
                    sb.AppendLine(PackageSourceErrorText(error));
                }
            }
        }

        foreach (var packages in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(packages.Name).Append(" (").Append(Relative(packages.Project, root)).Append("), ").Append(string.Join(", ", packages.TargetFrameworks));
            if (packages.CentralPackageManagement)
            {
                sb.Append(", versions managed centrally").Append(packages.CentralPackagesFile is { } props ? " in " + Relative(props, root) : string.Empty);
            }

            if (!packages.Restored)
            {
                sb.Append(", not restored");
            }

            sb.AppendLine(":");
            if (packages.Packages.Length == 0)
            {
                sb.AppendLine("  (no packages)");
            }

            foreach (var package in packages.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                var version = package.Resolved ?? package.Requested ?? "?";
                sb.Append("  ").Append(package.Id).Append(' ').Append(version);
                if (package.Requested is { } requested && !string.Equals(requested, version, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(" (asked for ").Append(requested).Append(')');
                }

                sb.Append(PackageKindNote(package));
                if (package.Frameworks.Length > 0 && package.Frameworks.Length < packages.TargetFrameworks.Length)
                {
                    sb.Append(" [").Append(string.Join(", ", package.Frameworks)).Append(']');
                }

                if (latest.TryGetValue(package.Id, out var newest) && newest is not null && PackageManagerViewModel.Compare(newest, version) > 0)
                {
                    sb.Append("; update available: ").Append(newest);
                }

                sb.AppendLine();
            }

            if (Bool(args, "transitive") == true && packages.Transitive.Length > 0)
            {
                sb.AppendLine("  Transitive:");
                foreach (var package in packages.Transitive.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
                {
                    sb.Append("    ").Append(package.Id).Append(' ').AppendLine(package.Resolved);
                }
            }
        }

        if (projects.Count == 0 && hidden == 0)
        {
            sb.AppendLine("No projects.");
        }

        if (hidden > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{hidden} project(s) excluded from AI aren't shown."));
        }

        return sb.ToString();
    }

    private static string PackageSourceErrorText(PackageSourceError error) =>
        $"The source {error.Source} failed: {error.Message}" + (PackageService.NeedsSignIn(error.Message) ? " (it needs a sign-in: the user signs in from the NuGet Package Manager)." : "");

    private async Task<string> SearchPackagesTextAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var query = Str(args, "query")?.Trim() is { Length: > 0 } text ? text : throw new IdeToolException("A query is needed.");
        var limit = Math.Clamp(Int(args, "limit") ?? PackageSearchDefaultLimit, 1, 100);
        var result = await Packages.SearchAsync(query, Bool(args, "prerelease") == true, 0, limit, Str(args, "source") is { Length: > 0 } source ? source : null, cancellationToken).ConfigureAwait(true);
        var sb = new StringBuilder();
        foreach (var package in result.Packages)
        {
            sb.Append(package.Id).Append(' ').Append(package.Version);
            if (package.Description is { Length: > 0 } description)
            {
                var line = description.ReplaceLineEndings(" ").Trim();
                sb.Append(" — ").Append(line.Length > 200 ? line[..200] + "…" : line);
            }

            sb.Append(" (");
            if (package.Downloads is { } downloads)
            {
                sb.Append(downloads.ToString("N0", CultureInfo.InvariantCulture)).Append(" downloads, ");
            }

            sb.Append(package.Source).Append(')');
            if (package.Verified)
            {
                sb.Append(" [verified owner]");
            }

            if (package.Deprecated)
            {
                sb.Append(" [deprecated]");
            }

            if (package.VulnerabilitySeverity >= 0)
            {
                sb.Append(" [vulnerable: ").Append(package.VulnerabilitySeverity switch { 0 => "low", 1 => "moderate", 2 => "high", _ => "critical" }).Append(']');
            }

            sb.AppendLine();
        }

        if (result.Packages.Length == 0)
        {
            sb.AppendLine("No packages match.");
        }
        else if (result.HasMore)
        {
            sb.AppendLine("More packages match: narrow the query or raise the limit.");
        }

        foreach (var error in result.Errors)
        {
            sb.AppendLine(PackageSourceErrorText(error));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The version to install (the one given, or the newest stable one that supports <paramref name="frameworks"/>), after
    /// checking that the package doesn't ask for its licence to be accepted: only the user accepts a licence.
    /// </summary>
    private async Task<string> PackageVersionAsync(string id, string? version, IReadOnlyList<string> frameworks, CancellationToken cancellationToken)
    {
        var details = await Packages.DetailsAsync(id, prerelease: version?.Contains('-', StringComparison.Ordinal) == true, version, null, cancellationToken, frameworks).ConfigureAwait(true);
        if (details.RequireLicenseAcceptance)
        {
            throw new IdeToolException($"{details.Id} {details.Version} requires its licence ({details.LicenseExpression ?? details.LicenseUrl ?? "see the package"}) to be accepted, which only the user can do: ask them to install it from the NuGet Package Manager.");
        }

        return version ?? details.Version;
    }

    /// <summary>The central versions file to change, checked; and the other projects whose version follows it.</summary>
    private async Task<string> PackageCentralNoteAsync(ProjectPackages packages, IReadOnlyCollection<string> targets, string id, string version, bool versionOverride, string root, CancellationToken cancellationToken)
    {
        if (!packages.CentralPackageManagement || packages.CentralPackagesFile is not { } props)
        {
            return string.Empty;
        }

        await PackageEnsureAllowedAsync(props, root, change: true, cancellationToken).ConfigureAwait(true);
        if (versionOverride)
        {
            return string.Empty;
        }

        var followers = (await Packages.InstalledAsync(null, cancellationToken).ConfigureAwait(true))
            .Where(p => p.CentralPackagesFile is { } file && SolutionPaths.PathComparer.Equals(file, props) && !targets.Contains(p.Project, SolutionPaths.PathComparer))
            .Where(p => p.Packages.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && i.VersionOverride is null && i.CentralVersion is { } central && !central.Equals(version, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return followers.Count == 0
            ? string.Empty
            : $" The central version in {Relative(props, root)} changed, which {string.Join(", ", followers)} follow too.";
    }

    private async Task<ProjectPackages?> PackagesOfAsync(string project, CancellationToken cancellationToken)
    {
        var packages = await Packages.InstalledAsync(project, cancellationToken).ConfigureAwait(true);
        return packages.Count > 0 ? packages[0] : null;
    }

    private string PackageChangedText(IReadOnlyList<string> files, string root) =>
        files.Count == 0 ? " Nothing changed." : " Changed: " + string.Join(", ", files.Select(f => Relative(f, root))) + ". A restore runs now.";

    private async Task<string> AddPackageAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var project = await PackageProjectAsync(Str(args, "project") ?? throw new IdeToolException("A project is needed."), root, change: true, cancellationToken).ConfigureAwait(true);
        var id = PackageId(args, required: true)!;
        var packages = await PackagesOfAsync(project, cancellationToken).ConfigureAwait(true)
            ?? throw new IdeToolException($"The packages of {Path.GetFileNameWithoutExtension(project)} can't be read yet (it is loading).");
        if (packages.Packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            return existing.Kind == InstalledPackageKinds.Direct
                ? $"{packages.Name} already references {existing.Id} {existing.Resolved ?? existing.Requested}: update_package changes its version."
                : $"{packages.Name} already gets {existing.Id}{PackageKindNote(existing)}.";
        }

        var version = await PackageVersionAsync(id, Str(args, "version") is { Length: > 0 } v ? v.Trim() : null, packages.TargetFrameworks, cancellationToken).ConfigureAwait(true);
        var versionOverride = Bool(args, "versionOverride") == true;
        var central = await PackageCentralNoteAsync(packages, [project], id, version, versionOverride, root, cancellationToken).ConfigureAwait(true);
        var files = await Packages.InstallAsync([new PackageEditTarget(project)], id, version, versionOverride, cancellationToken).ConfigureAwait(true);
        return $"Added {id} {version} to {packages.Name}." + PackageChangedText(files, root) + central;
    }

    private async Task<string> UpdatePackageAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var id = PackageId(args, required: true)!;
        var project = Str(args, "project") is { Length: > 0 } name ? await PackageProjectAsync(name, root, change: true, cancellationToken).ConfigureAwait(true) : null;
        var all = await Packages.InstalledAsync(project, cancellationToken).ConfigureAwait(true);
        var targets = new List<ProjectPackages>();
        var hidden = 0;
        foreach (var packages in all.Where(p => p.Packages.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && i.Kind == InstalledPackageKinds.Direct)))
        {
            if (await IsExcludedAsync(packages.Project, root, cancellationToken).ConfigureAwait(true))
            {
                hidden++;
            }
            else
            {
                targets.Add(packages);
            }
        }

        if (targets.Count == 0)
        {
            throw new IdeToolException(project is null
                ? $"No project references {id} directly{(hidden > 0 ? " (but some excluded from AI)" : "")}: add_package adds it."
                : $"{Path.GetFileNameWithoutExtension(project)} doesn't reference {id} directly: add_package adds it.");
        }

        var frameworks = targets.SelectMany(t => t.TargetFrameworks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var version = await PackageVersionAsync(id, Str(args, "version") is { Length: > 0 } v ? v.Trim() : null, frameworks, cancellationToken).ConfigureAwait(true);
        targets.RemoveAll(t => t.Packages.First(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is var installed
            && string.Equals(installed.Resolved ?? installed.Requested, version, StringComparison.OrdinalIgnoreCase));
        if (targets.Count == 0)
        {
            return $"{id} is already at {version}.";
        }

        targets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var targetPaths = targets.Select(t => t.Project).ToList();
        var versionOverride = Bool(args, "versionOverride") == true;
        var central = new StringBuilder();
        foreach (var packages in targets)
        {
            var note = await PackageCentralNoteAsync(packages, targetPaths, id, version, versionOverride, root, cancellationToken).ConfigureAwait(true);
            if (note.Length > 0 && !central.ToString().Contains(note, StringComparison.Ordinal))
            {
                central.Append(note);
            }
        }

        var files = await Packages.InstallAsync([.. targets.Select(t => new PackageEditTarget(t.Project))], id, version, versionOverride, cancellationToken).ConfigureAwait(true);
        return $"Updated {id} to {version} in {string.Join(", ", targets.Select(t => t.Name))}." + PackageChangedText(files, root) + central
            + (hidden > 0 ? string.Create(CultureInfo.InvariantCulture, $" {hidden} project(s) excluded from AI weren't changed.") : "");
    }

    private async Task<string> RemovePackageAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var project = await PackageProjectAsync(Str(args, "project") ?? throw new IdeToolException("A project is needed."), root, change: true, cancellationToken).ConfigureAwait(true);
        var id = PackageId(args, required: true)!;
        var packages = await PackagesOfAsync(project, cancellationToken).ConfigureAwait(true);
        var installed = packages?.Packages.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new IdeToolException($"{Path.GetFileNameWithoutExtension(project)} doesn't reference {id}.");
        if (installed.Kind != InstalledPackageKinds.Direct)
        {
            throw new IdeToolException($"{Path.GetFileNameWithoutExtension(project)} gets {installed.Id}{PackageKindNote(installed)}: it isn't removed from the project.");
        }

        if (installed.DefinedIn is { } definedIn && !SolutionPaths.PathComparer.Equals(definedIn, project))
        {
            await PackageEnsureAllowedAsync(definedIn, root, change: true, cancellationToken).ConfigureAwait(true);
        }

        var files = await Packages.UninstallAsync([project], installed.Id, cancellationToken).ConfigureAwait(true);
        return $"Removed {installed.Id} from {packages!.Name}." + PackageChangedText(files, root);
    }

    // ------------------------------------------------------------------ EF Core migrations

    /// <summary>The DbContext a migration tool acts on: the one named (by project and name), or the only one.</summary>
    private async Task<(EfContextEntry Context, EfTarget Target)> EfToolTargetAsync(JsonElement args, string root, bool change, CancellationToken cancellationToken)
    {
        if (EntityFramework.Projects.Count == 0)
        {
            throw new IdeToolException("No project of the solution references EF Core.");
        }

        var project = Str(args, "project") is { Length: > 0 } name ? ProjectPath(name) : null;
        var wanted = Str(args, "context")?.Trim() is { Length: > 0 } context ? context : null;
        bool Matches(EfContextEntry entry) =>
            (project is null || SolutionPaths.PathComparer.Equals(entry.Project, project))
            && (wanted is null || entry.Name.Equals(wanted, StringComparison.Ordinal) || entry.FullName.Equals(wanted, StringComparison.Ordinal));

        // Roslyn's answer is instant; dotnet ef's (which builds) when Roslyn has none (still loading, or a context it can't see).
        var found = (await EntityFramework.ContextsAsync(fromTool: false, cancellationToken).ConfigureAwait(true)).Where(Matches).ToList();
        if (found.Count == 0)
        {
            found = [.. (await EntityFramework.ContextsAsync(fromTool: true, cancellationToken).ConfigureAwait(true)).Where(Matches)];
        }

        var entry = found.Count switch
        {
            0 => throw new IdeToolException($"No DbContext{(wanted is null ? "" : $" named '{wanted}'")}{(project is null ? "" : $" in {Path.GetFileNameWithoutExtension(project)}")} was found."),
            1 => found[0],
            _ => throw new IdeToolException($"There are several DbContexts: {string.Join(", ", found.Select(c => c.FullName + " (" + Path.GetFileNameWithoutExtension(c.Project) + ")"))}. Pass context (and project)."),
        };
        await PackageEnsureAllowedAsync(entry.Project, root, change, cancellationToken).ConfigureAwait(true);
        return (entry, EntityFramework.Target(entry));
    }

    private static string EfState(EfMigrationItem item) => item.Applied switch
    {
        true => "applied",
        false => "pending",
        null => "unknown",
    };

    private async Task<string> ListMigrationsTextAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var (context, target) = await EfToolTargetAsync(args, root, change: false, cancellationToken).ConfigureAwait(true);
        var result = await EntityFramework.MigrationsAsync(target, cancellationToken).ConfigureAwait(true);
        var sb = new StringBuilder().Append(context.FullName).Append(" (").Append(Path.GetFileNameWithoutExtension(context.Project)).AppendLine("):");
        if (result.Migrations.Length == 0)
        {
            sb.AppendLine("  No migrations yet: add_migration adds the first one.");
        }

        foreach (var migration in result.Migrations)
        {
            sb.Append("  ").Append(migration.Name).Append(" (").Append(migration.Id).Append("): ").AppendLine(EfState(migration));
        }

        if (result.ConnectionError is { } error)
        {
            sb.Append("The database couldn't be reached, so whether each migration is applied is unknown: ").AppendLine(error);
        }

        return sb.ToString();
    }

    private async Task<string> AddMigrationToolAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var name = Str(args, "name")?.Trim() ?? "";
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new IdeToolException("The migration's name is a C# identifier: letters, digits and underscores, starting with a letter.");
        }

        var (context, target) = await EfToolTargetAsync(args, root, change: true, cancellationToken).ConfigureAwait(true);
        var outputDir = Str(args, "outputDir")?.Trim() is { Length: > 0 } dir ? dir : null;
        var folder = WorkspacePath(Path.Combine(Path.GetDirectoryName(context.Project)!, (outputDir ?? "Migrations").Replace('/', Path.DirectorySeparatorChar)), root);
        await PackageEnsureAllowedAsync(Path.Combine(folder, name + ".cs"), root, change: true, cancellationToken).ConfigureAwait(true);
        var files = await EntityFramework.AddMigrationAsync(target, name, outputDir, cancellationToken).ConfigureAwait(true);
        return $"Added the migration {name} to {context.Name}: {string.Join(", ", files.Select(f => Relative(f, root)))}. It isn't applied yet: apply_migrations applies it.";
    }

    private async Task<string> ApplyMigrationsToolAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var (context, target) = await EfToolTargetAsync(args, root, change: false, cancellationToken).ConfigureAwait(true);
        var listed = await EntityFramework.MigrationsAsync(target, cancellationToken).ConfigureAwait(true);
        var items = listed.Migrations;
        if (items.Length == 0)
        {
            return $"{context.Name} has no migrations to apply.";
        }

        if (listed.ConnectionError is { } error && items.All(m => m.Applied is null))
        {
            throw new IdeToolException("The database can't be reached: " + error);
        }

        var migration = Str(args, "migration")?.Trim() is { Length: > 0 } wanted ? wanted : null;
        if (migration is not null && migration != "0" && !items.Any(m => m.Id == migration || m.Name == migration))
        {
            throw new IdeToolException($"{context.Name} has no migration '{migration}'; list_migrations lists them.");
        }

        var (from, to) = EntityFrameworkService.UpdateRange(items, migration);
        string Name(string id) => id == "0" ? "no migrations (the empty database)" : items.FirstOrDefault(m => m.Id == id)?.Name ?? id;
        if (from == to)
        {
            return $"The database is already at {Name(to)}.";
        }

        var revert = string.CompareOrdinal(to, from) < 0;
        var changed = revert
            ? items.Where(m => string.CompareOrdinal(m.Id, to) > 0 && string.CompareOrdinal(m.Id, from) <= 0).ToList()
            : items.Where(m => string.CompareOrdinal(m.Id, from) > 0 && string.CompareOrdinal(m.Id, to) <= 0).ToList();
        if (revert && Bool(args, "allowRevert") != true)
        {
            throw new IdeToolException($"Going back to {Name(to)} reverts {string.Join(", ", changed.Select(m => m.Name))}: their Down scripts may drop tables and data. Pass allowRevert only when the user asked for it.");
        }

        var sql = await EntityFramework.UpdateScriptAsync(target, items, migration, cancellationToken).ConfigureAwait(true);
        await EntityFramework.UpdateDatabaseAsync(target, migration ?? to, cancellationToken).ConfigureAwait(true);
        var sb = new StringBuilder()
            .Append(revert ? "Reverted " : "Applied ").Append(string.Join(", ", changed.Select(m => m.Name)))
            .Append(". The database is at ").Append(Name(to)).AppendLine(".")
            .AppendLine("The SQL that ran:")
            .AppendLine(sql.Length > EfAppliedSqlLength ? sql[..EfAppliedSqlLength] + "\n… (cut; script_migrations gives all of it)" : sql);
        return sb.ToString();
    }

    private async Task<string> ScriptMigrationsToolAsync(JsonElement args, string root, CancellationToken cancellationToken)
    {
        var (_, target) = await EfToolTargetAsync(args, root, change: false, cancellationToken).ConfigureAwait(true);
        static string? Point(JsonElement args, string name) => Str(args, name)?.Trim() switch
        {
            null or "" => null,
            ['-', ..] => throw new IdeToolException($"'{Str(args, name)}' isn't a migration."),
            var point => point,
        };

        var sql = await EntityFramework.ScriptAsync(target, Point(args, "from"), Point(args, "to"), Bool(args, "idempotent") == true, noTransactions: false, cancellationToken).ConfigureAwait(true);
        return sql.Trim().Length == 0 ? "The script is empty: there is nothing between those migrations." : sql;
    }
}
