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

    [Fact]
    public void WSL_and_Roslyn_component_profiles_are_read_and_runnable()
    {
        var profiles = LaunchSettings.Parse("""
            {
              "profiles": {
                "WSL": { "commandName": "WSL2", "distributionName": "", "commandLineArgs": "--port 5" },
                "Generator": { "commandName": "DebugRoslynComponent", "targetProject": "..\\App\\App.csproj" }
              }
            }
            """);
        Assert.Equal(string.Empty, profiles[0].DistributionName); // the default distribution
        Assert.Equal(@"..\App\App.csproj", profiles[1].TargetProject);
        Assert.All(profiles, p => Assert.True(p.IsRunnable));
        Assert.Throws<InvalidOperationException>(() => LaunchPlanner.Plan(Info(), profiles[1])); // planned from the target's compiler command line
    }

    [Fact]
    public void A_WSL_profile_runs_the_build_output_on_the_distributions_dotnet()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WSL profiles run on Windows.");
        var project = Path.Combine(@"D:\src\App", "App.csproj");
        var info = new RunInfo(project, "net10.0", "Exe", @"D:\src\App\bin\Debug\net10.0\App.dll", @"D:\src\App\bin\Debug\net10.0\App.exe", null, null, null);
        var plan = LaunchPlanner.Plan(info, new LaunchProfile("WSL", LaunchProfile.Wsl, "--name \"x y\"", ApplicationUrl: "http://localhost:5000", EnvironmentVariables: new Dictionary<string, string> { ["MODE"] = "it's" }, DistributionName: "Ubuntu"));

        Assert.Equal(("Ubuntu", "dotnet", "/mnt/d/src/App"), (plan.Wsl!.Distribution, plan.Wsl.Program, plan.Wsl.WorkingDirectory));
        Assert.Equal(["exec", "/mnt/d/src/App/bin/Debug/net10.0/App.dll", "--name", "x y"], plan.Wsl.Arguments);
        Assert.Equal("wsl.exe", plan.FileName);
        Assert.Equal(["-d", "Ubuntu", "--cd", "/mnt/d/src/App", "--exec", "sh", "-lc"], plan.ArgumentList!.Take(7));
        var script = plan.ArgumentList![7];
        Assert.StartsWith("exec env ", script, StringComparison.Ordinal);
        Assert.Contains("'MODE=it'\\''s'", script, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS=http://localhost:5000", script, StringComparison.Ordinal);
        Assert.EndsWith(" dotnet exec /mnt/d/src/App/bin/Debug/net10.0/App.dll --name 'x y'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Roslyn_component_runs_the_sdks_compiler_on_the_targets_command_line()
    {
        var sdk = Path.Combine(Path.GetTempPath(), "daedalus-fake-dotnet-" + Guid.NewGuid().ToString("N")[..8], "sdk", "10.0.100");
        Directory.CreateDirectory(Path.Combine(sdk, "Roslyn", "bincore"));
        File.WriteAllText(Path.Combine(sdk, "Roslyn", "bincore", "csc.dll"), string.Empty);
        var output = Path.Combine(Path.GetTempPath(), "daedalus-roslyn-plan-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var inputs = new CompilerInputsInfo(Path.Combine("C:", "src", "Target", "Target.csproj"), "net10.0", "C#", CompilerInputStates.Ok, null,
                ["/noconfig", "/out:obj\\Debug\\Target.dll", "/refout:obj\\refint\\Target.dll", "/analyzer:\"C:\\Program Files\\gen.dll\"", "Program.cs"], [], null, null);
            var plan = RoslynComponents.Plan(sdk, inputs, output);
            var root = Directory.GetParent(sdk)!.Parent!.FullName;
            Assert.Equal(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), plan.FileName);
            Assert.Equal(["exec", Path.Combine(sdk, "Roslyn", "bincore", "csc.dll"), "@" + Path.Combine(output, "compile.rsp")], plan.ArgumentList);
            Assert.Equal(Path.GetDirectoryName(inputs.Path), plan.WorkingDirectory);
            var response = File.ReadAllLines(Path.Combine(output, "compile.rsp"));
            Assert.Equal("/noconfig", response[0]);
            Assert.StartsWith("/out:", response[1], StringComparison.Ordinal);
            Assert.Contains(Path.Combine(output, "Target.dll"), response[1], StringComparison.Ordinal);
            Assert.DoesNotContain(response, l => l.StartsWith("/refout", StringComparison.Ordinal));
            Assert.Contains("/analyzer:\"C:\\Program Files\\gen.dll\"", response);
            Assert.Throws<InvalidOperationException>(() => RoslynComponents.Plan(sdk, inputs with { Language = "F#" }, output));
        }
        finally
        {
            Directory.Delete(Directory.GetParent(sdk)!.Parent!.FullName, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://*:5000", null, "http://localhost:5000/")]
    [InlineData("https://localhost:7001", "https://example.com/x", "https://example.com/x")]
    [InlineData(null, "swagger", null)]
    public void Browser_urls(string? applicationUrl, string? launchUrl, string? expected) =>
        Assert.Equal(expected, LaunchPlanner.BrowserUrl(applicationUrl, launchUrl));
}
