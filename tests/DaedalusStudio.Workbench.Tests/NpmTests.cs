using DaedalusStudio.Workbench.Node;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// M16d (ADR 0027): npm in solutions. Finding package.json files, reading scripts and Task Runner bindings, choosing
/// the npm, and an install of a local dependency with the built-in Node.js (downloaded into a shared cache once).
/// </summary>
public sealed class NpmTests : IDisposable
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "daedalus-test-node-cache");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-npm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Write(string relative, string text)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Packages_are_found_below_the_roots_but_not_in_installed_packages_or_build_output()
    {
        var app = Write("package.json", "{}");
        var client = Write("src/App/ClientApp/package.json", "{}");
        Write("node_modules/left-pad/package.json", "{}");
        Write("src/App/bin/Debug/package.json", "{}");
        Write("src/App/obj/package.json", "{}");
        Write(".git/package.json", "{}");

        // The project folder is a root too; each package is found once.
        Assert.Equal([app, client], Npm.Find([_root, Path.Combine(_root, "src", "App")]), StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Npm.Find([Path.Combine(_root, "missing")]));
    }

    [Fact]
    public void A_package_json_gives_its_scripts_dependencies_and_bindings()
    {
        var path = Write("package.json", """
            {
              // comments and trailing commas, as editors allow
              "name": "web-scripts",
              "scripts": { "build": "vite build", "test": "vitest", "odd": 3, },
              "-vs-binding": { "BeforeBuild": ["build"], "ProjectOpen": ["test", "watch"] },
              "devDependencies": { "vite": "^7.0.0" }
            }
            """);
        var package = Npm.Read(path)!;
        Assert.Equal("web-scripts", package.Name);
        Assert.Equal([new NpmScript("build", "vite build"), new NpmScript("test", "vitest")], package.Scripts);
        Assert.True(package.HasDependencies);
        Assert.False(package.IsRestored);
        Assert.Equal(["build"], package.BoundTo(Npm.BeforeBuild));
        Assert.Equal(["test", "watch"], package.BoundTo("projectopen"));
        Assert.Empty(package.BoundTo(Npm.AfterBuild));

        Assert.False(Npm.Read(Write("other/package.json", """{ "dependencies": {} }"""))!.HasDependencies);
        Assert.Null(Npm.Read(Write("broken/package.json", "{ \"name\": ")));
        Assert.Null(Npm.Read(Write("array/package.json", "[]")));
    }

    [Fact]
    public void The_users_npm_is_found_on_the_path()
    {
        Assert.Null(Npm.UserNpm(Path.Combine(_root, "empty")));
        var bin = Path.Combine(_root, "nodejs");
        if (OperatingSystem.IsWindows())
        {
            // The installer's layout: npm's script runs on its node, without cmd.exe.
            Write("nodejs/npm.cmd", "@echo off");
            var withoutNode = Npm.UserNpm(bin)!;
            Assert.EndsWith("cmd.exe", withoutNode.Program, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["/d", "/c", Path.Combine(bin, "npm.cmd"), "run", "build"], withoutNode.Arguments("run", "build"));

            Write("nodejs/node.exe", string.Empty);
            var cli = Write("nodejs/node_modules/npm/bin/npm-cli.js", string.Empty);
            var npm = Npm.UserNpm(string.Join(Path.PathSeparator, Path.Combine(_root, "empty"), bin))!;
            Assert.Equal(Path.Combine(bin, "node.exe"), npm.Program);
            Assert.Equal([cli, "run", "build"], npm.Arguments("run", "build"));
        }
        else
        {
            Write("nodejs/npm", "#!/usr/bin/env node");
            var npm = Npm.UserNpm(bin)!;
            Assert.Equal(Path.Combine(bin, "npm"), npm.Program);
            Assert.Equal(["run", "build"], npm.Arguments("run", "build"));
        }
    }

    [Fact]
    public async Task The_built_in_npm_installs_a_local_dependency_and_runs_a_script()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        using var node = new NodeRuntime(SharedCache);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await node.EnsureAsync(null, timeout.Token);

        Write("lib/package.json", """{ "name": "local-lib", "version": "1.0.0", "main": "index.js" }""");
        Write("lib/index.js", "module.exports = () => 'from the local lib';\n");
        var package = Write("app/package.json", """
            { "name": "app", "version": "1.0.0", "private": true,
              "scripts": { "show": "node -e \"console.log(require('local-lib')())\"" },
              "dependencies": { "local-lib": "file:../lib" } }
            """);
        var npm = Npm.BuiltIn(node);
        var output = new List<string>();
        void Collect(string line)
        {
            lock (output)
            {
                output.Add(line);
            }
        }

        var folder = Path.GetDirectoryName(package)!;
        Assert.Equal(0, await Npm.RunAsync(npm, ["install", "--no-audit", "--no-fund"], folder, Collect, timeout.Token));
        Assert.True(Npm.Read(package)!.IsRestored);
        Assert.Equal(0, await Npm.RunAsync(npm, ["run", "show"], folder, Collect, timeout.Token));
        lock (output)
        {
            Assert.Contains("from the local lib", output);
        }
    }
}
