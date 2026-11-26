using Codalus.Platform;
using Codalus.Tests;
using Codalus.Workbench.Node;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The version managers' Node.js, found in their own folders under a home folder of the test's (never this machine's):
/// each one's default when new enough, else its newest that is; only folders with both node and npx.
/// </summary>
public sealed class NodeVersionManagersTests : IDisposable
{
    private readonly string _home = Path.Combine(RealPath.Resolve(Path.GetTempPath()), "codalus-node-managers-" + Guid.NewGuid().ToString("N"));

    public NodeVersionManagersTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string? Variable(string name) => name == "HOME" ? _home : null;

    /// <summary>A Node.js's bin folder at <paramref name="relative"/> under the home folder, with node (and npx).</summary>
    private string Bin(string relative, bool npx = true)
    {
        var bin = Path.Combine(_home, relative);
        Directory.CreateDirectory(bin);
        StandInScript.Write(Path.Combine(bin, "node"), "#!/bin/sh\n");
        if (npx)
        {
            StandInScript.Write(Path.Combine(bin, "npx"), "#!/bin/sh\n");
        }

        return bin;
    }

    private void Text(string relative, string text)
    {
        var file = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private ManagedNode? Resolve(string manager, int? major = null) => NodeVersionManagers.Resolve(manager, Variable, major, PlatformKind.Linux);

    [Fact]
    public void Nvm_takes_its_default_alias_through_other_aliases_else_its_newest_with_npx()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Version managers' Node.js is looked for on macOS and Linux.");
        Bin(".nvm/versions/node/v20.11.0/bin");
        var jod = Bin(".nvm/versions/node/v22.21.1/bin");
        Bin(".nvm/versions/node/v26.13.1/bin", npx: false); // npm removed: not usable
        var newest = Bin(".nvm/versions/node/v24.1.0/bin", npx: false);

        // nvm's npx is a link to npm's script, as nvm installs it.
        var script = Path.Combine(_home, ".nvm/versions/node/v24.1.0/lib/node_modules/npm/bin/npx-cli.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        StandInScript.Write(script, "#!/usr/bin/env node\n");
        File.CreateSymbolicLink(Path.Combine(newest, "npx"), "../lib/node_modules/npm/bin/npx-cli.js");

        Assert.Equal(new ManagedNode("nvm", newest, "24.1.0"), Resolve("nvm")); // no default: the newest with npx

        Text(".nvm/alias/default", "lts/*\n");
        Text(".nvm/alias/lts/*", "lts/jod\n");
        Text(".nvm/alias/lts/jod", "v22.21.1\n");
        Assert.Equal(jod, Resolve("nvm")!.Folder);
        Assert.Equal(newest, Resolve("nvm", 24)!.Folder); // the default is too old for the agent: the newest that isn't

        Text(".nvm/alias/default", "20\n");
        Assert.Equal("20.11.0", Resolve("nvm")!.Version);
        Assert.Null(Resolve("nvm", 26));

        // NVM_DIR moves it.
        var moved = Path.Combine(_home, "elsewhere");
        Directory.Move(Path.Combine(_home, ".nvm"), moved);
        Assert.Null(Resolve("nvm"));
        Assert.Equal("20.11.0", NodeVersionManagers.Resolve("nvm", n => n == "NVM_DIR" ? moved : Variable(n), null, PlatformKind.Linux)!.Version);
    }

    [Fact]
    public void Fnm_volta_asdf_and_mise_each_have_their_folders_and_defaults()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Version managers' Node.js is looked for on macOS and Linux.");

        // fnm: the folder its aliases/default link points to.
        var fnm22 = Bin(".local/share/fnm/node-versions/v22.1.0/installation/bin");
        Bin(".local/share/fnm/node-versions/v24.0.0/installation/bin");
        Directory.CreateDirectory(Path.Combine(_home, ".local/share/fnm/aliases"));
        Directory.CreateSymbolicLink(Path.Combine(_home, ".local/share/fnm/aliases/default"), Path.GetDirectoryName(fnm22)!);
        Assert.Equal(fnm22, Resolve("fnm")!.Folder);

        // Volta: platform.json's runtime.
        var volta = Bin(".volta/tools/image/node/22.5.0/bin");
        Bin(".volta/tools/image/node/24.2.0/bin");
        Text(".volta/tools/user/platform.json", """{ "node": { "runtime": "22.5.0", "npm": null } }""");
        Assert.Equal(volta, Resolve("volta")!.Folder);
        Assert.Equal("Volta", Resolve("volta")!.ManagerName);

        // asdf: ~/.tool-versions.
        Bin(".asdf/installs/nodejs/22.3.0/bin");
        var asdf = Bin(".asdf/installs/nodejs/24.4.0/bin");
        Text(".tool-versions", "python 3.12.0\nnodejs 24.4.0\n");
        Assert.Equal(asdf, Resolve("asdf")!.Folder);

        // mise: its own links (26, latest) aren't versions; the default is config.toml's [tools] node.
        var mise24 = Bin(".local/share/mise/installs/node/24.1.0/bin");
        var mise26 = Bin(".local/share/mise/installs/node/26.0.0/bin");
        Directory.CreateSymbolicLink(Path.Combine(_home, ".local/share/mise/installs/node/latest"), Path.GetDirectoryName(mise26)!);
        Assert.Equal(mise26, Resolve("mise")!.Folder);
        Text(".config/mise/config.toml", "[env]\nnode = \"x\"\n\n[tools]\nnode = \"24\"\n");
        Assert.Equal(mise24, Resolve("mise")!.Folder);

        Assert.Equal(["nvm", "fnm", "volta", "asdf", "mise"], NodeVersionManagers.Names);
        Assert.Equal(["fnm", "volta", "asdf", "mise"], NodeVersionManagers.Find(Variable, 22, PlatformKind.Linux).Select(n => n.Manager));
        Assert.Equal(["mise"], NodeVersionManagers.Find(Variable, 26, PlatformKind.Linux).Select(n => n.Manager));
        Assert.Empty(NodeVersionManagers.Find(Variable, null, PlatformKind.Windows));
    }
}
