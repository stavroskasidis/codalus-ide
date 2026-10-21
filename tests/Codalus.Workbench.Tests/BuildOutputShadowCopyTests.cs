using Codalus.Workbench.Previewer;

namespace Codalus.Workbench.Tests;

/// <summary>
/// #66 on every OS: the copy of a build output a designer host runs on, so a build can replace the original while a form
/// is open. The files the host needs (deps and runtimeconfig files, the top-level assemblies, runtimes/, satellites)
/// must copy; others (a database another process holds, a log) are skipped. Paths inside the output map to the copy;
/// only the app's own assemblies (its deps file's) make it outdated; copies go when they're deleted or their process
/// has ended, read-only files included.
/// </summary>
public sealed class BuildOutputShadowCopyTests : IDisposable
{
    private const string Deps = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0", "signature": "" },
          "targets": {
            ".NETCoreApp,Version=v8.0": {
              "App/1.0.0": {
                "dependencies": { "Lib": "1.0.0" },
                "runtime": { "App.dll": {} },
                "resources": { "fr/App.resources.dll": { "locale": "fr" } }
              },
              "Lib/1.0.0": { "runtime": { "Lib.dll": {} } }
            }
          },
          "libraries": {}
        }
        """;

    private readonly string _temp = Directory.CreateTempSubdirectory("codalus-shadow-").FullName;

    private string Root => Path.Combine(_temp, "copies");

    private string Output => Path.Combine(_temp, "bin", "Debug", "net8.0-windows");

    private string App => Path.Combine(Output, "App.dll");

    private string OutputFile(params string[] parts) => Path.Combine([Output, .. parts]);

    /// <summary>An app's build output: its assemblies and pdb, deps and runtimeconfig files, a satellite, a native asset, a read-only text file, a database, a log and a publish folder.</summary>
    private void MakeOutput(bool deps = true)
    {
        Write("App.dll", "app");
        Write("App.pdb", "pdb");
        Write("App.runtimeconfig.json", "{}");
        if (deps)
        {
            Write("App.deps.json", Deps);
        }

        Write("Lib.dll", "lib");
        Write("fr/App.resources.dll", "fr");
        Write("runtimes/win-x64/native/x.dll", "native");
        Write("notes.txt", "notes");
        File.SetAttributes(OutputFile("notes.txt"), FileAttributes.ReadOnly);
        Write("Database.mdf", "data");
        Write("app.log", "log");
        Write("publish/App.dll", "published");
    }

    private void Write(string relative, string text)
    {
        var path = OutputFile(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        File.WriteAllText(path, text);
    }

    /// <summary>Rewrites a file with new contents (a space more: a deps file stays JSON) and a newer time, as a build does.</summary>
    private void Rewrite(string relative)
    {
        var path = OutputFile(relative.Split('/'));
        File.SetAttributes(path, FileAttributes.Normal);
        File.AppendAllText(path, " ");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
    }

    private BuildOutputShadowCopy Copy(Action<string, string>? copyFile = null, params string[] assemblies) =>
        BuildOutputShadowCopy.Create([Output], assemblies.Length == 0 ? [App] : assemblies, Root, copyFile, TestContext.Current.CancellationToken);

    private string[] Copies() => Directory.Exists(Root) ? [.. Directory.GetDirectories(Root).Select(Path.GetFileName).OfType<string>()] : [];

    [Fact]
    public async Task The_build_output_is_copied_with_its_satellites_and_runtimes_and_paths_map_to_the_copy()
    {
        MakeOutput();
        var copy = Copy();
        Assert.Equal(Root, Path.GetDirectoryName(copy.Folder));
        Assert.Matches($"^{Environment.ProcessId}-[0-9a-f]{{8}}$", Path.GetFileName(copy.Folder));
        var app = Path.Combine(copy.Folder, "0");
        string[] copied = ["App.dll", "App.pdb", "App.runtimeconfig.json", "App.deps.json", "Lib.dll", Path.Combine("fr", "App.resources.dll"), Path.Combine("runtimes", "win-x64", "native", "x.dll"), "notes.txt", "Database.mdf", "app.log"];
        Assert.All(copied, f => Assert.True(File.Exists(Path.Combine(app, f)), $"{f} wasn't copied"));
        Assert.Equal("fr", await File.ReadAllTextAsync(Path.Combine(app, "fr", "App.resources.dll"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(app, "publish")), "a publish folder isn't the app the designer runs");
        Assert.False(File.GetAttributes(Path.Combine(app, "notes.txt")).HasFlag(FileAttributes.ReadOnly), "a read-only file's copy must be deletable");
        Assert.Equal(copied.Length, copy.Files);
        Assert.Equal(copied.Sum(f => new FileInfo(OutputFile(f)).Length), copy.Bytes);
        Assert.Empty(copy.Skipped);

        // Paths in the output are the copy's; others stay.
        Assert.Equal(Path.Combine(app, "App.dll"), copy.Map(App));
        Assert.Equal(app, copy.Map(Output));
        Assert.Equal(Path.Combine(app, "fr", "App.resources.dll"), copy.Map(OutputFile("fr", "App.resources.dll")));
        var elsewhere = Path.Combine(_temp, "elsewhere", "Other.dll");
        Assert.Equal(elsewhere, copy.Map(elsewhere));
        Assert.Equal(Output + "2", copy.Map(Output + "2")); // a sibling folder whose name starts the same isn't inside

        // A second folder (a designed library's output elsewhere) is the next numbered folder.
        var library = Path.Combine(_temp, "lib");
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(Path.Combine(library, "Forms.dll"), "forms", TestContext.Current.CancellationToken);
        var two = BuildOutputShadowCopy.Create([Output, library], [App, Path.Combine(library, "Forms.dll")], Root, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(two.Folder, "1", "Forms.dll"), two.Map(Path.Combine(library, "Forms.dll")));
        Assert.True(File.Exists(Path.Combine(two.Folder, "1", "Forms.dll")));

        // What the host needs, and what's copied only when it can be.
        Assert.True(BuildOutputShadowCopy.IsRequired("App.dll"));
        Assert.True(BuildOutputShadowCopy.IsRequired("App.exe"));
        Assert.True(BuildOutputShadowCopy.IsRequired("App.deps.json"));
        Assert.True(BuildOutputShadowCopy.IsRequired("App.runtimeconfig.json"));
        Assert.True(BuildOutputShadowCopy.IsRequired("App.runtimeconfig.dev.json"));
        Assert.True(BuildOutputShadowCopy.IsRequired("fr/App.resources.dll"));
        Assert.True(BuildOutputShadowCopy.IsRequired(Path.Combine("zh-Hans", "App.resources.dll")));
        Assert.True(BuildOutputShadowCopy.IsRequired("pt-BR/App.resources.dll"));
        Assert.True(BuildOutputShadowCopy.IsRequired("runtimes/win-x64/native/x.dll"));
        Assert.False(BuildOutputShadowCopy.IsRequired("App.pdb"));
        Assert.False(BuildOutputShadowCopy.IsRequired("App.xml"));
        Assert.False(BuildOutputShadowCopy.IsRequired("App.dll.config"));
        Assert.False(BuildOutputShadowCopy.IsRequired("notes.txt"));
        Assert.False(BuildOutputShadowCopy.IsRequired("Database.mdf"));
        Assert.False(BuildOutputShadowCopy.IsRequired("data/App.resources.dll"));
        Assert.False(BuildOutputShadowCopy.IsRequired("plugins/Plugin.dll"));
        Assert.False(BuildOutputShadowCopy.IsRequired("fr/readme.txt"));

        await copy.DeleteAsync();
        await two.DeleteAsync();
    }

    [Fact]
    public async Task A_file_another_process_holds_is_skipped_unless_the_host_needs_it()
    {
        MakeOutput();

        // LocalDB holds its database, the running app its log: skipped, and the copy goes on.
        void Held(string source, string target)
        {
            switch (Path.GetFileName(source))
            {
                case "Database.mdf":
                    throw new IOException("The process cannot access the file because it is being used by another process.");
                case "app.log":
                    throw new UnauthorizedAccessException("Access is denied.");
                default:
                    File.Copy(source, target, overwrite: true);
                    break;
            }
        }

        var copy = Copy(Held);
        Assert.Equal(new[] { OutputFile("Database.mdf"), OutputFile("app.log") }.Order(StringComparer.Ordinal), copy.Skipped.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(copy.Folder, "0", "App.dll")));
        Assert.False(File.Exists(Path.Combine(copy.Folder, "0", "Database.mdf")));
        await copy.DeleteAsync();
        Assert.Empty(Copies());

        // An assembly the host needs: the designer doesn't start, with the file and the reason, and nothing is left.
        var error = Assert.Throws<PreviewerException>(() => Copy((source, target) =>
        {
            if (Path.GetFileName(source) == "Lib.dll")
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            File.Copy(source, target, overwrite: true);
        }));
        Assert.StartsWith("The designer didn't start: copying " + OutputFile("Lib.dll"), error.Message, StringComparison.Ordinal);
        Assert.Contains("being used by another process", error.Message, StringComparison.Ordinal);
        Assert.Empty(Copies());

        // A real lock: Windows refuses the copy (the file is skipped); elsewhere .NET's advisory lock may refuse it too, or the file copies.
        await using (var held = new FileStream(OutputFile("Database.mdf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = Copy();
            Assert.True(locked.Skipped.Contains(OutputFile("Database.mdf")) || File.Exists(Path.Combine(locked.Folder, "0", "Database.mdf")));
            Assert.True(File.Exists(Path.Combine(locked.Folder, "0", "App.dll")));
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains(OutputFile("Database.mdf"), locked.Skipped);
            }

            await locked.DeleteAsync();
        }
    }

    [Fact]
    public async Task Only_the_apps_own_assemblies_make_the_copy_outdated()
    {
        MakeOutput();
        Write("Other.dll", "another project's");
        Write("Other.deps.json", "{}");
        var copy = Copy();
        Assert.False(copy.IsOutdated());

        // What the app runs: its assembly, a library its deps file lists, the deps file (a new reference).
        foreach (var built in new[] { "App.dll", "Lib.dll", "App.deps.json", "fr/App.resources.dll" })
        {
            Rewrite(built);
            Assert.True(copy.IsOutdated(), $"{built} was rebuilt");
            await copy.DeleteAsync();
            copy = Copy();
            Assert.False(copy.IsOutdated());
        }

        // What the app writes into its output, and another project's output in a shared folder: no restart.
        foreach (var written in new[] { "Database.mdf", "app.log", "notes.txt", "App.pdb", "Other.dll", "Other.deps.json" })
        {
            Rewrite(written);
            Assert.False(copy.IsOutdated(), $"{written} changed, which the app doesn't load from its deps file");
        }

        // Clean: the files are gone, which doesn't restart the designer (the next build does).
        File.Delete(App);
        File.Delete(OutputFile("Lib.dll"));
        Assert.False(copy.IsOutdated());
        await copy.DeleteAsync();

        // No deps file: every top-level assembly counts, and a new one too.
        File.Delete(OutputFile("App.deps.json"));
        Write("App.dll", "app");
        Write("Lib.dll", "lib");
        copy = Copy();
        Assert.False(copy.IsOutdated());
        Rewrite("Other.dll");
        Assert.True(copy.IsOutdated());
        await copy.DeleteAsync();
        copy = Copy();
        Write("Extra.dll", "extra");
        Assert.True(copy.IsOutdated());
        await copy.DeleteAsync();
    }

    [Fact]
    public async Task Copies_of_ended_sessions_are_swept_including_read_only_files_and_a_missing_output_fails_to_start()
    {
        MakeOutput();
        var live = Copy();

        // A copy left by a process that ended (a crashed IDE): swept, its read-only file too; this process's live copy stays.
        var stale = Path.Combine(Root, "2147483646-abc");
        Directory.CreateDirectory(Path.Combine(stale, "0"));
        var readOnly = Path.Combine(stale, "0", "App.dll");
        await File.WriteAllTextAsync(readOnly, "app", TestContext.Current.CancellationToken);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var running = Path.Combine(Root, "2147483645-def");
        Directory.CreateDirectory(running);
        Directory.CreateDirectory(Path.Combine(Root, "not-a-copy"));
        Assert.Equal(1, BuildOutputShadowCopy.Sweep(Root, pid => pid == 2147483645));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(running)); // its process still runs
        Assert.True(Directory.Exists(Path.Combine(Root, "not-a-copy")));
        Assert.True(Directory.Exists(live.Folder));

        // A copy of this process's that it no longer uses (its deletion failed) goes at the next sweep.
        var forgotten = Path.Combine(Root, $"{Environment.ProcessId}-0badc0de");
        Directory.CreateDirectory(forgotten);
        Assert.Equal(1, BuildOutputShadowCopy.Sweep(Root, _ => true));
        Assert.False(Directory.Exists(forgotten));
        Assert.True(Directory.Exists(live.Folder));

        // A copy with read-only files is deleted, and deleting never throws (twice is fine).
        File.SetAttributes(Path.Combine(live.Folder, "0", "App.dll"), FileAttributes.ReadOnly);
        await live.DeleteAsync();
        await live.DeleteAsync();
        Assert.False(Directory.Exists(live.Folder));

        // An output that isn't there: the designer doesn't start, and says to build.
        var missing = Path.Combine(_temp, "missing");
        var error = Assert.Throws<PreviewerException>(() => BuildOutputShadowCopy.Create([missing], [Path.Combine(missing, "App.dll")], Root, cancellationToken: TestContext.Current.CancellationToken));
        Assert.StartsWith("The designer didn't start", error.Message, StringComparison.Ordinal);
        Assert.Contains(missing, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deps_files_runtime_target_lists_the_apps_assemblies_and_satellites()
    {
        Directory.CreateDirectory(Output);
        var deps = OutputFile("App.deps.json");
        File.WriteAllText(deps, Deps.Replace("\"Lib/1.0.0\": { \"runtime\": { \"Lib.dll\": {} } }", "\"Lib/1.0.0\": { \"runtime\": { \"Lib.dll\": {} } }, \"Pkg/2.0.0\": { \"runtime\": { \"lib/net8.0/Pkg.dll\": {} }, \"resources\": { \"lib/net8.0/de/Pkg.resources.dll\": { \"locale\": \"de\" } } }", StringComparison.Ordinal));
        Assert.Equal(["App.dll", Path.Combine("fr", "App.resources.dll"), "Lib.dll", "Pkg.dll", Path.Combine("de", "Pkg.resources.dll")], BuildOutputShadowCopy.DepsAssemblies(deps));
        File.WriteAllText(deps, "not json");
        Assert.Empty(BuildOutputShadowCopy.DepsAssemblies(deps));
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_temp, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
