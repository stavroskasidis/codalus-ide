using System.Diagnostics;
using Codalus.Debugging.Host;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M44 (ADR 0074): <c>codalus-debughost --activate-app</c>, which starts a registered packaged app through Windows'
/// <c>IApplicationActivationManager</c> and prints its process id. Its arguments are checked everywhere; off Windows it
/// says packaged apps start only there; on Windows an id no package has is refused by Windows with its error. Starting a
/// real package is <c>WinUIShellTests</c>' (Windows, Developer Mode).
/// </summary>
public sealed class AppActivationTests
{
    [Fact]
    public void Its_arguments_are_checked_and_off_windows_it_says_where_packaged_apps_start()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, AppActivation.Run(["--activate-app"], output, error));
        Assert.Contains("Usage: codalus-debughost --activate-app", error.ToString(), StringComparison.Ordinal);

        error.GetStringBuilder().Clear();
        var code = AppActivation.Run(["--activate-app", "Codalus.NoSuchPackage_0000000000000!App", "--arguments", "--x"], output, error);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(1, code);
            Assert.Contains("Windows didn't start Codalus.NoSuchPackage_0000000000000!App", error.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(3, code);
            Assert.Contains("Packaged apps start only on Windows.", error.ToString(), StringComparison.Ordinal);
        }

        Assert.Empty(output.ToString()); // nothing that reads as a process id
    }

    [Fact]
    public async Task The_companion_process_answers_activate_app()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll"));
        start.ArgumentList.Add("--activate-app");
        start.ArgumentList.Add("Codalus.NoSuchPackage_0000000000000!App");
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 3, process.ExitCode);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.NotEmpty((await stderr).Trim());
    }
}
