using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// ADR 0114: a project says which of its target frameworks run on CoreCLR where Mono used to be the runtime
/// (<c>UseMonoRuntime</c> evaluated to <c>false</c>, as .NET 11's iOS workload sets it, or <c>_AndroidRuntime</c> to
/// <c>CoreCLR</c>, as its Android workload does: ADR 0115), per framework. Projects that set neither, and frameworks
/// that keep Mono, aren't listed. The properties are set here by the project itself, so no workload is needed.
/// </summary>
public sealed class CoreClrFrameworksTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-coreclr-" + Guid.NewGuid().ToString("N"));
    private SolutionService _solutions = null!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public async ValueTask InitializeAsync()
    {
        foreach (var (name, body) in new[]
                 {
                     ("Both", "<TargetFrameworks>net10.0;net11.0</TargetFrameworks><UseMonoRuntime Condition=\"'$(TargetFramework)' == 'net11.0'\">false</UseMonoRuntime><UseMonoRuntime Condition=\"'$(TargetFramework)' == 'net10.0'\">true</UseMonoRuntime>"),
                     ("One", "<TargetFramework>net11.0</TargetFramework><UseMonoRuntime>False</UseMonoRuntime>"),
                     ("Plain", "<TargetFramework>net11.0</TargetFramework>"),

                     // The Android workload: UseMonoRuntime stays empty, and _AndroidRuntime has the runtime.
                     ("Droid", "<TargetFrameworks>net10.0;net11.0</TargetFrameworks><_AndroidRuntime Condition=\"'$(TargetFramework)' == 'net11.0'\">CoreCLR</_AndroidRuntime><_AndroidRuntime Condition=\"'$(TargetFramework)' == 'net10.0'\">MonoVM</_AndroidRuntime>"),
                     ("Aot", "<TargetFramework>net11.0</TargetFramework><_AndroidRuntime>NativeAOT</_AndroidRuntime>"),
                 })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, name + ".csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{body}</PropertyGroup></Project>");
        }

        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution><Project Path=\"Both/Both.csproj\" /><Project Path=\"One/One.csproj\" /><Project Path=\"Plain/Plain.csproj\" /><Project Path=\"Droid/Droid.csproj\" /><Project Path=\"Aot/Aot.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _solutions = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath);
        await _solutions.OpenAsync(Path.Combine(_root, "App.slnx"), trusted: true, restore: false, Timeout());
        var deadline = DateTime.UtcNow + s_timeout;
        while (!(_solutions.Progress is { Completed: true } && _solutions.Projects.Count == 5))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out evaluating.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _solutions.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void The_frameworks_that_turn_Mono_off_are_listed_each_by_its_own_evaluation()
    {
        string[] Of(string name) => _solutions.Projects[Path.Combine(_root, name, name + ".csproj")].CoreClrFrameworks;
        Assert.Equal(["net11.0"], Of("Both"));
        Assert.Equal(["net11.0"], Of("One"));
        Assert.Empty(Of("Plain"));
        Assert.Equal(["net11.0"], Of("Droid"));
        Assert.Empty(Of("Aot"));
    }
}
