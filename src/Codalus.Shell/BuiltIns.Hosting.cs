using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>Git hosting and repository features (M13b, ADR 0031): settings, tool windows, commands and the Git menu.</summary>
internal static partial class BuiltIns
{
    public static IReadOnlyList<ToolWindowDescriptor> HostingToolWindows() =>
    [
        new(PullRequestsViewModel.ToolWindowId, "Pull Requests", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "pull-request" },
        new(WorkItemsViewModel.ToolWindowId, "Work Items", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "issue" },
        new(WorktreesViewModel.ToolWindowId, "Worktrees", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "worktree" },
    ];

    public static IReadOnlyList<SettingDescriptor> HostingSettings()
    {
        var shell = ContributionSource.Shell;
        return
        [
            SettingDescriptor.Create(ShellHost.HostingEnabledSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Git Hosting Integration",
                Category = "Source Control",
                Description = "Talk to GitHub, Azure DevOps and GitLab (pull requests, checks, work items) for the hosts you sign in to. Off: no hosting call is made.",
                Order = 1,
            },
            SettingDescriptor.Create(ShellHost.HostingCiSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Show CI Status",
                Category = "Source Control",
                Description = "Show the checks, pipelines or builds of the current branch, beside the branch and in Git Changes.",
                Order = 2,
            },
            SettingDescriptor.Create(ShellHost.HostingStatusSecondsSetting, "120", SettingType.Integer, shell) with
            {
                Title = "CI Status Refresh (Seconds)",
                Category = "Source Control",
                Description = "How often the CI status and the branch's pull request are read again while signed in.",
                Minimum = 15,
                Maximum = 3600,
                Order = 3,
            },
            SettingDescriptor.Create(ShellHost.HostingReviewCommentsSetting, "true", SettingType.Boolean, shell) with
            {
                Title = "Show Review Comments in the Editor",
                Category = "Source Control",
                Description = "Show the review comments of the current branch's pull request at their lines.",
                Order = 4,
            },
            SettingDescriptor.Create(ShellHost.HostingResolvedCommentsSetting, "false", SettingType.Boolean, shell) with
            {
                Title = "Show Resolved Review Comments",
                Category = "Source Control",
                Description = "Also show resolved review threads in the editor.",
                Order = 5,
            },
        ];
    }

    public static void RegisterHostingCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string enablement = "gitRepository") =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Git", Enablement = WhenClause.Parse(enablement) },
                Ui(handler));
        void Show(string id, string title, string window, string enablement = "") =>
            Add(id, title, () =>
            {
                shell.Layout.ShowToolWindow(window, focus: true);
                return Task.CompletedTask;
            }, enablement);

        Add("hosting.accounts", "Git Hosting Accounts…", () =>
        {
            shell.ShowAccounts();
            return Task.CompletedTask;
        }, string.Empty);
        Show("git.pullRequests", "Pull Requests", PullRequestsViewModel.ToolWindowId);
        Add("git.createPullRequest", "Create Pull Request…", shell.CreatePullRequestAsync, "gitRepository && hostingRepository");
        Show("git.workItems", "Work Items", WorkItemsViewModel.ToolWindowId);
        Add("git.publish", "Publish to a Git Host…", shell.PublishAndReadRemotesAsync, "solutionOpen");
        Add("hosting.showChecks", "Show Checks", shell.ShowChecksAsync, "hostingRepository");
        Add("hosting.refresh", "Refresh Pull Requests and Checks", shell.RefreshHostingAsync, "gitRepository");
        Show("git.worktrees", "Worktrees", WorktreesViewModel.ToolWindowId, "gitRepository");
        Add("git.addWorktree", "Add Worktree…", shell.AddWorktreeAsync);
        Add("git.switchRepository", "Switch Repository…", shell.PickRepositoryAsync, "gitRepositories");
        Add("git.lfsLocks", "Git LFS Locks…", shell.ShowLfsLocksAsync, "gitLfs");
        Add("git.lfsLock", "Git LFS: Lock File", () => shell.LockActiveFileAsync(unlock: false), "gitLfs");
        Add("git.lfsUnlock", "Git LFS: Unlock File", () => shell.LockActiveFileAsync(unlock: true), "gitLfs");
        Add("git.lfsPull", "Git LFS: Download Files", shell.LfsPullAsync, "gitLfs");
    }
}
