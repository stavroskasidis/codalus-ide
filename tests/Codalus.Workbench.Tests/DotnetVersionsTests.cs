using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>M30 (ADR 0025): the build host's runtime for a newer SDK, and the support status of SDKs.</summary>
public sealed class DotnetVersionsTests
{
    [Fact]
    public void A_newer_sdk_rolls_the_build_host_forward_and_a_preview_allows_prerelease_runtimes()
    {
        Assert.Null(BuildHostRuntime.EnvironmentFor("10.0.401", 10));
        Assert.Null(BuildHostRuntime.EnvironmentFor("8.0.425", 10));
        Assert.Null(BuildHostRuntime.EnvironmentFor(null, 10));
        Assert.Null(BuildHostRuntime.EnvironmentFor("garbage", 10));

        var release = BuildHostRuntime.EnvironmentFor("11.0.100", 10)!;
        Assert.Equal("LatestMajor", release[BuildHostRuntime.RollForward]);
        Assert.False(release.ContainsKey(BuildHostRuntime.RollForwardToPrerelease));

        var preview = BuildHostRuntime.EnvironmentFor("11.0.100-rc.1.26425.128", 10)!;
        Assert.Equal("LatestMajor", preview[BuildHostRuntime.RollForward]);
        Assert.Equal("1", preview[BuildHostRuntime.RollForwardToPrerelease]);
    }

    [Fact]
    public void Only_an_sdk_of_a_newer_dotnet_needs_the_check()
    {
        var root = Path.Combine(Path.GetTempPath(), "codalus-dotnet-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sdk", "10.0.401"));
            Directory.CreateDirectory(Path.Combine(root, "sdk", "9.0.318"));
            Assert.False(BuildHostRuntime.HasNewerSdk(root, 10));
            Directory.CreateDirectory(Path.Combine(root, "sdk", "11.0.100-rc.1.26425.128"));
            Assert.True(BuildHostRuntime.HasNewerSdk(root, 10));
            Assert.False(BuildHostRuntime.HasNewerSdk(Path.Combine(root, "missing"), 10));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Sdks_are_marked_by_their_support_lifecycle()
    {
        var today = new DateOnly(2026, 10, 10);
        Assert.Equal(("Supported until 2028-11-14 (.NET 10.0 LTS)", false), DotnetSupport.Status("10.0.401", today));
        Assert.Equal(("Supported until 2026-11-10 (.NET 8.0 LTS)", false), DotnetSupport.Status("8.0.425", today));
        Assert.Equal(("Out of support since 2026-11-10 (.NET 9.0)", true), DotnetSupport.Status("9.0.318", new DateOnly(2026, 11, 11)));
        Assert.Equal(("Out of support since 2024-11-12 (.NET 6.0)", true), DotnetSupport.Status("6.0.428", today));
        Assert.Equal(("Out of support since 2021-08-21 (.NET 2.1)", true), DotnetSupport.Status("2.1.818", today));
        Assert.Equal(("Preview of .NET 11.0 (STS)", false), DotnetSupport.Status("11.0.100-rc.1.26425.128", today));
        Assert.Equal((".NET 11.0 (STS)", false), DotnetSupport.Status("11.0.100", today));
        Assert.Equal(("Preview of .NET 12.0", false), DotnetSupport.Status("12.0.100-preview.1", today));
    }
    [Fact]
    public void Frameworks_are_labeled_by_their_support()
    {
        var today = new DateOnly(2026, 10, 21);
        Assert.Equal(".NET 10.0 (Long Term Support)", DotnetSupport.FrameworkLabel("net10.0", preview: false, today));
        Assert.Equal(".NET 11.0 (Standard Term Support)", DotnetSupport.FrameworkLabel("net11.0", preview: false, today));
        Assert.Equal(".NET 11.0 (Preview)", DotnetSupport.FrameworkLabel("net11.0", preview: true, today));
        Assert.Equal(".NET 9.0 (Standard Term Support)", DotnetSupport.FrameworkLabel("net9.0", preview: false, today));
        Assert.Equal(".NET 9.0 (Out of support)", DotnetSupport.FrameworkLabel("net9.0", preview: false, new DateOnly(2026, 11, 11)));
        Assert.Equal(".NET 7.0 (Out of support)", DotnetSupport.FrameworkLabel("net7.0", preview: false, today));
        Assert.Equal(".NET Core 3.1 (Out of support)", DotnetSupport.FrameworkLabel("netcoreapp3.1", preview: false, today));
        Assert.Equal(".NET 12.0", DotnetSupport.FrameworkLabel("net12.0", preview: false, today)); // not in the table yet
        Assert.Equal(".NET Standard 2.1", DotnetSupport.FrameworkLabel("netstandard2.1", preview: false, today));
        Assert.Equal(".NET Framework 4.8", DotnetSupport.FrameworkLabel("net48", preview: false, today));
        Assert.Equal(".NET Framework 4.7.2", DotnetSupport.FrameworkLabel("net472", preview: false, today));
        Assert.Equal("uap10.0", DotnetSupport.FrameworkLabel("uap10.0", preview: false, today));
    }
}
