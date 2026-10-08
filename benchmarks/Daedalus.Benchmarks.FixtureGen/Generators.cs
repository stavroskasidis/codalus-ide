using System.Globalization;
using System.Text;

namespace Daedalus.Benchmarks.FixtureGen;

internal static class LargeFileGenerator
{
    public const int DefaultLines = 100_000;
    public const string FileName = "Large.cs";

    public static string Generate(int targetLines, int seed = 42)
    {
        var writer = new CodeWriter(seed);
        writer.FileHeader("Daedalus.Fixtures.Large");
        var index = 0;
        while (writer.Lines < targetLines - 150)
        {
            writer.Type(index++);
        }

        // Pad with comment lines to hit the exact target.
        while (writer.Lines < targetLines)
        {
            writer.Line($"// padding line {writer.Lines.ToString(CultureInfo.InvariantCulture)}");
        }

        return writer.ToString();
    }

    /// <summary>Writes the file plus a project so that IDEs can load it with full semantic services.</summary>
    public static void WriteProject(string outDir, int lines)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, FileName), Generate(lines));
        File.WriteAllText(Path.Combine(outDir, "Large100k.csproj"), SolutionGenerator.ProjectXml(isExe: false, references: []));
    }
}

internal static class SolutionGenerator
{
    public const int DefaultProjects = 50;
    private const int FilesPerProject = 20;

    public static void Write(string outDir, int projectCount, int seed = 7)
    {
        Directory.CreateDirectory(outDir);
        var random = new Random(seed);
        var projects = new List<(string Name, string Guid, string RelativePath)>();

        for (var p = 0; p < projectCount; p++)
        {
            // Every 5th project is a console app; each project references up to 3 earlier ones (a DAG).
            var isExe = p % 5 == 4;
            var name = isExe ? $"App{p:D2}" : $"Lib{p:D2}";
            var references = Enumerable.Range(0, p).OrderBy(_ => random.Next()).Take(Math.Min(p, 3))
                .Where(r => r % 5 != 4)
                .Select(r => $"..\\{projects[r].Name}\\{projects[r].Name}.csproj")
                .ToArray();

            var projectDir = Path.Combine(outDir, name);
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, $"{name}.csproj"), ProjectXml(isExe, references));
            for (var f = 0; f < FilesPerProject; f++)
            {
                var writer = new CodeWriter(seed * 1000 + p * 100 + f);
                writer.FileHeader($"Daedalus.Fixtures.{name}");
                writer.Type(f);
                File.WriteAllText(Path.Combine(projectDir, $"Type{f:D2}.cs"), writer.ToString());
            }

            if (isExe)
            {
                File.WriteAllText(Path.Combine(projectDir, "Program.cs"), "System.Console.WriteLine(\"fixture\");\n");
            }

            var guid = new Guid(MD5Of(name)).ToString("B").ToUpperInvariant();
            projects.Add((name, guid, $"{name}\\{name}.csproj"));
        }

        File.WriteAllText(Path.Combine(outDir, "Solution50.sln"), SlnXml(projects));
        File.WriteAllText(Path.Combine(outDir, "Solution50.slnx"), SlnxXml(projects));
    }

    public static string ProjectXml(bool isExe, string[] references)
    {
        var sb = new StringBuilder();
        sb.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
        sb.Append("  <PropertyGroup>\n");
        if (isExe)
        {
            sb.Append("    <OutputType>Exe</OutputType>\n");
        }

        sb.Append("    <TargetFramework>net10.0</TargetFramework>\n");
        sb.Append("    <Nullable>enable</Nullable>\n");
        sb.Append("  </PropertyGroup>\n");
        if (references.Length > 0)
        {
            sb.Append("  <ItemGroup>\n");
            foreach (var reference in references)
            {
                sb.Append("    <ProjectReference Include=\"").Append(reference).Append("\" />\n");
            }

            sb.Append("  </ItemGroup>\n");
        }

        sb.Append("</Project>\n");
        return sb.ToString();
    }

    private static string SlnXml(List<(string Name, string Guid, string RelativePath)> projects)
    {
        const string CSharpProjectType = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
        var sb = new StringBuilder();
        sb.Append("\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\n");
        foreach (var (name, guid, path) in projects)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Project(\"{CSharpProjectType}\") = \"{name}\", \"{path}\", \"{guid}\"\r\nEndProject\r\n");
        }

        sb.Append("Global\r\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n");
        sb.Append("\t\tDebug|Any CPU = Debug|Any CPU\r\n\t\tRelease|Any CPU = Release|Any CPU\r\n\tEndGlobalSection\r\n");
        sb.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n");
        foreach (var (_, guid, _) in projects)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                sb.Append(CultureInfo.InvariantCulture, $"\t\t{guid}.{config}|Any CPU.ActiveCfg = {config}|Any CPU\r\n");
                sb.Append(CultureInfo.InvariantCulture, $"\t\t{guid}.{config}|Any CPU.Build.0 = {config}|Any CPU\r\n");
            }
        }

        sb.Append("\tEndGlobalSection\r\nEndGlobal\r\n");
        return sb.ToString();
    }

    private static string SlnxXml(List<(string Name, string Guid, string RelativePath)> projects)
    {
        var sb = new StringBuilder("<Solution>\n");
        foreach (var (_, _, path) in projects)
        {
            sb.Append("  <Project Path=\"").Append(path.Replace('\\', '/')).Append("\" />\n");
        }

        return sb.Append("</Solution>\n").ToString();
    }

#pragma warning disable CA5351 // MD5 is used for a stable, non-security GUID only.
    private static byte[] MD5Of(string value) => System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value));
#pragma warning restore CA5351
}
