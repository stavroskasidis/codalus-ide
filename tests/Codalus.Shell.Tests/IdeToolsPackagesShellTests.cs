using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Shell.Tests;

/// <summary>
/// M60's package and migration tools for agents in the shell. Packages on a copy of samples/Packages and its local feed
/// (offline: nuget.org is never searched), through the NuGet Package Manager's one path; migrations on samples/Data
/// through the Entity Framework window's flow and the fixture's local dotnet-ef. Project files excluded from AI are left
/// out and can't be changed, and a licence is never accepted for the user.
/// </summary>
public sealed class IdeToolsPackagesShellTests
{
    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    private static async Task<string> OkAsync(ShellFixture f, string tool, string json = "{}")
    {
        var result = await CallAsync(f, tool, json);
        Assert.False(result.IsError, $"{tool}: {result.Text}");
        return result.Text;
    }

    private static async Task<string> ErrorAsync(ShellFixture f, string tool, string json)
    {
        var result = await CallAsync(f, tool, json);
        Assert.True(result.IsError, $"{tool} should fail: {result.Text}");
        return result.Text;
    }

    /// <summary>The file's text, or empty while the build host is writing it.</summary>
    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Asks list_packages until its answer has <paramref name="text"/> (the edit's restore and evaluation ran).</summary>
    private static async Task<string> ListedAsync(ShellFixture f, string json, string text, bool present = true)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var listed = await OkAsync(f, "list_packages", json);
            if (listed.Contains(text, StringComparison.Ordinal) == present)
            {
                return listed;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), $"list_packages {(present ? "never had" : "still has")} '{text}':\n{listed}");
            ShellFixture.Pump();
            await Task.Delay(200);
        }
    }

    [AvaloniaFact]
    public async Task Agents_list_search_add_update_and_remove_packages_through_the_package_manager()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Packages");
        File.WriteAllText(Path.Combine(folder, ".codalusignore"), "src/Multi/Multi.csproj\n");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Packages.slnx"));
        await ShellFixture.PumpUntil(
            () => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }
                  && f.Shell.Solutions.RestoreState == "succeeded" && f.Shell.Solutions.Projects.Values.Count(p => p.Restored) >= 3,
            seconds: 180);
        f.Shell.Packages.Offline = true; // only the fixture's local feed: tests never reach nuget.org
        var changes = 0;
        f.Shell.Packages.PackagesChanged += (_, _) => Interlocked.Increment(ref changes);
        var plain = Path.Combine(folder, "src", "Plain", "Plain.csproj");
        var props = Path.Combine(folder, "cpm", "Directory.Packages.props");

        // Every project's packages but the excluded one's; where versions come from; updates from the feed.
        var listed = (await OkAsync(f, "list_packages")).ReplaceLineEndings("\n");
        Assert.Contains("Plain (src/Plain/Plain.csproj), net10.0:\n  Codalus.Sample.Greeting 1.0.0\n", listed, StringComparison.Ordinal);
        Assert.Contains("Central (cpm/Central/Central.csproj), net10.0, versions managed centrally in cpm/Directory.Packages.props:\n  Codalus.Sample.Greeting 1.0.0, its version set centrally\n", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("Multi", listed, StringComparison.Ordinal);
        Assert.Contains("1 project(s) excluded from AI aren't shown.", listed, StringComparison.Ordinal);
        Assert.Contains("Codalus.Sample.Greeting 1.0.0; update available: 1.1.0", await OkAsync(f, "list_packages", """{"project":"Plain","updates":true}"""), StringComparison.Ordinal);
        Assert.Contains("is excluded from AI", await ErrorAsync(f, "list_packages", """{"project":"Multi"}"""), StringComparison.Ordinal);

        // Search the solution's sources, as Browse does.
        var found = await OkAsync(f, "search_packages", """{"query":"Codalus.Sample"}""");
        Assert.Contains("Codalus.Sample.Greeting 1.1.0", found, StringComparison.Ordinal);
        Assert.Contains("Codalus.Sample.Text 1.0.0", found, StringComparison.Ordinal);
        Assert.DoesNotContain("2.0.0-beta.1", found, StringComparison.Ordinal);
        Assert.Contains("Codalus.Sample.Greeting 2.0.0-beta.1", await OkAsync(f, "search_packages", """{"query":"Codalus.Sample.Greeting","prerelease":true}"""), StringComparison.Ordinal);

        // Add: the newest stable version that fits; the project file changes and the package manager hears of it.
        var added = await OkAsync(f, "add_package", """{"project":"Plain","id":"Codalus.Sample.Text"}""");
        Assert.Equal("Added Codalus.Sample.Text 1.0.0 to Plain. Changed: src/Plain/Plain.csproj. A restore runs now.", added);
        Assert.Contains("Include=\"Codalus.Sample.Text\" Version=\"1.0.0\"", Read(plain), StringComparison.Ordinal);
        Assert.True(Volatile.Read(ref changes) > 0);
        await ListedAsync(f, """{"project":"Plain"}""", "Codalus.Sample.Text 1.0.0");
        Assert.Contains("already references Codalus.Sample.Text 1.0.0", await OkAsync(f, "add_package", """{"project":"Plain","id":"Codalus.Sample.Text"}"""), StringComparison.Ordinal);

        // A licence is the user's to accept; an excluded project file isn't changed.
        Assert.Contains("requires its licence", await ErrorAsync(f, "add_package", """{"project":"Plain","id":"Codalus.Sample.Licensed"}"""), StringComparison.Ordinal);
        Assert.DoesNotContain("Licensed", Read(plain), StringComparison.Ordinal);
        Assert.Equal("'src/Multi/Multi.csproj' is excluded from AI: the tools can't change it.", await ErrorAsync(f, "add_package", """{"project":"Multi","id":"Codalus.Sample.Text"}"""));
        Assert.DoesNotContain("Codalus.Sample", Read(Path.Combine(folder, "src", "Multi", "Multi.csproj")), StringComparison.Ordinal);

        // Update everywhere it's referenced: Plain's project file and the central version both move.
        var updated = await OkAsync(f, "update_package", """{"id":"Codalus.Sample.Greeting"}""");
        Assert.StartsWith("Updated Codalus.Sample.Greeting to 1.1.0 in Central, Other, Plain.", updated, StringComparison.Ordinal);
        Assert.Contains("Include=\"Codalus.Sample.Greeting\" Version=\"1.1.0\"", Read(plain), StringComparison.Ordinal);
        Assert.Contains("Include=\"Codalus.Sample.Greeting\" Version=\"1.1.0\"", Read(props), StringComparison.Ordinal);
        await ListedAsync(f, """{"project":"Plain"}""", "Codalus.Sample.Greeting 1.1.0");
        Assert.Equal("Codalus.Sample.Greeting is already at 1.1.0.", await OkAsync(f, "update_package", """{"project":"Plain","id":"Codalus.Sample.Greeting"}"""));
        Assert.Contains("doesn't reference Codalus.Sample.Licensed directly", await ErrorAsync(f, "update_package", """{"project":"Plain","id":"Codalus.Sample.Licensed"}"""), StringComparison.Ordinal);

        // Remove.
        Assert.StartsWith("Removed Codalus.Sample.Text from Plain.", await OkAsync(f, "remove_package", """{"project":"Plain","id":"Codalus.Sample.Text"}"""), StringComparison.Ordinal);
        Assert.DoesNotContain("Codalus.Sample.Text", Read(plain), StringComparison.Ordinal);
        await ListedAsync(f, """{"project":"Plain"}""", "Codalus.Sample.Text 1.0.0", present: false);
        Assert.Contains("doesn't reference Codalus.Sample.Text", await ErrorAsync(f, "remove_package", """{"project":"Plain","id":"Codalus.Sample.Text"}"""), StringComparison.Ordinal);

        Assert.Equal(["list_packages", "search_packages"], IdePackageTools.Tools.Where(t => t.Kind == IdeToolKind.Read).Select(t => t.Name));
        // Listing and scripting migrations build the startup project and run dotnet ef, the workspace's code: actions (D52).
        Assert.Equal(["add_package", "update_package", "remove_package", "list_migrations", "add_migration", "apply_migrations", "script_migrations"], IdePackageTools.Tools.Where(t => t.Kind == IdeToolKind.Action).Select(t => t.Name));
    }

    [AvaloniaFact]
    public async Task Agents_list_script_add_and_apply_migrations_through_the_users_dotnet_ef()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Data");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Data.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var restore = await f.Shell.Packages.RunToolCommandAsync(new ToolCommandParams(ToolActions.Restore), CancellationToken.None);
        Assert.True(restore.Succeeded, restore.Output);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.RestoreState == "succeeded"
            && f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 180);
        var database = Path.Combine(folder, "Blogging", "blogging.db");

        // The migrations and whether each is applied; the SQL without applying it.
        var migrations = await OkAsync(f, "list_migrations");
        Assert.Matches(@"Blogging\.Data\.BloggingContext \(Blogging\):\s+InitialCreate \(\d+_InitialCreate\): pending", migrations);
        Assert.Contains("CREATE TABLE \"Blogs\"", await OkAsync(f, "script_migrations"), StringComparison.Ordinal);
        Assert.False(File.Exists(database));
        Assert.Contains("No DbContext named 'Nope'", await ErrorAsync(f, "list_migrations", """{"context":"Nope"}"""), StringComparison.Ordinal);

        // Apply: the SQL that ran comes back, and the database is there.
        var applied = await OkAsync(f, "apply_migrations");
        Assert.StartsWith("Applied InitialCreate. The database is at InitialCreate.", applied, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"Blogs\"", applied, StringComparison.Ordinal);
        Assert.True(File.Exists(database));
        Assert.Contains("InitialCreate", await OkAsync(f, "list_migrations", """{"context":"BloggingContext","project":"Blogging"}"""), StringComparison.Ordinal);
        Assert.Contains(": applied", await OkAsync(f, "list_migrations"), StringComparison.Ordinal);
        Assert.Equal("The database is already at InitialCreate.", await OkAsync(f, "apply_migrations"));

        // A model change and add_migration: its files, listed as pending, the Entity Framework window's flow.
        var contextFile = Path.Combine(folder, "Blogging", "Data", "BloggingContext.cs");
        File.WriteAllText(contextFile, File.ReadAllText(contextFile).Replace("public DateTime Published { get; set; }", "public DateTime Published { get; set; }\n\n    public int Rating { get; set; }", StringComparison.Ordinal));
        f.Shell.EntityFramework.Invalidate();
        Assert.Contains("C# identifier", await ErrorAsync(f, "add_migration", """{"name":"1Rating"}"""), StringComparison.Ordinal);
        var added = await OkAsync(f, "add_migration", """{"name":"AddRating"}""");
        Assert.Matches(@"^Added the migration AddRating to BloggingContext: Blogging/Migrations/\d+_AddRating\.cs", added);
        Assert.Contains("It isn't applied yet", added, StringComparison.Ordinal);
        Assert.Matches(@"AddRating \(\d+_AddRating\): pending", await OkAsync(f, "list_migrations"));
        Assert.Contains("ALTER TABLE \"Posts\" ADD \"Rating\"", await OkAsync(f, "script_migrations", """{"from":"InitialCreate"}"""), StringComparison.Ordinal);

        // Apply it; going back needs allowRevert, which the user asks for.
        Assert.StartsWith("Applied AddRating. The database is at AddRating.", await OkAsync(f, "apply_migrations"), StringComparison.Ordinal);
        Assert.Contains("Pass allowRevert only when the user asked for it", await ErrorAsync(f, "apply_migrations", """{"migration":"InitialCreate"}"""), StringComparison.Ordinal);
        Assert.Contains(": applied", (await OkAsync(f, "list_migrations")).Split('\n').Single(l => l.Contains("AddRating", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.StartsWith("Reverted AddRating. The database is at InitialCreate.", await OkAsync(f, "apply_migrations", """{"migration":"InitialCreate","allowRevert":true}"""), StringComparison.Ordinal);
        Assert.Contains("has no migration 'Nope'", await ErrorAsync(f, "apply_migrations", """{"migration":"Nope"}"""), StringComparison.Ordinal);

        // An excluded project: its migrations aren't read and none is added.
        File.WriteAllText(Path.Combine(folder, ".codalusignore"), "Blogging/Blogging.csproj\n");
        Assert.Equal("'Blogging/Blogging.csproj' is excluded from AI.", await ErrorAsync(f, "list_migrations", "{}"));
        Assert.Equal("'Blogging/Blogging.csproj' is excluded from AI: the tools can't change it.", await ErrorAsync(f, "add_migration", """{"name":"Other"}"""));
    }
}
