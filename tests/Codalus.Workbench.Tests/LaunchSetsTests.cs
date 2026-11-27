using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Sets of startup projects (ADR 0110): the solution's launch files in the classic IDE's format (<c>.slnLaunch</c>,
/// <c>.slnxLaunch</c>, their <c>.user</c> files), and <see cref="RunService"/>'s active set and per-entry launch profiles.
/// </summary>
public sealed class LaunchSetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-launch-sets-" + Guid.NewGuid().ToString("N"));

    public LaunchSetsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Web"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Worker"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Web => Path.Combine(_root, "src", "Web", "Web.csproj");

    private string Worker => Path.Combine(_root, "src", "Worker", "Worker.csproj");

    private string Slnx => Path.Combine(_root, "Contoso.slnx");

    private string Sln => Path.Combine(_root, "Contoso.sln");

    // The classic IDE's file: backslashes, a debug target on one project, a property Codalus doesn't know.
    private const string Classic = """
        [
          {
            "Name": "Web + Worker",
            "Projects": [
              {
                "Path": "src\\Web\\Web.csproj",
                "Action": "Start",
                "DebugTarget": "https"
              },
              {
                "Path": "src\\Worker\\Worker.csproj",
                "Action": "StartWithoutDebugging",
                "Extra": 42
              }
            ]
          },
          {
            "Name": "Web only",
            "Projects": [
              {
                "Path": "src/Web/Web.csproj",
                "Action": "Start"
              }
            ]
          }
        ]
        """;

    [Fact]
    public void The_classic_format_reads_with_either_slash_and_each_entrys_debug_target()
    {
        File.WriteAllText(Slnx.Replace(".slnx", ".slnxLaunch", StringComparison.Ordinal), Classic);
        var read = SolutionLaunchFiles.Read(Slnx, SolutionFormats.Slnx);

        Assert.Empty(read.Problems);
        Assert.Equal(["Web + Worker", "Web only"], read.Sets.Select(s => s.Name));
        var both = read.Sets[0];
        Assert.True(both.Shared);
        Assert.Equal([new StartupEntry(Web, StartupAction.Start, "https"), new StartupEntry(Worker, StartupAction.StartWithoutDebugging)], both.Entries);
        Assert.Equal([new StartupEntry(Web, StartupAction.Start)], read.Sets[1].Entries);
    }

    [Fact]
    public void A_slnx_reads_its_slnxLaunch_first_then_a_slnLaunch_kept_from_before_and_the_user_files_last()
    {
        File.WriteAllText(Path.Combine(_root, "Contoso.slnxLaunch"), """[{ "Name": "A", "Projects": [] }]""");
        File.WriteAllText(Path.Combine(_root, "Contoso.slnLaunch"), """[{ "Name": "A", "Projects": [] }, { "Name": "B", "Projects": [] }]""");
        File.WriteAllText(Path.Combine(_root, "Contoso.slnLaunch.user"), """[{ "Name": "B", "Projects": [] }, { "Name": "Mine", "Projects": [] }]""");

        var read = SolutionLaunchFiles.Read(Slnx, SolutionFormats.Slnx);

        Assert.Equal([("A", "Contoso.slnxLaunch", true), ("B", "Contoso.slnLaunch", true), ("Mine", "Contoso.slnLaunch.user", false)],
            read.Sets.Select(s => (s.Name, Path.GetFileName(s.File)!, s.Shared)));
        Assert.Equal(2, read.Problems.Count); // the hidden A and B, logged

        // A .sln has only its .slnLaunch; a folder has none.
        Assert.Equal(["B"], SolutionLaunchFiles.Read(Sln, SolutionFormats.Sln).Sets.Where(s => s.Shared).Skip(1).Select(s => s.Name));
        Assert.Empty(SolutionLaunchFiles.Read(_root, SolutionFormats.Folder).Sets);
    }

    [Fact]
    public void A_file_that_isnt_a_list_of_profiles_is_a_problem_not_an_exception()
    {
        File.WriteAllText(Path.Combine(_root, "Contoso.slnLaunch"), """{ "Name": "not a list" }""");
        var read = SolutionLaunchFiles.Read(Sln, SolutionFormats.Sln);
        Assert.Empty(read.Sets);
        Assert.Contains("isn't a list of launch profiles", Assert.Single(read.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_a_set_keeps_the_other_sets_and_the_properties_Codalus_doesnt_know()
    {
        var file = Path.Combine(_root, "Contoso.slnxLaunch");
        File.WriteAllText(file, Classic.ReplaceLineEndings("\r\n"));
        var both = SolutionLaunchFiles.Read(Slnx, SolutionFormats.Slnx).Sets[0];

        // Worker's debug target changes; Web is renamed in nothing; the set is renamed.
        var changed = both.With("Both", [new StartupEntry(Web, StartupAction.Start, "https"), new StartupEntry(Worker, StartupAction.Start, "Worker")], shared: true);
        Assert.Equal(file, SolutionLaunchFiles.Save(Slnx, SolutionFormats.Slnx, changed, previousName: both.Name));

        var text = File.ReadAllText(file);
        Assert.Contains("\r\n", text, StringComparison.Ordinal); // its line endings are kept
        var read = SolutionLaunchFiles.Read(Slnx, SolutionFormats.Slnx).Sets;
        Assert.Equal(["Both", "Web only"], read.Select(s => s.Name));
        Assert.Equal([new StartupEntry(Web, StartupAction.Start, "https"), new StartupEntry(Worker, StartupAction.Start, "Worker")], read[0].Entries);
        Assert.Contains("\"Extra\": 42", text, StringComparison.Ordinal);
        Assert.Contains("\"Path\": \"src\\\\Worker\\\\Worker.csproj\"", text, StringComparison.Ordinal); // as the file wrote it
        Assert.Contains("\"Path\": \"src/Web/Web.csproj\"", text, StringComparison.Ordinal); // the other set, untouched
    }

    [Fact]
    public void A_new_set_goes_in_a_new_file_of_the_solutions_kind_with_relative_backslash_paths()
    {
        var set = new LaunchSet("New", [new StartupEntry(Worker, StartupAction.StartWithoutDebugging), new StartupEntry(Web, StartupAction.None)], shared: true);
        var file = SolutionLaunchFiles.Save(Slnx, SolutionFormats.Slnx, set);
        Assert.Equal(Path.Combine(_root, "Contoso.slnxLaunch"), file);
        var text = File.ReadAllText(file);
        Assert.Contains("\"Path\": \"src\\\\Worker\\\\Worker.csproj\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DebugTarget", text, StringComparison.Ordinal); // none chosen: none written
        Assert.DoesNotContain("Web.csproj", text, StringComparison.Ordinal); // None isn't written

        Assert.Equal(Path.Combine(_root, "Contoso.slnLaunch"), SolutionLaunchFiles.Save(Sln, SolutionFormats.Sln, set));
        Assert.Equal(Path.Combine(_root, "Contoso.slnLaunch.user"), SolutionLaunchFiles.Save(Sln, SolutionFormats.Sln, new LaunchSet("Mine", [], shared: false)));
    }

    [Fact]
    public void A_slnx_saves_a_new_set_in_the_slnLaunch_it_already_has()
    {
        File.WriteAllText(Path.Combine(_root, "Contoso.slnLaunch"), """[{ "Name": "Old", "Projects": [] }]""");
        var file = SolutionLaunchFiles.Save(Slnx, SolutionFormats.Slnx, new LaunchSet("New", [new StartupEntry(Web, StartupAction.Start)], shared: true));
        Assert.Equal(Path.Combine(_root, "Contoso.slnLaunch"), file);
        Assert.False(File.Exists(Path.Combine(_root, "Contoso.slnxLaunch")));
        Assert.Equal(["Old", "New"], SolutionLaunchFiles.Read(Slnx, SolutionFormats.Slnx).Sets.Select(s => s.Name));
    }

    [Fact]
    public void Unsharing_a_set_moves_it_to_the_user_file_and_an_emptied_file_goes()
    {
        var shared = Path.Combine(_root, "Contoso.slnLaunch");
        File.WriteAllText(shared, """[{ "Name": "Mine", "Projects": [{ "Path": "src\\Web\\Web.csproj", "Action": "Start" }] }]""");
        var set = SolutionLaunchFiles.Read(Sln, SolutionFormats.Sln).Sets.Single();

        var file = SolutionLaunchFiles.Save(Sln, SolutionFormats.Sln, set.With(set.Name, set.Entries, shared: false));

        Assert.Equal(shared + ".user", file);
        Assert.False(File.Exists(shared));
        var moved = SolutionLaunchFiles.Read(Sln, SolutionFormats.Sln).Sets.Single();
        Assert.False(moved.Shared);
        SolutionLaunchFiles.Delete(moved);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_project_with_an_action_Codalus_doesnt_know_is_None_and_kept_as_it_was()
    {
        var file = Path.Combine(_root, "Contoso.slnLaunch");
        File.WriteAllText(file, """[{ "Name": "S", "Projects": [{ "Path": "src\\Web\\Web.csproj", "Action": "StartWithProfiling" }, { "Path": "src\\Worker\\Worker.csproj", "Action": "Start" }] }]""");
        var set = SolutionLaunchFiles.Read(Sln, SolutionFormats.Sln).Sets.Single();
        Assert.Equal(StartupAction.None, set.Entries[0].Action);

        SolutionLaunchFiles.Save(Sln, SolutionFormats.Sln, set.With("S", [set.Entries[0], new StartupEntry(Worker, StartupAction.StartWithoutDebugging)], shared: true));
        var text = File.ReadAllText(file);
        Assert.Contains("\"Action\": \"StartWithProfiling\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Action\": \"StartWithoutDebugging\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_entry_starts_with_its_own_profile_and_a_single_project_with_the_chosen_one()
    {
        LaunchProfile[] profiles = [new("http", "Project"), new("https", "Project"), new("IIS", "IISExpress")];
        Assert.Equal("https", RunService.ResolveProfile(new StartupEntry(Web, StartupAction.Start, "https"), profiles)?.Name);
        Assert.Equal("http", RunService.ResolveProfile(new StartupEntry(Web, StartupAction.Start), profiles)?.Name); // no debug target: the first runnable
        Assert.Equal("http", RunService.ResolveProfile(new StartupEntry(Web, StartupAction.Start, "gone"), profiles)?.Name);

        var (runs, _) = Runs();
        runs.SetProfile(Web, "https");
        runs.SetStartupProject(Web);
        Assert.Equal([new StartupEntry(Web, StartupAction.Start, "https")], runs.StartupTargets);
    }

    [Fact]
    public void A_named_set_starts_its_projects_and_one_gone_from_the_files_falls_back_to_the_single_project()
    {
        File.WriteAllText(Path.Combine(_root, "Contoso.slnxLaunch"), Classic);
        var (runs, solutions) = Runs();
        runs.Load(Web, null, multiple: true, [new StartupEntry(Worker, StartupAction.Start)]);
        Assert.Empty(runs.ReloadLaunchSets());
        runs.SelectLaunchSet("Web + Worker");

        Assert.True(runs.MultipleStartup);
        Assert.Equal("Web + Worker", runs.ActiveLaunchSet?.Name);
        Assert.Equal([new StartupEntry(Web, StartupAction.Start, "https"), new StartupEntry(Worker, StartupAction.StartWithoutDebugging)], runs.StartupTargets);

        // The unnamed set is still there to choose.
        runs.SelectLaunchSet(null);
        Assert.Equal([new StartupEntry(Worker, StartupAction.Start)], runs.StartupTargets);

        // A checkout removed the file: the choice is kept for the session, Start starts the single project meanwhile.
        runs.SelectLaunchSet("Web + Worker");
        File.Delete(Path.Combine(_root, "Contoso.slnxLaunch"));
        runs.ReloadLaunchSets();
        Assert.False(runs.MultipleStartup);
        Assert.Equal("Web + Worker", runs.ChosenLaunchSet);
        Assert.Equal(Web, Assert.Single(runs.StartupTargets).Project);

        // Set as Startup Project ends it.
        runs.SetStartupProject(Worker);
        Assert.Null(runs.ChosenLaunchSet);
        GC.KeepAlive(solutions);
    }

    [Fact]
    public void Saving_a_renamed_active_set_keeps_it_active_and_deleting_it_leaves_the_unnamed_set()
    {
        var (runs, _) = Runs();
        runs.SaveLaunchSet(new LaunchSet("First", [new StartupEntry(Web, StartupAction.Start)], shared: false));
        runs.SelectLaunchSet("First");
        var changed = 0;
        runs.StartupChanged += (_, _) => changed++;

        runs.SaveLaunchSet(runs.ActiveLaunchSet!.With("Second", [new StartupEntry(Web, StartupAction.Start)], shared: false), previousName: "First");

        Assert.True(changed > 0);
        Assert.Equal("Second", runs.ActiveLaunchSet?.Name);
        Assert.Equal(["Second"], runs.LaunchSets.Select(s => s.Name));
        runs.DeleteLaunchSet(runs.ActiveLaunchSet!);
        Assert.Empty(runs.LaunchSets);
        Assert.Null(runs.ChosenLaunchSet);
        Assert.False(File.Exists(Path.Combine(_root, "Contoso.slnxLaunch.user")));
    }

    private (RunService Runs, SolutionService Solutions) Runs()
    {
        var solutions = new SolutionService(() => "/nonexistent/codalus-buildhost.dll");
        solutions.UseSolution(new SolutionInfo(
            Slnx,
            SolutionFormats.Slnx,
            [],
            [new SolutionProjectInfo(Web, "Web", null, null), new SolutionProjectInfo(Worker, "Worker", null, null)],
            [],
            null,
            Trusted: true));
        return (new RunService(solutions, new Output.OutputService(), _ => { }), solutions);
    }
}
