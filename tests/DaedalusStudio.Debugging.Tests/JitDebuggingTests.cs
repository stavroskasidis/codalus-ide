using System.Diagnostics;
using DaedalusStudio.Debugging.Host;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>
/// Just-In-Time debugging (M14c, ADR 0039): registration in a stand-in user environment (never the real one: the tests
/// leave no registration behind), and a crash with the registration's variables writing a dump that the watcher reports
/// and the companion opens.
/// </summary>
public sealed class JitDebuggingTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>
{
    private sealed class FakeEnvironment : IUserEnvironment
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string Description => "a test's environment";

        public IReadOnlyDictionary<string, string> Read(IReadOnlyCollection<string> names) =>
            Values.Where(p => names.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        public void Write(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                if (value is null)
                {
                    Values.Remove(name);
                }
                else
                {
                    Values[name] = value;
                }
            }
        }
    }

    [Fact]
    public void Registering_sets_the_crash_dump_variables_and_unregistering_removes_them()
    {
        var environment = new FakeEnvironment();
        environment.Values["OTHER"] = "kept";
        var folder = Path.Combine(Path.GetTempPath(), "daedalus-jit-" + Guid.NewGuid().ToString("N")[..8]);
        var jit = new JitDebugging(environment, folder);
        Assert.False(jit.IsRegistered);

        jit.Register();
        Assert.True(jit.IsRegistered);
        Assert.Equal("1", environment.Values[JitDebugging.EnableVariable]);
        Assert.Equal("4", environment.Values[JitDebugging.TypeVariable]);
        Assert.Equal(Path.Combine(folder, "%e.%p.dmp"), environment.Values[JitDebugging.NameVariable]);
        Assert.False(new JitDebugging(environment, folder + "-other").IsRegistered);
        Assert.True(new JitDebugging(environment, folder + "-other").IsSetElsewhere);

        jit.Unregister();
        Assert.False(jit.IsRegistered);
        Assert.Equal(["OTHER"], environment.Values.Keys);
        Directory.Delete(folder);
    }

    [Fact]
    public void The_Linux_environment_files_keep_the_users_profile()
    {
        var home = Path.Combine(Path.GetTempPath(), "daedalus-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        try
        {
            var profile = Path.Combine(home, ".profile");
            File.WriteAllText(profile, "export PATH=\"$HOME/bin:$PATH\"\n");
            var environment = new LinuxUserEnvironment(home);
            var dumps = Path.Combine(home, "crash-dumps");
            var jit = new JitDebugging(environment, dumps);

            jit.Register();
            Assert.True(jit.IsRegistered);
            var conf = File.ReadAllText(Path.Combine(home, ".config", "environment.d", "90-daedalus-jit.conf"));
            Assert.Contains("DOTNET_DbgEnableMiniDump=1\n", conf, StringComparison.Ordinal);
            var text = File.ReadAllText(profile);
            Assert.StartsWith("export PATH=\"$HOME/bin:$PATH\"\n\n" + LinuxUserEnvironment.BlockStart, text, StringComparison.Ordinal);
            Assert.Contains($"export DOTNET_DbgMiniDumpName='{Path.Combine(dumps, "%e.%p.dmp")}'", text, StringComparison.Ordinal);

            jit.Register(); // again: still one block
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(profile), ">>> Daedalus"));

            jit.Unregister();
            Assert.False(jit.IsRegistered);
            Assert.Equal("export PATH=\"$HOME/bin:$PATH\"\n", File.ReadAllText(profile));
            Assert.False(File.Exists(Path.Combine(home, ".config", "environment.d", "90-daedalus-jit.conf")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task A_crash_with_the_registration_writes_a_dump_that_is_reported_and_opens()
    {
        var folder = Path.Combine(Path.GetTempPath(), "daedalus-crashes-" + Guid.NewGuid().ToString("N")[..8]);
        using var watcher = new CrashDumpWatcher(folder);
        var reported = new TaskCompletionSource<CrashDump>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Crashed += (_, dump) => reported.TrySetResult(dump);
        var dumps = new Dumps();
        try
        {
            // The registration's variables, given to this one program only.
            var start = new ProcessStartInfo("dotnet", ["exec", debuggee.Dll, "--crash"]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var (name, value) in JitDebugging.Settings(folder))
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(0, process.ExitCode);

            var dump = await reported.Task.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
            Assert.Equal(process.Id, dump.ProcessId);
            Assert.StartsWith("dotnet", dump.Program, StringComparison.Ordinal);
            Assert.Contains(watcher.Existing(), d => d.Path == dump.Path);

            var summary = dumps.Open(dump.Path);
            Assert.Contains(summary.Threads, t => t.Exception?.Contains("crashed on purpose", StringComparison.Ordinal) == true
                || t.Frames.Any(f => f.Line == DebuggeeFixture.Line(debuggee.Program, "crash")));
            Assert.Contains("crashed on purpose", await error, StringComparison.Ordinal);
            dumps.Close(summary.Id);
        }
        finally
        {
            dumps.Dispose();
            Directory.Delete(folder, recursive: true);
        }
    }
}
