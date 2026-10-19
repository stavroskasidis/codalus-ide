using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Trusted folders (ADR 0004) by their real path: the shell opens a solution under its real path (links resolved), so a
/// folder trusted through a link must be trusted through its target and the other way round, as macOS's
/// <c>/var</c> → <c>/private/var</c> does with every temp folder, and entries saved under a link's spelling still count.
/// </summary>
public sealed class TrustStoreTests : IDisposable
{
    // The temp folder as the system spells it (on macOS through /var, a link to /private/var), not resolved: the
    // tests see the same two spellings the trust dialog did.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-trust-" + Guid.NewGuid().ToString("N"));

    public TrustStoreTests() => Directory.CreateDirectory(_root);

    private string File => Path.Combine(_root, "state", "trust.json");

    private string Target => Path.Combine(_root, "target");

    private string Link => Path.Combine(_root, "link");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A folder with a repository, and a link to it.</summary>
    private void CreateLinkedRepo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need extra privileges on Windows.");
        Directory.CreateDirectory(Path.Combine(Target, "repo"));
        Directory.CreateSymbolicLink(Link, Target);
    }

    [Fact]
    public void A_folder_trusted_through_a_link_is_trusted_through_its_target_and_saved_under_its_real_path()
    {
        CreateLinkedRepo();
        var store = new TrustStore(File);
        store.Trust(Path.Combine(Link, "repo"));

        var reloaded = new TrustStore(File);
        Assert.True(reloaded.IsTrusted(Path.Combine(Target, "repo", "App.slnx")));
        Assert.True(reloaded.IsTrusted(RealPath.Resolve(Path.Combine(Target, "repo", "App.slnx"))));
        Assert.True(reloaded.IsTrusted(Path.Combine(Link, "repo", "App.slnx")));
        Assert.Equal(RealPath.Resolve(Path.Combine(Target, "repo")), Assert.Single(reloaded.Folders));
        Assert.False(reloaded.IsTrusted(Path.Combine(Target, "other", "App.slnx")));

        // Trusting the target again adds nothing; revoking it through the link removes it.
        reloaded.Trust(Path.Combine(Target, "repo"));
        Assert.Single(reloaded.Folders);
        reloaded.Revoke(Path.Combine(Link, "repo"));
        Assert.Empty(reloaded.Folders);
        Assert.False(reloaded.IsTrusted(Path.Combine(Target, "repo", "App.slnx")));
    }

    [Fact]
    public void A_folder_trusted_through_its_target_is_trusted_through_a_link()
    {
        CreateLinkedRepo();
        var store = new TrustStore(File);
        store.Trust(Target);

        Assert.True(store.IsTrusted(Path.Combine(Link, "repo", "App.slnx")));
        Assert.True(new TrustStore(File).IsTrusted(Path.Combine(Link, "repo", "App.slnx")));

        // A parent trusted later replaces it, whichever spelling it comes in.
        store.Trust(_root);
        Assert.Equal(RealPath.Resolve(_root), Assert.Single(store.Folders));
        Assert.True(store.IsTrusted(Path.Combine(Link, "repo", "App.slnx")));
    }

    [Fact]
    public void An_entry_saved_under_a_link_spelling_still_matches_by_its_spelling_and_where_it_leads()
    {
        CreateLinkedRepo();

        // As a version before this one saved it: the link's spelling, not resolved.
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new { folders = new[] { Link } }));
        var store = new TrustStore(File);

        Assert.True(store.IsTrusted(Path.Combine(Link, "repo", "App.slnx")));
        Assert.True(store.IsTrusted(Path.Combine(Target, "repo", "App.slnx")));
        Assert.True(store.IsTrusted(RealPath.Resolve(Path.Combine(Target, "repo", "App.slnx"))));
        Assert.Equal([Link], store.Folders); // kept as saved

        // It covers its target, so trusting the target or a folder in it adds nothing; revoking the target removes it.
        store.Trust(Target);
        store.Trust(Path.Combine(Target, "repo"));
        Assert.Equal([Link], store.Folders);
        store.Revoke(Target);
        Assert.Empty(store.Folders);
        Assert.False(store.IsTrusted(Path.Combine(Link, "repo", "App.slnx")));
    }

    [Fact]
    public void A_link_in_a_trusted_folder_that_leads_out_of_it_is_not_trusted()
    {
        CreateLinkedRepo();
        var trusted = Directory.CreateDirectory(Path.Combine(_root, "trusted")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(trusted, "escape"), Target);
        var store = new TrustStore(File);
        store.Trust(trusted);

        Assert.True(store.IsTrusted(Path.Combine(trusted, "App.slnx")));
        Assert.False(store.IsTrusted(Path.Combine(trusted, "escape", "repo", "App.slnx")));
    }

    [Fact]
    public void Paths_compare_with_the_platform_case_rules()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "Repo")).FullName;
        var store = new TrustStore(File);
        store.Trust(repo);

        var differentCase = Path.Combine(_root, "REPO", "App.slnx");
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), store.IsTrusted(differentCase));
        Assert.True(store.IsTrusted(Path.Combine(repo, "App.slnx")));
    }
}
