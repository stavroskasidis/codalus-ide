using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>
/// .NET Framework projects (M43a, ADR 0075): which projects are .NET Framework's and run only on Windows, their C#
/// version and Hot Reload, the installed runtime for Check Environment, and Convert to SDK-style, whose output for the
/// fixture's old-style project is compared with golden files (TestData/NetFramework).
/// </summary>
public sealed class NetFrameworkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-netfx-convert-" + Guid.NewGuid().ToString("N"));

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

    private static ProjectInfo Project(string[] frameworks, bool nonSdk = false) =>
        new("/src/App/App.csproj", "App", ProjectStates.Loaded, null, frameworks, "Exe", [], [], [], true) { IsNonSdkStyle = nonSdk };

    private static string Golden(string name) => Path.Combine(ContainerToolsTests.RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "NetFramework", name);

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "packages")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    /// <summary>Compares with a golden file; a difference writes the actual text next to the test's temp folder and says where.</summary>
    private void AssertGolden(string name, string actual)
    {
        var expected = File.ReadAllText(Golden(name)).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (expected != actual.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            var written = Path.Combine(Path.GetTempPath(), "codalus-golden-" + name);
            File.WriteAllText(written, actual);
            Assert.Fail($"{name} differs from its golden file; the actual text is in {written}.\n{actual}");
        }
    }

    [Theory]
    [InlineData("net48", "4.8", ".NET Framework 4.8")]
    [InlineData("net472", "4.7.2", ".NET Framework 4.7.2")]
    [InlineData("net35", "3.5", ".NET Framework 3.5")]
    [InlineData("net8.0", null, "net8.0")]
    [InlineData("netstandard2.0", null, "netstandard2.0")]
    public void Monikers_name_their_net_framework(string moniker, string? version, string display)
    {
        Assert.Equal(version, NetFrameworkProjects.VersionOf(moniker)?.ToString());
        Assert.Equal(display, NetFrameworkProjects.Display(moniker));
        Assert.Equal(version is not null, NetFrameworkProjects.IsMoniker(moniker));
    }

    [Fact]
    public void Net_framework_projects_run_only_on_windows()
    {
        Assert.True(NetFrameworkProjects.IsNetFramework(Project(["net48"])));
        Assert.True(NetFrameworkProjects.IsNetFramework(Project(["net48", "net472"])));
        Assert.True(NetFrameworkProjects.IsNetFramework(Project(["net472"], nonSdk: true)));
        Assert.False(NetFrameworkProjects.IsNetFramework(Project(["net48", "net8.0"]))); // multi-targeted: .NET runs everywhere
        Assert.False(NetFrameworkProjects.IsNetFramework(Project(["net11.0"])));
        Assert.True(WindowsDesktopProjects.CannotRunOn(Project(["net48"]), windows: false));
        Assert.False(WindowsDesktopProjects.CannotRunOn(Project(["net48"]), windows: true));
        Assert.False(WindowsDesktopProjects.CannotRunOn(Project(["net48", "net8.0"]), windows: false));
    }

    [Fact]
    public void Net_framework_has_csharp_73_and_no_hot_reload()
    {
        Assert.Equal("7.3", DotnetFrameworks.DefaultCSharpVersion("net48"));
        Assert.Equal("7.3", DotnetFrameworks.DefaultCSharpVersion("net35"));
        Assert.Null(DotnetFrameworks.NetCoreVersion("net48"));
        Assert.Equal("Hot Reload needs .NET 6 or later: App targets net48 (.NET Framework).", DotnetFrameworks.HotReloadUnsupportedReason("net48", "App"));
        Assert.Equal("Hot Reload needs .NET 6 or later: App targets netcoreapp3.1.", DotnetFrameworks.HotReloadUnsupportedReason("netcoreapp3.1", "App"));
        Assert.Null(DotnetFrameworks.HotReloadUnsupportedReason("net8.0", "App"));
    }

    [Theory]
    [InlineData(533320, "4.8.1")]
    [InlineData(533325, "4.8.1")]
    [InlineData(528040, "4.8")]
    [InlineData(528449, "4.8")]
    [InlineData(461814, "4.7.2")]
    [InlineData(394802, "4.6.2")]
    [InlineData(378389, "4.5")]
    [InlineData(1000, null)]
    [InlineData(null, null)]
    public void The_release_dword_names_the_installed_version(int? release, string? version) =>
        Assert.Equal(version, NetFrameworkProjects.VersionFromRelease(release)?.ToString());

    [Fact]
    public void Check_environment_reports_the_installed_runtime_against_the_targeted_one()
    {
        Assert.Empty(NetFrameworkProjects.ToolRows([Project(["net11.0"])], 528040, true));

        var found = Assert.Single(NetFrameworkProjects.ToolRows([Project(["net48"]), Project(["net472"], nonSdk: true)], 528040, false));
        Assert.Equal((NetFrameworkProjects.RuntimeToolId, ToolState.Found, "4.8", "4.8"), (found.Id, found.State, found.Version, found.WantedVersion));

        var old = Assert.Single(NetFrameworkProjects.ToolRows([Project(["net481"])], 528040, null));
        Assert.Equal(ToolState.Mismatch, old.State);
        Assert.Contains("targets .NET Framework 4.8.1; 4.8 is installed", old.Detail, StringComparison.Ordinal);

        var none = Assert.Single(NetFrameworkProjects.ToolRows([Project(["net48"])], null, null));
        Assert.Equal(ToolState.Missing, none.State);

        var rows = NetFrameworkProjects.ToolRows([Project(["net35"], nonSdk: true), Project(["net48"])], 533320, false);
        Assert.Equal(2, rows.Count);
        Assert.Equal((NetFrameworkProjects.RuntimeToolId + ":3.5", ToolState.Missing), (rows[1].Id, rows[1].State));
        Assert.Equal(NetFrameworkProjects.DownloadPage, NetFrameworkProjects.PlanFor(rows[0])!.Command);
        Assert.Equal(528040, NetFrameworkProjects.Release(528040));
        Assert.Null(NetFrameworkProjects.Release(null));
    }

    // ------------------------------------------------------------------ Convert to SDK-style

    /// <summary>The fixture's old-style app, with its package restored: the project file and AssemblyInfo.cs match the golden files.</summary>
    [Fact]
    public void The_old_style_fixture_converts_to_the_golden_sdk_style_project()
    {
        var fixture = Path.Combine(_root, "NetFramework");
        Copy(Path.Combine(ContainerToolsTests.RepoRoot, "samples", "NetFramework"), fixture);
        var packages = Path.Combine(fixture, "packages");
        Directory.CreateDirectory(Path.Combine(packages, "Codalus.Sample.Legacy.1.0.0"));
        File.WriteAllText(Path.Combine(packages, "Codalus.Sample.Legacy.1.0.0", "Codalus.Sample.Legacy.nuspec"), "<package><metadata><id>Codalus.Sample.Legacy</id></metadata></package>");
        var project = Path.Combine(fixture, "LegacyApp", "LegacyApp.csproj");

        var conversion = SdkStyleConverter.Convert(project, packages);

        Assert.Equal(project, conversion.ProjectPath);
        AssertGolden("LegacyApp.csproj.golden", conversion.ProjectText);
        var assemblyInfo = Assert.Single(conversion.ChangedFiles);
        Assert.Equal(Path.Combine(fixture, "LegacyApp", "Properties", "AssemblyInfo.cs"), assemblyInfo.Path);
        AssertGolden("AssemblyInfo.cs.golden", assemblyInfo.Text);
        Assert.Equal([Path.Combine(fixture, "LegacyApp", "packages.config")], conversion.DeletedFiles);
        Assert.Contains(conversion.Notes, n => n.StartsWith("packages.config's 1 package(s) became PackageReference items", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.StartsWith("AssemblyTitle, AssemblyDescription, AssemblyCompany, AssemblyProduct, AssemblyCopyright, AssemblyVersion, AssemblyFileVersion moved", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.StartsWith("DebugType full and pdbonly were left out", StringComparison.Ordinal));
    }

    [Fact]
    public void Sdk_style_and_visual_studio_web_projects_are_not_converted()
    {
        var fixture = Path.Combine(ContainerToolsTests.RepoRoot, "samples", "NetFramework");
        var sdk = Assert.Throws<SdkConversionException>(() => SdkStyleConverter.Convert(Path.Combine(fixture, "ConsoleApp", "ConsoleApp.csproj")));
        Assert.Equal("ConsoleApp.csproj is SDK-style already.", sdk.Message);
        var web = Assert.Throws<SdkConversionException>(() => SdkStyleConverter.Convert(Path.Combine(fixture, "LegacyWebApp", "LegacyWebApp.csproj")));
        Assert.StartsWith("LegacyWebApp imports Visual Studio's own targets ($(VSToolsPath))", web.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Windows Forms app with the rest of what old-style projects carry: strongly typed resources and settings (kept as
    /// updates), a form's designer and .resx (nested by convention), a package that another depends on, an x86
    /// configuration, a wildcard version, ClickOnce's defaults, a test project's import of Visual Studio's targets
    /// where they exist, a folder with another project, a package's build import and its check target.
    /// </summary>
    [Fact]
    public void A_windows_forms_project_keeps_what_the_sdk_doesnt_do_by_itself()
    {
        var folder = Path.Combine(_root, "Forms");
        Directory.CreateDirectory(Path.Combine(folder, "Properties"));
        Directory.CreateDirectory(Path.Combine(folder, "Nested"));
        File.WriteAllText(Path.Combine(folder, "Nested", "Nested.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(folder, "Nested", "Other.cs"), "class Other { }");
        foreach (var file in new[] { "Program.cs", "MainForm.cs", "MainForm.Designer.cs", "MainForm.resx", "Properties/Resources.resx", "Properties/Resources.Designer.cs", "Properties/Settings.settings", "Properties/Settings.Designer.cs", "Logo.ico" })
        {
            File.WriteAllText(Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar)), string.Empty);
        }

        File.WriteAllText(Path.Combine(folder, "Properties", "AssemblyInfo.cs"), "using System.Reflection;\n\n[assembly: AssemblyTitle(\"Forms\")]\n[assembly: AssemblyVersion(\"2.0.*\")]\n[assembly: AssemblyInformationalVersion(Constants.Version)]\n");
        File.WriteAllText(Path.Combine(folder, "packages.config"), "<packages><package id=\"Polly\" version=\"7.2.4\" targetFramework=\"net462\" /><package id=\"Polly.Core\" version=\"8.0.0\" targetFramework=\"net462\" /><package id=\"StyleCop.Analyzers\" version=\"1.1.118\" developmentDependency=\"true\" /></packages>");
        var packages = Path.Combine(_root, "packages");
        Directory.CreateDirectory(Path.Combine(packages, "Polly.7.2.4"));
        File.WriteAllText(Path.Combine(packages, "Polly.7.2.4", "Polly.nuspec"), "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>Polly</id><dependencies><group targetFramework=\".NETFramework4.6.2\"><dependency id=\"Polly.Core\" version=\"8.0.0\" /></group></dependencies></metadata></package>");
        var project = Path.Combine(folder, "Forms.csproj");
        File.WriteAllText(project, """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="..\packages\StyleCop.Analyzers.1.1.118\build\StyleCop.Analyzers.props" Condition="Exists('..\packages\StyleCop.Analyzers.1.1.118\build\StyleCop.Analyzers.props')" />
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <RootNamespace>Contoso.Forms</RootNamespace>
                <AssemblyName>Forms</AssemblyName>
                <TargetFrameworkVersion>v4.6.2</TargetFrameworkVersion>
                <ApplicationIcon>Logo.ico</ApplicationIcon>
                <PublishUrl>publish\</PublishUrl>
                <BootstrapperEnabled>true</BootstrapperEnabled>
                <VisualStudioVersion Condition="'$(VisualStudioVersion)' == ''">15.0</VisualStudioVersion>
              </PropertyGroup>
              <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|x86' ">
                <PlatformTarget>x86</PlatformTarget>
                <OutputPath>bin\x86\Debug\</OutputPath>
                <DefineConstants>DEBUG;TRACE</DefineConstants>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Polly, Version=7.0.0.0, Culture=neutral, PublicKeyToken=c8a3ffc3f8f825cc">
                  <HintPath>..\packages\Polly.7.2.4\lib\net461\Polly.dll</HintPath>
                </Reference>
                <Reference Include="System" />
                <Reference Include="System.Drawing" />
                <Reference Include="System.Windows.Forms" />
                <Reference Include="System.Deployment" />
                <Reference Include="System.Net.Http" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="MainForm.cs">
                  <SubType>Form</SubType>
                </Compile>
                <Compile Include="MainForm.Designer.cs">
                  <DependentUpon>MainForm.cs</DependentUpon>
                </Compile>
                <Compile Include="Program.cs" />
                <Compile Include="Properties\AssemblyInfo.cs" />
                <Compile Include="..\Shared\Version.cs">
                  <Link>Properties\Version.cs</Link>
                </Compile>
                <EmbeddedResource Include="MainForm.resx">
                  <DependentUpon>MainForm.cs</DependentUpon>
                </EmbeddedResource>
                <EmbeddedResource Include="Properties\Resources.resx">
                  <Generator>ResXFileCodeGenerator</Generator>
                  <LastGenOutput>Resources.Designer.cs</LastGenOutput>
                  <SubType>Designer</SubType>
                </EmbeddedResource>
                <Compile Include="Properties\Resources.Designer.cs">
                  <AutoGen>True</AutoGen>
                  <DependentUpon>Resources.resx</DependentUpon>
                </Compile>
                <None Include="packages.config" />
                <None Include="Properties\Settings.settings">
                  <Generator>SettingsSingleFileGenerator</Generator>
                  <LastGenOutput>Settings.Designer.cs</LastGenOutput>
                </None>
                <Compile Include="Properties\Settings.Designer.cs">
                  <AutoGen>True</AutoGen>
                  <DependentUpon>Settings.settings</DependentUpon>
                  <DesignTimeSharedInput>True</DesignTimeSharedInput>
                </Compile>
                <Content Include="Logo.ico" />
              </ItemGroup>
              <ItemGroup>
                <BootstrapperPackage Include=".NETFramework,Version=v4.6.2">
                  <Visible>False</Visible>
                </BootstrapperPackage>
                <Analyzer Include="..\packages\StyleCop.Analyzers.1.1.118\analyzers\dotnet\cs\StyleCop.Analyzers.dll" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
              <Import Project="$(VSToolsPath)\TeamTest\Microsoft.TestTools.targets" Condition="Exists('$(VSToolsPath)\TeamTest\Microsoft.TestTools.targets')" />
              <Target Name="EnsureNuGetPackageBuildImports" BeforeTargets="PrepareForBuild">
                <Error Condition="!Exists('..\packages\StyleCop.Analyzers.1.1.118\build\StyleCop.Analyzers.props')" Text="Missing" />
              </Target>
              <Target Name="AfterBuild">
                <Message Text="Built" />
              </Target>
            </Project>
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        var conversion = SdkStyleConverter.Convert(project, packages);
        var text = conversion.ProjectText;

        Assert.Contains("\r\n", text, StringComparison.Ordinal); // the file's own line endings
        Assert.StartsWith("<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <OutputType>WinExe</OutputType>\r\n    <TargetFramework>net462</TargetFramework>\r\n    <RootNamespace>Contoso.Forms</RootNamespace>\r\n    <UseWindowsForms>true</UseWindowsForms>\r\n", text, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Logo.ico</ApplicationIcon>", text, StringComparison.Ordinal);
        Assert.Contains("<AssemblyTitle>Forms</AssemblyTitle>", text, StringComparison.Ordinal);
        Assert.Contains("<GenerateAssemblyVersionAttribute>false</GenerateAssemblyVersionAttribute>", text, StringComparison.Ordinal);
        Assert.Contains("<Deterministic>false</Deterministic>", text, StringComparison.Ordinal);
        Assert.Contains("<GenerateAssemblyInformationalVersionAttribute>false</GenerateAssemblyInformationalVersionAttribute>", text, StringComparison.Ordinal);
        Assert.Contains("<Platforms>AnyCPU;x86</Platforms>", text, StringComparison.Ordinal);
        Assert.Contains("<PropertyGroup Condition=\"'$(Configuration)|$(Platform)' == 'Debug|x86'\">\r\n    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>\r\n  </PropertyGroup>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishUrl", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualStudioVersion", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("MainForm", text, StringComparison.Ordinal); // the globs and the IDE's nesting do it
        Assert.Contains("<EmbeddedResource Update=\"Properties\\Resources.resx\">\r\n      <Generator>ResXFileCodeGenerator</Generator>\r\n      <LastGenOutput>Resources.Designer.cs</LastGenOutput>\r\n    </EmbeddedResource>", text, StringComparison.Ordinal);
        Assert.Contains("<Compile Update=\"Properties\\Resources.Designer.cs\">", text, StringComparison.Ordinal);
        Assert.Contains("<None Update=\"Properties\\Settings.settings\">", text, StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"..\\Shared\\Version.cs\">\r\n      <Link>Properties\\Version.cs</Link>\r\n    </Compile>", text, StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"Logo.ico\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Compile Remove=\"Nested\\**\" />", text, StringComparison.Ordinal);
        Assert.Contains("<None Remove=\"Nested\\**\" />", text, StringComparison.Ordinal);
        Assert.Contains("<Reference Include=\"System.Net.Http\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Deployment", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HintPath", text, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Polly\" Version=\"7.2.4\" />", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Polly.Core", text, StringComparison.Ordinal); // Polly depends on it
        Assert.Contains("<PackageReference Include=\"StyleCop.Analyzers\" Version=\"1.1.118\">\r\n      <PrivateAssets>all</PrivateAssets>\r\n    </PackageReference>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Analyzer Include", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BootstrapperPackage", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Import", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureNuGetPackageBuildImports", text, StringComparison.Ordinal);
        Assert.Contains("<Target Name=\"AfterBuild\">", text, StringComparison.Ordinal);
        Assert.DoesNotContain("xmlns", text, StringComparison.Ordinal);
        Assert.EndsWith("</Project>\r\n", text, StringComparison.Ordinal);

        var info = Assert.Single(conversion.ChangedFiles);
        Assert.Equal("using System.Reflection;\n\n[assembly: AssemblyVersion(\"2.0.*\")]\n[assembly: AssemblyInformationalVersion(Constants.Version)]\n", info.Text);
        Assert.Contains(conversion.Notes, n => n.Contains("Polly.Core was left out", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.Contains("ClickOnce", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.Contains("The target AfterBuild is kept", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.Contains("StyleCop.Analyzers.props", StringComparison.Ordinal));
        Assert.Contains(conversion.Notes, n => n.Contains("Microsoft.TestTools.targets was left out", StringComparison.Ordinal));
    }
}
