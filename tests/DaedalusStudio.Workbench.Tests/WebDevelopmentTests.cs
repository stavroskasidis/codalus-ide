using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Web;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>M15a chores: user secrets through the SDK, and the environment override of runs.</summary>
public sealed class WebDevelopmentTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daedalus-web-" + Guid.NewGuid().ToString("N"));

    public WebDevelopmentTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task User_secrets_init_adds_an_id_to_the_project()
    {
        var project = Path.Combine(_folder, "Site.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        Assert.Null(WebDevelopmentService.UserSecretsId(project));

        var web = new WebDevelopmentService(() => null);
        var id = await web.InitUserSecretsAsync(project, TestContext.Current.CancellationToken);

        Assert.Equal(id, WebDevelopmentService.UserSecretsId(project));
        Assert.Equal(id, await web.InitUserSecretsAsync(project, TestContext.Current.CancellationToken)); // idempotent
        var path = WebDevelopmentService.SecretsPath(id);
        Assert.EndsWith(Path.Combine(id, "secrets.json"), path, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "UserSecrets" : "usersecrets", path, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_override_replaces_the_profiles()
    {
        var plan = new LaunchPlan("app", string.Empty, _folder, new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["OTHER"] = "1" }, null);
        var output = new Output.OutputService();
        var runs = new RunService(null!, output, _ => { });
        Assert.Same(plan, runs.Adjust(plan));

        runs.EnvironmentOverride = "Staging";
        var adjusted = runs.Adjust(plan);
        Assert.Equal("Staging", adjusted.Environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Staging", adjusted.Environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("1", adjusted.Environment["OTHER"]);
    }
}
