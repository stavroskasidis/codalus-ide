using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>New Project's .NET Framework templates (M43a, ADR 0075, ADR 0082): SDK-style net48 projects in C# 7.3 that build on every OS.</summary>
public sealed class NetFrameworkTemplatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-netfx-templates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void The_templates_write_sdk_style_net_framework_projects()
    {
        var files = NetFrameworkTemplates.Files(NetFrameworkTemplates.ConsoleShortName, "Hello.App", "net472");
        Assert.Equal(["Hello.App.csproj", "Program.cs", "App.config"], files.Select(f => f.Path));
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net472</TargetFramework>\n  </PropertyGroup>\n\n</Project>\n", files[0].Text);
        Assert.Contains("namespace Hello.App\n{", files[1].Text, StringComparison.Ordinal);
        Assert.Contains("private static void Main(string[] args)", files[1].Text, StringComparison.Ordinal);
        Assert.Contains("sku=\".NETFramework,Version=v4.7.2\"", files[2].Text, StringComparison.Ordinal);

        var library = NetFrameworkTemplates.Files(NetFrameworkTemplates.LibraryShortName, "1Lib-Core", "net48");
        Assert.Equal(["1Lib-Core.csproj", "Class1.cs"], library.Select(f => f.Path));
        Assert.Contains("<RootNamespace>_1Lib_Core</RootNamespace>", library[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OutputType", library[0].Text, StringComparison.Ordinal);
        Assert.Equal("net48", NetFrameworkTemplates.Frameworks[0]);

        Assert.True(NetFrameworkTemplates.Is(NetFrameworkTemplates.ConsoleShortName));
        Assert.False(NetFrameworkTemplates.Is("console"));
        Assert.Equal(("ConsoleApp1", "ClassLibrary1"), (NetFrameworkTemplates.All[0].DefaultName, NetFrameworkTemplates.All[1].DefaultName));
    }

    /// <summary>The console template's project builds with the SDK on this OS (the reference assemblies package from nuget.org, as other restores' targeting packs).</summary>
    [Fact]
    public async Task The_console_template_builds()
    {
        var directory = Path.Combine(_root, "ConsoleApp1");
        Directory.CreateDirectory(directory);
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project />");
        foreach (var (path, text) in NetFrameworkTemplates.Files(NetFrameworkTemplates.ConsoleShortName, "ConsoleApp1", "net48"))
        {
            File.WriteAllText(Path.Combine(directory, path), text);
        }

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "build", "--nologo", "-v:q" })
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.True(File.Exists(Path.Combine(directory, "bin", "Debug", "net48", "ConsoleApp1.exe")));
        Assert.True(File.Exists(Path.Combine(directory, "bin", "Debug", "net48", "ConsoleApp1.exe.config")));
    }
}
