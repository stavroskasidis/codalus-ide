using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Codalus.Platform;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The Hot Reload-while-debugging research of M14c (ADR 0040), kept as an opt-in test (<c>CODALUS_RESEARCH=1</c>):
/// a program runs under <c>dotnet watch</c> (M15's Hot Reload), netcoredbg attaches to it, and a saved edit is
/// applied, or not. It records what the runtime does today; it isn't part of the regular runs (dotnet watch is slow).
/// </summary>
public sealed partial class HotReloadResearchTests
{
    [GeneratedRegex(@"pid=(\d+) (v\d)")]
    private static partial Regex Line();

    [Fact]
    public async Task An_edit_reaches_a_program_under_dotnet_watch_while_netcoredbg_is_attached()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODALUS_RESEARCH") == "1", "Research only (CODALUS_RESEARCH=1).");
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var root = Path.Combine(Path.GetTempPath(), "codalus-hotreload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        Codalus.Tests.FixtureSdk.Pin(root);
        await File.WriteAllTextAsync(Path.Combine(root, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        var program = Path.Combine(root, "Program.cs");
        await File.WriteAllTextAsync(program, Source("v1"), TestContext.Current.CancellationToken);

        var output = new StringBuilder();
        var watch = new ProcessStartInfo("dotnet", ["watch", "run", "--non-interactive"]) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        watch.Environment["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1";
        using var process = Process.Start(watch)!;
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine("err: " + e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        string Text() { lock (output) { return output.ToString(); } }

        await using var debug = new DebugService(() => DebuggeeFixture.Adapter);
        var log = new StringBuilder();
        debug.Output += (_, o) => { lock (log) { log.Append(o.Text); } };
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            Match match;
            while (!(match = Line().Match(Text())).Success)
            {
                Assert.True(DateTime.UtcNow < deadline, "the program didn't start under dotnet watch:\n" + Text());
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }

            await debug.AttachAsync(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(program, Source("v2"), TestContext.Current.CancellationToken);

            deadline = DateTime.UtcNow.AddSeconds(60);
            while (!Text().Contains(" v2", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }

            var report = $"dotnet watch:\n{Text()}\nDebug Console:\n{log}";
            TestContext.Current.SendDiagnosticMessage(report);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "codalus-hotreload-research.txt"), report);
        }
        finally
        {
            await debug.StopAsync();
            if (!process.HasExited)
            {
                ProcessTree.Kill(process);
            }
        }
    }

    private static string Source(string version) => $$"""
        while (true)
        {
            Console.WriteLine($"pid={Environment.ProcessId} {Message()}");
            Thread.Sleep(500);
        }

        static string Message() => "{{version}}";
        """;
}
