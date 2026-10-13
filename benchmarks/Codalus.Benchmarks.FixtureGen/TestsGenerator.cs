using System.Globalization;
using System.Text;

namespace Codalus.Benchmarks.FixtureGen;

/// <summary>
/// The Test Explorer fixture (M8): <see cref="DefaultProjects"/> xUnit test projects of <see cref="ClassesPerProject"/>
/// classes with <see cref="TestsPerClass"/> tests each (5,000 tests), every tenth test a theory with two cases.
/// </summary>
internal static class TestsGenerator
{
    public const int DefaultProjects = 20;
    public const int ClassesPerProject = 10;
    public const int TestsPerClass = 25;

    public static int Tests(int projects) => projects * ClassesPerProject * TestsPerClass;

    public static void Write(string outDir, int projects)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(outDir, "Directory.Packages.props"), "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");
        var solution = new StringBuilder("<Solution>\n");
        for (var p = 0; p < projects; p++)
        {
            var name = $"Tests{p:D2}";
            var dir = Path.Combine(outDir, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                  </ItemGroup>
                </Project>

                """);
            for (var c = 0; c < ClassesPerProject; c++)
            {
                File.WriteAllText(Path.Combine(dir, $"Feature{c:D2}Tests.cs"), Class(name, c));
            }

            solution.Append(CultureInfo.InvariantCulture, $"  <Project Path=\"{name}/{name}.csproj\" />\n");
        }

        solution.Append("</Solution>\n");
        File.WriteAllText(Path.Combine(outDir, "Tests5k.slnx"), solution.ToString());
    }

    private static string Class(string project, int index)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"using Xunit;\n\nnamespace {project}.Area{index % 3};\n\npublic class Feature{index:D2}Tests\n{{\n");
        for (var t = 0; t < TestsPerClass; t++)
        {
            if (t % 10 == 9)
            {
                text.Append(CultureInfo.InvariantCulture, $"    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void Case{t:D2}(int value) => Assert.True(value > 0);\n\n");
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture, $"    [Fact]\n    public void Case{t:D2}() => Assert.Equal({t}, {t});\n\n");
            }
        }

        text.Append("}\n");
        return text.ToString();
    }
}
