using System.Text;
using Codalus.Workbench.Output;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The Output window's colors (M34h): which lines are errors, warnings and successes, ANSI escapes decoded into runs
/// (never shown raw), and standard error kept apart in the channel through trims.
/// </summary>
public class OutputFormattingTests
{
    [Theory]
    // MSBuild, the compilers and NuGet.
    [InlineData(@"C:\src\App\Program.cs(12,5): error CS1002: ; expected [C:\src\App\App.csproj]", OutputLineKind.Error)]
    [InlineData("/src/App/Program.cs(12,5): error CS0103: The name 'x' does not exist in the current context", OutputLineKind.Error)]
    [InlineData("MSBUILD : error MSB1009: Project file does not exist.", OutputLineKind.Error)]
    [InlineData("  error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org", OutputLineKind.Error)]
    [InlineData("CSC : error CS2001: Source file 'x.cs' could not be found.", OutputLineKind.Error)]
    [InlineData("EXEC : error : the command exited with code 1", OutputLineKind.Error)]
    [InlineData("/src/App/App.csproj: error: The SDK 'Foo' wasn't found.", OutputLineKind.Error)]
    [InlineData("Build FAILED.", OutputLineKind.Error)]
    [InlineData("Build failed: 1 error, 0 warnings. Elapsed 00:00:02.10.", OutputLineKind.Error)]
    [InlineData("Restore failed: 1 error, 0 warnings. Elapsed 00:00:01.00.", OutputLineKind.Error)]
    [InlineData("    1 Error(s)", OutputLineKind.Error)]
    [InlineData("/src/App/Program.cs(3,13): warning CS0168: The variable 'e' is declared but never used [/src/App/App.csproj]", OutputLineKind.Warning)]
    [InlineData("  warning NU1603: App depends on Foo (>= 1.0.0) but Foo 1.0.0 was not found.", OutputLineKind.Warning)]
    [InlineData("    2 Warning(s)", OutputLineKind.Warning)]
    [InlineData("Build succeeded.", OutputLineKind.Success)]
    [InlineData("Build succeeded: 0 errors, 2 warnings. Elapsed 00:00:03.21.", OutputLineKind.Success)]
    [InlineData("Rebuild succeeded: 0 errors, 0 warnings. Elapsed 00:00:05.00.", OutputLineKind.Success)]
    [InlineData("    0 Error(s)", OutputLineKind.Success)]
    [InlineData("========== Build: 3 succeeded, 0 failed, 0 up-to-date, 0 skipped ==========", OutputLineKind.Success)]
    [InlineData("========== Build: 2 succeeded, 1 failed, 0 up-to-date, 0 skipped ==========", OutputLineKind.Error)]
    // Exceptions and their stack traces.
    [InlineData("Unhandled exception. System.InvalidOperationException: Sequence contains no elements", OutputLineKind.Error)]
    [InlineData("System.IO.FileNotFoundException: Could not find file '/tmp/x'.", OutputLineKind.Error)]
    [InlineData(" ---> System.Net.Sockets.SocketException (111): Connection refused", OutputLineKind.Error)]
    [InlineData("   at Lib.Calculator.Divide(Int32 a, Int32 b) in /src/Lib/Calculator.cs:line 12", OutputLineKind.Error)]
    [InlineData("   at Program.<Main>$(String[] args)", OutputLineKind.Error)]
    [InlineData("--- End of stack trace from previous location ---", OutputLineKind.Error)]
    // The console logger and the test runners.
    [InlineData("fail: Microsoft.AspNetCore.Server.Kestrel[13]", OutputLineKind.Error)]
    [InlineData("crit: Microsoft.AspNetCore.Hosting.Diagnostics[6]", OutputLineKind.Error)]
    [InlineData("warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]", OutputLineKind.Warning)]
    [InlineData("error: unable to read askpass response", OutputLineKind.Error)]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git", OutputLineKind.Error)]
    [InlineData("warning: LF will be replaced by CRLF in a.txt.", OutputLineKind.Warning)]
    [InlineData("Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 30 ms", OutputLineKind.Error)]
    [InlineData("Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 28 ms", OutputLineKind.Success)]
    [InlineData("  Failed Lib.Tests.CalculatorTests.Divides [12 ms]", OutputLineKind.Error)]
    [InlineData("Test Run Successful.", OutputLineKind.Success)]
    [InlineData("Test Run Failed.", OutputLineKind.Error)]
    // Plain lines stay plain.
    [InlineData("info: Microsoft.Hosting.Lifetime[14]", OutputLineKind.None)]
    [InlineData("      Now listening on: http://localhost:5000", OutputLineKind.None)]
    [InlineData("Build started: App.csproj (Debug|Any CPU)", OutputLineKind.None)]
    [InlineData("  App -> /src/App/bin/Debug/net9.0/App.dll", OutputLineKind.None)]
    [InlineData("Treat warnings as errors is off; the error count is 0", OutputLineKind.None)]
    [InlineData("The program '[4242] App' has exited with code 0 (0x0).", OutputLineKind.None)]
    [InlineData("   at the moment nothing is listening", OutputLineKind.None)]
    [InlineData("", OutputLineKind.None)]
    public void Lines_are_classified_by_what_they_report(string line, OutputLineKind kind) => Assert.Equal(kind, OutputClassifier.Classify(line));

    [Fact]
    public void A_long_line_is_classified_by_its_start()
    {
        Assert.Equal(OutputLineKind.Error, OutputClassifier.Classify("Program.cs(1,1): error CS1002: " + new string('x', 100_000)));
        Assert.Equal(OutputLineKind.None, OutputClassifier.Classify(new string('x', 100_000) + " error CS1002: late"));
    }

    [Fact]
    public void Ansi_colors_become_runs_and_no_escape_shows()
    {
        var (text, runs) = Decode("\e[31mred\e[0m plain \e[1;33mbold yellow\e[22m yellow\e[39m \e[38;5;208mindexed\e[m \e[38;2;10;20;30mrgb\e[0m");
        Assert.Equal("red plain bold yellow yellow indexed rgb", text);
        Assert.DoesNotContain('\e', text);
        Assert.Equal(
        [
            new OutputRun(0, 3, new OutputStyle(1, OutputAttributes.None)),
            new OutputRun(10, 11, new OutputStyle(3, OutputAttributes.Bold)),
            new OutputRun(21, 7, new OutputStyle(3, OutputAttributes.None)),
            new OutputRun(29, 7, new OutputStyle(208, OutputAttributes.None)),
            new OutputRun(37, 3, new OutputStyle(OutputStyle.RgbFlag | 0x0A141E, OutputAttributes.None)),
        ], runs);
    }

    [Fact]
    public void Other_sequences_are_dropped()
    {
        // Erasing the line, moving the cursor, a window title (OSC, BEL), a hyperlink (OSC, ST), the terminal logger's
        // progress (OSC 9;4), hiding the cursor (a private mode), a character set.
        var (text, runs) = Decode("\e[2K\e[1A\e]0;title\a\e]8;;https://example.com\e\\link\e]8;;\e\\ \e]9;4;3;\e\\\e[?25lok\e[?25h\e(B!");
        Assert.Equal("link ok!", text);
        Assert.Empty(runs);
    }

    [Fact]
    public void Colors_and_sequences_carry_across_appends()
    {
        var decoder = new AnsiDecoder();
        var output = new StringBuilder();
        var runs = new List<OutputRun>();
        decoder.Decode("one \e[3", isError: false, output, runs);
        decoder.Decode("2mgreen ", isError: false, output, runs);
        decoder.Decode("still green\e[0", isError: false, output, runs);
        decoder.Decode("m plain", isError: false, output, runs);
        Assert.Equal("one green still green plain", output.ToString());
        Assert.Equal([new OutputRun(4, 17, new OutputStyle(2, OutputAttributes.None))], runs);
        Assert.Equal(output.Length, decoder.Length);
    }

    [Fact]
    public void Standard_error_is_a_run_of_its_own_and_ansi_colors_inside_it_stay()
    {
        var decoder = new AnsiDecoder();
        var output = new StringBuilder();
        var runs = new List<OutputRun>();
        decoder.Decode("out\n", isError: false, output, runs);
        decoder.Decode("err \e[33mwarn\e[0m\n", isError: true, output, runs);
        Assert.Equal("out\nerr warn\n", output.ToString());
        Assert.Equal(
        [
            new OutputRun(4, 4, new OutputStyle(-1, OutputAttributes.Error)),
            new OutputRun(8, 4, new OutputStyle(3, OutputAttributes.Error)),
            new OutputRun(12, 1, new OutputStyle(-1, OutputAttributes.Error)),
        ], runs);
    }

    [Fact]
    public void Plain_text_decodes_without_runs()
    {
        var (text, runs) = Decode("Build succeeded.\nelapsed 1s\n");
        Assert.Equal("Build succeeded.\nelapsed 1s\n", text);
        Assert.Empty(runs);
    }

    [Fact]
    public void A_channel_keeps_its_standard_error_ranges_through_appends_trims_and_clears()
    {
        var channel = new OutputChannel("run", "Run");
        var events = new List<OutputAppendedEventArgs>();
        channel.TextAppended += (_, e) => events.Add(e);
        channel.AppendLine("> dotnet run");
        channel.AppendErrorLine("Unhandled exception.");
        channel.AppendErrorLine("   at Program.Main()");
        channel.AppendLine("exited");
        var snapshot = channel.GetSnapshot();
        Assert.Equal("> dotnet run\nUnhandled exception.\n   at Program.Main()\nexited\n", snapshot.Text);
        Assert.Equal([(13, 42)], snapshot.ErrorRanges); // the two error lines are one range
        Assert.Equal([false, true, true, false], events.Select(e => e.IsError));

        // Trimming drops what's before the new start and keeps later ranges where their text is.
        var line = new string('x', 1023) + "\n";
        for (var i = 0; i < (OutputChannel.MaxLength / 1024) + 10; i++)
        {
            channel.Append(line);
        }

        channel.AppendErrorLine("late error");
        snapshot = channel.GetSnapshot();
        var (start, length) = Assert.Single(snapshot.ErrorRanges);
        Assert.Equal("late error\n", snapshot.Text.Substring(start, length));
        Assert.Contains(events, e => e.Text is null); // the trim asked for a re-read

        channel.Clear();
        Assert.Empty(channel.GetSnapshot().ErrorRanges);
        channel.AppendErrorLine("again");
        Assert.Equal([(0, 6)], channel.GetSnapshot().ErrorRanges);
    }

    private static (string Text, List<OutputRun> Runs) Decode(string text)
    {
        var decoder = new AnsiDecoder();
        var output = new StringBuilder();
        var runs = new List<OutputRun>();
        decoder.Decode(text, isError: false, output, runs);
        return (output.ToString(), runs);
    }
}
