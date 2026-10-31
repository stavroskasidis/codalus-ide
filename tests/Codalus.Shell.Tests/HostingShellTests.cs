using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M13b in the shell (ADR 0031), against <see cref="MockGitHub"/>: signing in with a device code, the Pull Requests
/// window, checking one out, review comments in the editor, CI status, creating a pull request, work items and <c>#</c>
/// completion, worktrees, LFS badges, a solution spanning two repositories, and askpass. Nothing reaches a real host.
/// </summary>
public sealed class HostingShellTests
{
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    private static void Configure(string folder)
    {
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        Git(folder, "config", "credential.helper", string.Empty);
    }

    /// <summary>
    /// BuildAndRun as a repository whose origin is on the mock GitHub: fetched over git's dumb HTTP protocol from a bare
    /// repository the mock serves, pushed straight to that bare repository (<c>pushurl</c>). A pull request #1 from
    /// <c>feature/greeter</c> has a review thread on Greeter.cs, checks, and issues assigned to the user.
    /// </summary>
    private static async Task<(ShellFixture F, MockGitHub GitHub, string Folder, string Bare)> OpenHostedRepositoryAsync(bool signIn = true)
    {
        var github = new MockGitHub("contoso", "BuildAndRun");
        var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.GitHub"));
        await f.Shell.Workbench.Settings.SetAsync("github.serverUrl", github.ServerUrl, SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.Settings.SetAsync("github.oauthClientId", MockGitHub.ClientId, SettingScope.User, CancellationToken.None);

        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        var bare = Path.Combine(f.UserData, "BuildAndRun.git");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "-q", "--bare", "-b", "main");
        Git(folder, "remote", "add", "origin", github.RemoteUrl);
        Git(folder, "config", "remote.origin.pushurl", bare);
        Git(folder, "push", "-q", "-u", "origin", "main");

        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        Git(folder, "switch", "-q", "-c", "feature/greeter");
        File.WriteAllText(greeter, File.ReadAllText(greeter).Replace("Hello from", "Howdy from", StringComparison.Ordinal));
        Git(folder, "commit", "-q", "-am", "Say howdy");
        Git(folder, "push", "-q", "origin", "feature/greeter");
        Git(folder, "switch", "-q", "main");
        Git(folder, "branch", "-q", "-D", "feature/greeter");
        Git(folder, "update-ref", "-d", "refs/remotes/origin/feature/greeter");
        Git(bare, "update-server-info");
        github.GitDirectory = bare;

        github.PullRequests.Add(new MockPullRequest(1, "Add the greeter", "bo", "feature/greeter", "main") { Body = "Greets **everyone** now.", Reviewers = ["ada"] });
        github.Threads[1] = [new MockReviewThread("src/Greetings/Greeter.cs", 3, [("bo", "Should this be configurable?"), ("ada", "Later.")])];
        github.CheckRuns.Add(new MockCheckRun("build", "completed", "success"));
        github.CheckRuns.Add(new MockCheckRun("tests", "completed", "failure"));
        github.Issues.Add(new MockIssue(12, "Greeter is rude", "ada"));
        github.Issues.Add(new MockIssue(120, "Docs for the greeter", null));

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        if (signIn)
        {
            var row = f.Shell.Accounts.Row("github") ?? throw new InvalidOperationException("No GitHub row on the Accounts page.");
            row.Token = MockGitHub.Token;
            row.SignInWithTokenCommand.Execute(null);
            await ShellFixture.PumpUntil(() => row.IsSignedIn && f.Shell.Match is not null, seconds: 60);
        }

        return (f, github, folder, bare);
    }

    [AvaloniaFact]
    public async Task Signing_in_with_a_device_code_shows_the_code_until_the_host_approves()
    {
        var (f, github, _, _) = await OpenHostedRepositoryAsync(signIn: false);
        await using var _f = f;
        await using var _g = github;

        // Not signed in: nothing loaded the plugin, and the Pull Requests window offers to sign in.
        Assert.Equal(Workbench.Plugins.PluginState.Ready, f.Shell.Plugins.Plugins.Single(p => p.Id == "codalus.github").State);
        Assert.DoesNotContain(github.Requests, r => r.Path.StartsWith("/api/", StringComparison.Ordinal));
        f.Shell.Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.PullRequests.CanSignIn);
        Assert.Equal("Sign in to GitHub", f.Shell.PullRequests.SignInLabel);

        github.PendingPolls = int.MaxValue;
        var document = f.Shell.ShowAccounts("github");
        var row = document.ViewModel.Row("github")!;
        await ShellFixture.PumpUntil(() => row.HasCode, seconds: 60);
        Assert.Equal(MockGitHub.UserCode, row.UserCode);
        Assert.True(row.IsSigningIn);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AccountsView>().Any());
        Assert.Contains("memory", f.Shell.Accounts.StoreText, StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "m13b-accounts-device-code");

        github.PendingPolls = 0;
        await ShellFixture.PumpUntil(() => row.IsSignedIn, seconds: 60);
        Assert.Equal("Signed in as ada on " + new Uri(github.ServerUrl).Authority, row.StatusText);
        await ShellFixture.PumpUntil(() => f.Shell.PullRequests.Rows.Count == 1, seconds: 60);

        row.SignOutCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !row.IsSignedIn && f.Shell.Match is null);
        await ShellFixture.PumpUntil(() => f.Shell.PullRequests.CanSignIn);
    }

    [AvaloniaFact]
    public async Task Pull_requests_check_out_with_review_comments_in_the_editor_and_checks()
    {
        var (f, github, folder, _) = await OpenHostedRepositoryAsync();
        await using var _f = f;
        await using var _g = github;

        f.Shell.Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId);
        var prs = f.Shell.PullRequests;
        await ShellFixture.PumpUntil(() => prs.Rows.Count == 1, seconds: 60);
        Assert.Equal("contoso/BuildAndRun on GitHub", prs.RepositoryText);
        prs.Selected = prs.Rows[0];
        await ShellFixture.PumpUntil(() => prs.HasDetails && prs.Threads.Count == 1, seconds: 30);
        Assert.Equal("#1 Add the greeter", prs.DetailsTitle);
        Assert.Contains("bo wants to merge feature/greeter into main", prs.DetailsMeta, StringComparison.Ordinal);
        Assert.Equal("src/Greetings/Greeter.cs:3", prs.Threads[0].Location);
        Assert.Equal(["bo", "ada"], prs.Threads[0].Comments.Select(c => c.Author));
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PullRequestsView>().Any());
        NavigationDepthTests.Screenshot(f, "m13b-pull-requests");

        prs.FilterIndex = 2; // review requested: ada is a reviewer
        await ShellFixture.PumpUntil(() => prs.Rows.Count == 1 && !prs.IsLoading);
        prs.FilterIndex = 1; // mine: none
        await ShellFixture.PumpUntil(() => prs.Rows.Count == 0 && !prs.IsLoading);
        Assert.Equal("None match the filter.", prs.Message);
        prs.FilterIndex = 0;
        await ShellFixture.PumpUntil(() => prs.Rows.Count == 1 && !prs.IsLoading);

        // Check out: the branch is fetched over HTTP from the mock and tracked.
        await f.Shell.CheckOutAsync(prs.Rows[0].PullRequest);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "feature/greeter", Upstream: "origin/feature/greeter" }, seconds: 60);
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        Assert.Contains("Howdy from", await File.ReadAllTextAsync(greeter), StringComparison.Ordinal);

        // The branch's pull request, its checks and its review comments.
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.PullRequestText == "#1 Add the greeter" && f.Shell.ReviewThreads.Count == 1, seconds: 60);
        Assert.Equal("failure", f.Shell.GitChanges.CiState);
        Assert.Equal("1 check failed", f.Shell.GitChanges.CiText);
        Assert.Contains(f.Shell.StatusBar.Right, i => i.Item.Id == "codalus.hosting.ci" && i.Text.Contains("1 check failed", StringComparison.Ordinal));
        var document = await f.Shell.OpenFileAsync(greeter);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var insights = document!.Controller!.Insights;
        await ShellFixture.PumpUntil(() => insights.ReviewLines.Any(r => r.Line == 3));
        Assert.Contains("◆ bo: Should this be configurable? (+1)", insights.EndOfLineText(3), StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "m13b-review-comments");

        // Turning review comments off clears them.
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.HostingReviewCommentsSetting, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => insights.ReviewLines.Count == 0, seconds: 30);
    }

    [AvaloniaFact]
    public async Task The_git_repository_window_lists_pull_requests_by_the_pull_requests_windows_filters()
    {
        var (f, github, _, _) = await OpenHostedRepositoryAsync();
        await using var _f = f;
        await using var _g = github;

        // #1 is bo's, asking for ada's review; #2 is ada's own (the signed-in user).
        github.PullRequests.Add(new MockPullRequest(2, "Tidy the docs", "ada", "docs/tidy", "main"));
        await f.RunAsync("git.manageBranches");
        await ShellFixture.PumpUntil(
            () => f.Shell.Layout.ActiveDocument is GitRepositoryDocument { ViewModel: var vm } && vm.Roots.Count == 3 && vm.Roots[1].Children.Count == 3,
            seconds: 60,
            describe: () => string.Join(" | ", (f.Shell.Layout.ActiveDocument as GitRepositoryDocument)?.ViewModel.Roots.Select(r => r.Text + ": " + string.Join(", ", r.Children.Select(c => c.Text))) ?? []));
        var vm = ((GitRepositoryDocument)f.Shell.Layout.ActiveDocument!).ViewModel;

        // Mine, Review requested and All open, with their counts; the first two open, All open collapsed.
        var folders = vm.Roots[1].Children;
        Assert.Equal(["Mine (1)", "Review requested (1)", "All open (2)"], folders.Select(c => c.Text));
        Assert.Equal([true, true, false], folders.Select(c => c.IsExpanded));
        Assert.Equal(["#2 Tidy the docs"], folders[0].Children.Select(c => c.Text));
        Assert.Equal(["#1 Add the greeter"], folders[1].Children.Select(c => c.Text));
        Assert.Equal(["#1 Add the greeter", "#2 Tidy the docs"], folders[2].Children.Select(c => c.Text).Order());
        Assert.All(folders.SelectMany(c => c.Children), c => Assert.NotNull(c.Match));

        // The view, with the pull request that asks for a review selected (CODALUS_UI_SNAPSHOTS keeps a picture of it).
        vm.Selected = folders[1].Children[0];
        folders[1].Children[0].IsSelected = true;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitRepositoryView>().Any() && vm.HasMessage || vm.HasHistory);
        GitRepositoryShellTests.Snapshots(f.Window, "git-repository-pull-requests");

        // The tree's filter keeps the folders with a match (after its pause, M57).
        vm.Filter = "docs";
        await ShellFixture.PumpUntil(() => !vm.IsRebuilding);
        Assert.Equal(["Mine (1)", "All open (2)"], vm.Roots[1].Children.Select(c => c.Text));
    }

    [AvaloniaFact]
    public async Task A_pull_request_is_created_after_pushing_the_branch()
    {
        var (f, github, folder, bare) = await OpenHostedRepositoryAsync();
        await using var _f = f;
        await using var _g = github;

        Git(folder, "switch", "-q", "-c", "feature/french-greeting");
        File.AppendAllText(Path.Combine(folder, "README.md"), "\nBonjour.\n");
        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "Bonjour");
        await f.Shell.Git.RefreshAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "feature/french-greeting" });

        var document = await f.Shell.CreatePullRequestAsync();
        Assert.NotNull(document);
        var form = document!.ViewModel;
        Assert.Equal("French greeting", form.Title);
        Assert.Equal("main", form.Target);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<CreatePullRequestView>().Any());
        form.Description = "Says *bonjour*.";
        form.IsDraft = true;
        await form.CreateAsync();
        Assert.True(form.Error is null, form.Error);
        Assert.Equal("Created #2: French greeting", form.Created);
        Assert.Contains("pull request #2 feature/french-greeting -> main", github.Created);
        Assert.True(github.PullRequests.Single(p => p.Number == 2).Draft);
        Assert.Contains("feature/french-greeting", Git(bare, "branch", "--list"), StringComparison.Ordinal);
        Assert.Equal("origin/feature/french-greeting", f.Shell.Git.Status?.Upstream);
    }

    [AvaloniaFact]
    public async Task Work_items_are_listed_and_complete_after_a_hash_in_the_commit_message()
    {
        var (f, github, _, _) = await OpenHostedRepositoryAsync();
        await using var _f = f;
        await using var _g = github;

        f.Shell.Layout.ShowToolWindow(WorkItemsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.WorkItems.Rows.Count == 1, seconds: 60);
        Assert.Equal("#12", f.Shell.WorkItems.Rows[0].Reference);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<WorkItemsView>().Any());

        f.Shell.WorkItems.Selected = f.Shell.WorkItems.Rows[0];
        f.Shell.WorkItems.MentionCommand.Execute(null);
        Assert.Equal("#12 ", f.Shell.GitChanges.Message);

        // # completion in the commit message box.
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single();
        var message = view.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().First(t => t.Name == "Message");
        message.Text = "Fixes #1";
        message.CaretIndex = message.Text.Length;
        await view.UpdateCompletionAsync();
        await ShellFixture.PumpUntil(() => view.IsCompletionOpen, seconds: 30);
        Assert.Equal(["#12", "#120"], view.CompletionItems.Select(i => i.Reference));
        NavigationDepthTests.Screenshot(f, "m13b-commit-completion");
        view.AcceptCompletion();
        Assert.Equal("Fixes #12 ", message.Text);
        Assert.False(view.IsCompletionOpen);

        Assert.Equal((4, "1"), GitChangesViewModel.CompletionQuery("Fix #1", 6));
        Assert.Null(GitChangesViewModel.CompletionQuery("a#1", 3));
        Assert.Equal(("see #42 here", 8), GitChangesViewModel.ApplyCompletion("see #4 here", 6, "#42"));
    }

    [AvaloniaFact]
    public async Task Worktrees_are_listed_added_and_removed()
    {
        var (f, github, folder, _) = await OpenHostedRepositoryAsync(signIn: false);
        await using var _f = f;
        await using var _g = github;

        f.Shell.Layout.ShowToolWindow(WorktreesViewModel.ToolWindowId);
        var worktrees = f.Shell.Worktrees;
        await ShellFixture.PumpUntil(() => worktrees.Rows.Count == 1);
        Assert.True(worktrees.Rows[0].IsCurrent);
        Assert.Contains("main worktree", worktrees.Rows[0].Flags, StringComparison.Ordinal);

        var other = Path.Combine(Path.GetDirectoryName(folder)!, "BuildAndRun-spike");
        await f.Shell.Git.AddWorktreeAsync(other, "spike", null);
        await worktrees.ReloadAsync();
        Assert.Equal(2, worktrees.Rows.Count);
        var spike = worktrees.Rows.Single(r => r.Branch == "spike");
        worktrees.Selected = spike;
        Assert.Equal("Lock", worktrees.LockText);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<WorktreesView>().Any());
        NavigationDepthTests.Screenshot(f, "m13b-worktrees");

        await f.Shell.Git.RemoveWorktreeAsync(other, force: false);
        await worktrees.ReloadAsync();
        Assert.Single(worktrees.Rows);
    }

    [AvaloniaFact]
    public async Task Lfs_files_get_badges_and_a_solution_spanning_two_repositories_gets_a_picker()
    {
        var (f, github, folder, _) = await OpenHostedRepositoryAsync(signIn: false);
        await using var _f = f;
        await using var _g = github;

        // Git LFS: a pointer file that was never downloaded.
        File.WriteAllText(Path.Combine(folder, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        Git(folder, "add", ".gitattributes");
        Git(folder, "commit", "-q", "-m", "LFS");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "assets", "logo.bin"), "version https://git-lfs.github.com/spec/v1\noid sha256:4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393\nsize 2048\n");
        await f.Shell.Git.RefreshAsync();
        await ShellFixture.PumpUntil(
            () => f.Shell.GitChanges.Changes.Any(r => r.Name == "logo.bin" && r.Badge == "LFS pointer"),
            seconds: 30,
            describe: () =>
            {
                var lfs = f.Shell.GitChanges.Lfs;
                var output = f.Shell.GitLog.GetText();
                return $"Changes [{string.Join(", ", f.Shell.GitChanges.Changes.Select(r => $"{r.Status.RelativePath} {r.Status.Index}{r.Status.WorkTree} badge={r.Badge ?? "none"}"))}]; "
                    + $"git status [{string.Join(", ", f.Shell.Git.Status?.Files.Select(s => $"{s.Index}{s.WorkTree} {s.RelativePath}") ?? ["no status"])}]; "
                    + $"LFS used={lfs.IsUsed} installed={lfs.IsInstalled} files [{string.Join(", ", lfs.Files.Select(l => $"{l.RelativePath} pointer={l.IsPointer}"))}] locksError={lfs.LocksError ?? "none"}; "
                    + $"Git output: {output[^Math.Min(2000, output.Length)..]}";
            });
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("gitLfs"));

        // The Greetings project in a repository of its own.
        var greetings = Path.Combine(folder, "src", "Greetings");
        Git(greetings, "init", "-q", "-b", "trunk");
        Configure(greetings);
        File.WriteAllText(Path.Combine(greetings, ".gitignore"), "bin/\nobj/\n"); // the build host writes there meanwhile
        Git(greetings, "add", ".");
        Git(greetings, "commit", "-q", "-m", "greetings");
        await f.Shell.DiscoverRepositoriesAsync();
        Assert.Equal(["All Repositories (2)", Path.GetFileName(folder), "Greetings"], f.Shell.GitChanges.Repositories.Select(r => r.Name));
        Assert.True(f.Shell.GitChanges.HasSeveralRepositories);
        Assert.True(f.Shell.GitChanges.IsAllRepositories); // ADR 0085: all of them by default
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("gitRepositories"));

        f.Shell.GitChanges.SelectedRepository = f.Shell.GitChanges.Repositories.Single(r => r.Name == "Greetings");
        // Until the switch ends: the status turns to trunk before it does, and a discovery that the git init above
        // scheduled can land while the repository reopens (Root null) and clear the picker's choice meanwhile.
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "trunk" } && f.Shell.GitChanges.SelectedRepository?.Name == "Greetings");
        var greeter = Path.Combine(greetings, "Greeter.cs");
        Assert.Equal(Path.GetFullPath(greetings), f.Shell.Git.RepositoryFor(greeter));
        Assert.NotNull(await f.Shell.Git.ShowInRepositoryAsync(Path.Combine(folder, "README.md"), "HEAD"));
    }

    [AvaloniaFact]
    public async Task Askpass_asks_in_the_ide_for_https_credentials_and_keeps_nothing()
    {
        await using var f = ShellFixture.Create();
        Assert.NotNull(f.Shell.Askpass);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "codalus.exe" : "codalus", f.Shell.Askpass!.Program, StringComparison.OrdinalIgnoreCase);

        // A private repository over HTTP: 401 without the right Basic credentials.
        var bare = Path.Combine(f.UserData, "private.git");
        Directory.CreateDirectory(bare);
        Git(bare, "init", "-q", "--bare", "-b", "main");
        var seed = Path.Combine(f.UserData, "seed");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-q", "-b", "main");
        Configure(seed);
        File.WriteAllText(Path.Combine(seed, "a.txt"), "a\n");
        Git(seed, "add", "-A");
        Git(seed, "commit", "-q", "-m", "a");
        Git(seed, "push", "-q", bare, "main");
        Git(bare, "update-server-info");
        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ada:s3cret"));
        await using var server = new MockHttpServer(request =>
            request.Header("Authorization") != expected
                ? new MockHttpResponse(401, [], "text/plain") { Headers = new Dictionary<string, string> { ["WWW-Authenticate"] = "Basic realm=\"mock\"" } }
                : request.Path.StartsWith("/private.git/", StringComparison.Ordinal)
                    ? request.Path == "/private.git/info/refs" ? MockHttpResponse.Text(File.ReadAllText(Path.Combine(bare, "info", "refs"))) : MockHttpResponse.File(Path.Combine(bare, request.Path["/private.git/".Length..]))
                    : MockHttpResponse.NotFound());

        var prompts = new List<string>();
        f.Shell.Askpass.Asked += (_, prompt) =>
        {
            lock (prompts)
            {
                prompts.Add(prompt.Text);
            }
        };

        // No credential helper (the user's Git Credential Manager is left out), so git asks askpass.
        var listing = Task.Run(() => f.Shell.Git.RunAsync(f.UserData, ["-c", "credential.helper=", "ls-remote", server.BaseUrl + "/private.git"], CancellationToken.None));
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 60);
        Assert.Equal("Git: Sign In", f.Shell.Dialogs.Title);
        Assert.StartsWith("Username for", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal('\0', f.Shell.Dialogs.PasswordChar);
        f.Shell.Dialogs.InputText = "ada";
        f.Shell.Dialogs.Accept();

        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.Title == "Git: Password", seconds: 60);
        Assert.Equal('●', f.Shell.Dialogs.PasswordChar);
        NavigationDepthTests.Screenshot(f, "m13b-askpass");
        f.Shell.Dialogs.InputText = "s3cret";
        f.Shell.Dialogs.Accept();

        var (code, output, _) = await listing.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, code);
        Assert.Contains("refs/heads/main", output, StringComparison.Ordinal);
        Assert.Equal(2, prompts.Count);
        Assert.Contains(server.Requests, r => r.Header("Authorization") == expected);

        // Cancelling makes git fail with its own message, and nothing was stored anywhere.
        var cancelled = Task.Run(() => f.Shell.Git.RunAsync(f.UserData, ["-c", "credential.helper=", "ls-remote", server.BaseUrl + "/private.git"], CancellationToken.None, check: false));
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 60);
        f.Shell.Dialogs.Cancel();
        var (failedCode, _, _) = await cancelled.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotEqual(0, failedCode);
        foreach (var file in Directory.EnumerateFiles(f.UserData, "*", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("s3cret", File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("s3cret", f.Shell.GitLog.GetText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Git Changes' Create a pull request button on a repository whose remote is on an Azure DevOps Server the IDE isn't
    /// set up for (ADR 0087, 0088): it opens Git Changes' sign-in panel on that server (not the Accounts page), signing in
    /// there as the operating system's user sets <c>azureDevOps.serverUrl</c>, and then the form opens. A mock server stands
    /// in, and a request with no Authorization header stands for Negotiate.
    /// </summary>
    [AvaloniaFact]
    public async Task Create_pull_request_on_an_azure_devops_server_signs_in_there_first()
    {
        await using var server = new MockHttpServer(request => (request.Method, request.Path) switch
        {
            _ when request.Header("Authorization") is not null => MockHttpResponse.Json("""{ "message": "TF400813: not authorized." }""", 401),
            ("GET", "/tfs/_apis/connectionData") => MockHttpResponse.Json("""{ "authenticatedUser": { "id": "33", "providerDisplayName": "Ada Tester", "properties": { "Account": { "$value": "CONTOSO\\ada" } } } }"""),
            ("GET", "/tfs/DefaultCollection/Apps/_apis/git/repositories/BuildAndRun") => MockHttpResponse.Json("""{ "defaultBranch": "refs/heads/main" }"""),
            ("GET", "/tfs/DefaultCollection/Apps/_apis/git/repositories/BuildAndRun/pullrequests") or ("GET", "/tfs/DefaultCollection/Apps/_apis/build/builds") => MockHttpResponse.Json("""{ "value": [] }"""),
            _ => MockHttpResponse.NotFound(),
        });
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.AzureDevOps"));
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        Git(folder, "remote", "add", "origin", server.BaseUrl + "/tfs/DefaultCollection/Apps/_git/BuildAndRun");
        Git(folder, "switch", "-q", "-c", "feature/login");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "feature/login" } && f.Shell.Suggested is { Id: "azure-devops" }, seconds: 60);

        // Not signed in anywhere: Git Changes suggests the remote's server, and the button opens its sign-in there.
        var panel = f.Shell.GitChanges.HostingSignIn!;
        Assert.Equal((true, true, false), (panel.IsVisible, panel.IsSuggested, panel.IsExpanded));
        Assert.Equal(new Uri(server.BaseUrl).Authority + "/tfs", panel.Server);
        Assert.Equal(server.BaseUrl + "/tfs/DefaultCollection/_usersSettings/tokens", panel.TokenUrl);
        Assert.True(f.Shell.Workbench.Commands.IsEnabled("git.createPullRequest"));
        await f.Shell.GitChanges.RunAsync("git.createPullRequest");
        await ShellFixture.PumpUntil(() => panel.IsExpanded);
        Assert.Null(f.Shell.Layout.FindDocument<AccountsDocument>(AccountsDocument.DocumentId));
        var row = panel.Row!;
        Assert.Equal("azure-devops", row.Id);
        Assert.Equal(server.BaseUrl + "/tfs", row.PendingServer);
        Assert.Equal((true, false, false), (row.CanSignInIntegrated, row.CanSignInWithCode, row.IsSigningIn));
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Any(b => b.Name == "HostingSignInPanel" && b.IsEffectivelyVisible));
        NavigationDepthTests.Screenshot(f, "hosting-sign-in-azure-devops-server");

        // Signing in there sets the server, and the pull request form opens on its own.
        row.SignInIntegratedCommand.Execute(null);
        await ShellFixture.PumpUntil(() => row.IsSignedIn && f.Shell.Match is not null, seconds: 60);
        Assert.Equal(server.BaseUrl + "/tfs", f.Shell.Workbench.Settings.GetString("azureDevOps.serverUrl", string.Empty));
        Assert.Equal(@"Signed in as CONTOSO\ada on " + new Uri(server.BaseUrl).Authority, row.StatusText);
        Assert.Null(row.PendingServer);
        Assert.Equal("DefaultCollection/Apps/BuildAndRun", f.Shell.Match!.Repository.Id);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<CreatePullRequestDocument>(CreatePullRequestDocument.DocumentId) is not null, seconds: 60);
        Assert.False(panel.IsVisible);
        Assert.Equal("feature/login", f.Shell.Layout.FindDocument<CreatePullRequestDocument>(CreatePullRequestDocument.DocumentId)!.ViewModel.SourceBranch);
    }

    /// <summary>
    /// Git Changes suggests signing in to the repository's host until it is dismissed for the repository (ADR 0088), and
    /// the hosting windows' Sign in open that panel, not the Accounts page. Without an OAuth app's client id there is no
    /// Sign In with Your Browser (it could only fail): the token is the way, with a link to the host's token page.
    /// </summary>
    [AvaloniaFact]
    public async Task Git_changes_suggests_signing_in_until_dismissed_and_signs_in_in_place()
    {
        var (f, github, _, _) = await OpenHostedRepositoryAsync(signIn: false);
        await using var _f = f;
        await using var _g = github;
        var panel = f.Shell.GitChanges.HostingSignIn!;
        await ShellFixture.PumpUntil(() => panel.IsVisible);
        Assert.Equal((true, false, "github"), (panel.IsSuggested, panel.IsExpanded, panel.Row?.Id));
        Assert.Contains("GitHub", panel.Text, StringComparison.Ordinal);
        Assert.Equal(github.ServerUrl + "/settings/tokens/new?scopes=repo,read:org,workflow&description=Codalus%20IDE", panel.TokenUrl);
        Assert.True(panel.Row!.CanSignInWithCode); // the mock's OAuth app is set
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Any(b => b.Name == "HostingSignInPanel" && b.IsEffectivelyVisible));
        NavigationDepthTests.Screenshot(f, "hosting-sign-in-suggested");

        // Dismissed: gone for this repository, and still gone after the host is worked out again.
        panel.DismissCommand.Execute(null);
        Assert.False(panel.IsVisible);
        Assert.True(f.Shell.Hosting.IsSignInSuggestionDismissed(f.Shell.Git.Root!));
        await f.Shell.RefreshHostingAsync();
        Assert.False(panel.IsVisible);

        // The Pull Requests window's Sign in opens the panel all the same, in Git Changes.
        f.Shell.Layout.ShowToolWindow(PullRequestsViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Shell.PullRequests.CanSignIn);
        f.Shell.PullRequests.SignInCommand.Execute(null);
        await ShellFixture.PumpUntil(() => panel is { IsVisible: true, IsExpanded: true });
        Assert.Null(f.Shell.Layout.FindDocument<AccountsDocument>(AccountsDocument.DocumentId));
        Assert.False(panel.Row.IsSigningIn); // no device sign-in started by itself

        // github.com's own app (the default) is no use on another server, here the mock: no browser sign-in.
        Assert.Equal("Ov23lieq6uB5CAp48llO", f.Shell.Workbench.Settings.Find("github.oauthClientId")?.Default.GetString());
        await f.Shell.Workbench.Settings.SetAsync("github.oauthClientId", "Ov23lieq6uB5CAp48llO", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => !panel.Row.CanSignInWithCode);

        // No OAuth app: no browser sign-in, here or on the Accounts page, and opening the page starts nothing.
        await f.Shell.Workbench.Settings.SetAsync("github.oauthClientId", string.Empty, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => !panel.Row.CanSignInWithCode);
        Assert.Equal("Sign in with a personal access token:", panel.TokenPrompt);
        NavigationDepthTests.Screenshot(f, "hosting-sign-in-token");
        var accounts = f.Shell.ShowAccounts("github");
        Assert.False(accounts.ViewModel.Row("github")!.IsSigningIn);

        // A token pasted in the panel signs in, and the panel goes.
        panel.Row.Token = MockGitHub.Token;
        panel.Row.SignInWithTokenCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Match is not null && !panel.IsVisible, seconds: 60);
    }
}
