using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Tests;

/// <summary>The Windows installer's `codalus` command (M31): its PATH entry and the script behind it.</summary>
public sealed class InstallationTests
{
    private const string Bin = @"C:\Users\me\AppData\Local\Codalus\bin";

    [Fact]
    public void Installing_adds_the_folder_once_and_keeps_the_other_entries_as_they_are()
    {
        const string path = @"%USERPROFILE%\.dotnet\tools;C:\Tools";

        var added = Installation.WithEntry(path, Bin);

        Assert.Equal(@"%USERPROFILE%\.dotnet\tools;C:\Tools;" + Bin, added);
        Assert.Equal(added, Installation.WithEntry(added, Bin + @"\")); // an update doesn't add it again
        Assert.Equal(Bin, Installation.WithEntry(string.Empty, Bin));
    }

    [Fact]
    public void Uninstalling_removes_only_that_folder_in_any_case_or_form()
    {
        var path = @"C:\Tools;" + Bin.ToUpperInvariant() + @"\;%USERPROFILE%\.dotnet\tools";

        Assert.Equal(@"C:\Tools;%USERPROFILE%\.dotnet\tools", Installation.WithoutEntry(path, Bin));
        Assert.Equal(@"C:\Tools", Installation.WithoutEntry(@"C:\Tools", Bin));
    }

    [Fact]
    public void The_command_runs_the_installed_app_waits_for_it_and_passes_its_exit_code_on()
    {
        Assert.Equal("@echo off\r\n\"%~dp0..\\current\\codalus.exe\" %*\r\nexit /b %ERRORLEVEL%\r\n", Installation.CommandScript);
    }
}
