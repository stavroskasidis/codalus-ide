using Codalus.Platform.Pty;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The command line ConPTY starts a program with: <c>CommandLineToArgvW</c> quoting, except the line <c>cmd.exe</c> runs,
/// which it reads as written (a project path in quotes reached <c>dotnet workload restore</c> with the quotes in it).
/// </summary>
public sealed class WindowsCommandLineTests
{
    [Fact]
    public void Arguments_are_quoted_the_way_CommandLineToArgvW_reads_them()
    {
        Assert.Equal(
            @"C:\Tools\app.exe plain ""with space"" ""say \""hi\"""" ""C:\My Dir\\"" """"",
            WindowsCommandLine.Build(@"C:\Tools\app.exe", ["plain", "with space", "say \"hi\"", @"C:\My Dir\", ""]));
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\cmd.exe")]
    [InlineData("cmd.exe")]
    [InlineData("CMD")]
    public void The_line_cmd_runs_goes_as_written(string cmd)
    {
        var line = @"dotnet workload restore ""D:\My Projects\App\App.csproj"" & echo. & pause";
        Assert.Equal(
            (cmd.Contains(' ', StringComparison.Ordinal) ? $"\"{cmd}\"" : cmd) + $" /s /c \"{line}\"",
            WindowsCommandLine.Build(cmd, ["/c", line]));
    }

    [Fact]
    public void A_line_after_switches_keeps_them_and_adds_s_once()
    {
        Assert.Equal("cmd.exe /d /s /c \"echo \"a b\"\"", WindowsCommandLine.Build("cmd.exe", ["/d", "/s", "/c", "echo \"a b\""]));
        Assert.Equal("cmd.exe /d /s /c \"run.cmd\"", WindowsCommandLine.Build("cmd.exe", ["/d", "/c", "run.cmd"]));
        Assert.Equal("cmd.exe /s /k \"cd \"C:\\My Dir\"\"", WindowsCommandLine.Build("cmd.exe", ["/k", "cd \"C:\\My Dir\""]));
    }

    [Fact]
    public void A_program_with_its_arguments_after_c_stays_quoted_for_the_program()
    {
        // cmd.exe passes them on as they are, and the program parses them with CommandLineToArgvW.
        Assert.Equal(@"cmd.exe /d /c run.cmd ""a b"" ""C:\My Dir\\""", WindowsCommandLine.Build("cmd.exe", ["/d", "/c", "run.cmd", "a b", @"C:\My Dir\"]));
        Assert.Equal("cmd.exe /k", WindowsCommandLine.Build("cmd.exe", ["/k"]));
        Assert.Equal("cmd.exe", WindowsCommandLine.Build("cmd.exe", []));
    }

    [Fact]
    public void Other_programs_given_c_are_quoted_as_usual()
    {
        Assert.Equal(@"tool.exe /c ""a \""b\""""", WindowsCommandLine.Build("tool.exe", ["/c", "a \"b\""]));
    }
}
