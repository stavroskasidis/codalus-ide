using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Tests;

public class LaunchSettingsTests
{
    private static readonly string s_project = Path.Combine(Path.GetTempPath(), "app", "App.csproj");

    private static RunInfo Info(string? runCommand = "/dotnet/dotnet", string? runArguments = "exec \"/app/bin/App.dll\"") =>
        new(s_project, "net10.0", "Exe", "/app/bin/App.dll", runCommand, runArguments, null, null);

    [Fact]
    public void Profiles_are_read_in_order_with_comments_and_unknown_properties()
    {
        var profiles = LaunchSettings.Parse("""
            {
              // comment
              "$schema": "https://json.schemastore.org/launchsettings.json",
              "iisSettings": { "windowsAuthentication": false },
              "profiles": {
                "web": {
                  "commandName": "Project",
                  "commandLineArgs": "--verbose",
                  "launchBrowser": true,
                  "launchUrl": "swagger",
                  "applicationUrl": "https://localhost:7001;http://localhost:5001",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "N": 1 },
                  "dotnetRunMessages": true,
                },
                "tool": { "commandName": "Executable", "executablePath": "dotnet", "workingDirectory": "sub" },
                "iis": { "commandName": "IISExpress" }
              }
            }
            """);

        Assert.Equal(["web", "tool", "iis"], profiles.Select(p => p.Name));
        var web = profiles[0];
        Assert.Equal("--verbose", web.CommandLineArgs);
        Assert.True(web.LaunchBrowser);
        Assert.Equal("Development", web.EnvironmentVariables!["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("1", web.EnvironmentVariables["N"]);
        Assert.True(profiles[1].IsRunnable);
        Assert.False(profiles[2].IsRunnable);
        Assert.Empty(LaunchSettings.Parse("{}"));
    }

    [Fact]
    public void Project_profiles_run_the_sdk_run_command_with_the_profile_arguments_and_environment()
    {
        var profile = new LaunchProfile("web", "Project", "--verbose", ApplicationUrl: "https://localhost:7001;http://localhost:5001", LaunchBrowser: true, LaunchUrl: "swagger",
            EnvironmentVariables: new Dictionary<string, string> { ["A"] = "1" });
        var plan = LaunchPlanner.Plan(Info(), profile);

        Assert.Equal("/dotnet/dotnet", plan.FileName);
        Assert.Equal("exec \"/app/bin/App.dll\" --verbose", plan.Arguments);
        Assert.Equal(Path.GetDirectoryName(s_project), plan.WorkingDirectory);
        Assert.Equal("1", plan.Environment["A"]);
        Assert.Equal("https://localhost:7001;http://localhost:5001", plan.Environment["ASPNETCORE_URLS"]);
        Assert.Equal("web", plan.Environment["DOTNET_LAUNCH_PROFILE"]);
        Assert.Equal("https://localhost:7001/swagger", plan.BrowserUrl);
    }

    [Fact]
    public void Without_a_profile_the_program_starts_with_no_arguments_in_the_project_folder()
    {
        var plan = LaunchPlanner.Plan(Info(runCommand: "/app/bin/App.exe", runArguments: null), null);
        Assert.Equal("/app/bin/App.exe", plan.FileName);
        Assert.Equal(string.Empty, plan.Arguments);
        Assert.Empty(plan.Environment);
        Assert.Null(plan.BrowserUrl);
    }

    [Fact]
    public void Executable_profiles_and_unsupported_commands()
    {
        var plan = LaunchPlanner.Plan(Info(), new LaunchProfile("tool", "Executable", "--info", WorkingDirectory: "sub", ExecutablePath: "dotnet"));
        Assert.Equal("dotnet", plan.FileName); // found on the PATH
        Assert.Equal("--info", plan.Arguments);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(s_project)!, "sub"), plan.WorkingDirectory);

        Assert.Throws<InvalidOperationException>(() => LaunchPlanner.Plan(Info(), new LaunchProfile("iis", "IISExpress")));
        Assert.Throws<InvalidOperationException>(() => LaunchPlanner.Plan(new RunInfo(s_project, "net10.0", "Library", "/app/bin/Lib.dll", null, null, null, null), null));
    }

    [Theory]
    [InlineData("http://*:5000", null, "http://localhost:5000/")]
    [InlineData("https://localhost:7001", "https://example.com/x", "https://example.com/x")]
    [InlineData(null, "swagger", null)]
    public void Browser_urls(string? applicationUrl, string? launchUrl, string? expected) =>
        Assert.Equal(expected, LaunchPlanner.BrowserUrl(applicationUrl, launchUrl));
}
