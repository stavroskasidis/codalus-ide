using System.Globalization;
using System.Text;

namespace DaedalusStudio.Benchmarks.FixtureGen;

internal static class LargeFileGenerator
{
    public const int DefaultLines = 100_000;
    public const string FileName = "Large.cs";

    public static string Generate(int targetLines, int seed = 42)
    {
        var writer = new CodeWriter(seed);
        writer.FileHeader("DaedalusStudio.Fixtures.Large");
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

/// <summary>100k-line JSON and XML files for the markup editing budgets (M11, ADR 0016).</summary>
internal static class MarkupGenerator
{
    public static void Write(string outDir, int lines)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "Large.json"), Json(lines));
        File.WriteAllText(Path.Combine(outDir, "Large.xml"), Xml(lines));
    }

    // Each record is 10 lines; the array brackets add 2.
    public static string Json(int lines)
    {
        var builder = new StringBuilder("[\n");
        var records = Math.Max(1, (lines - 2) / 10);
        for (var i = 0; i < records; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            builder.Append("  {\n")
                .Append("    \"id\": ").Append(n).Append(",\n")
                .Append("    \"name\": \"item ").Append(n).Append("\",\n")
                .Append("    \"enabled\": ").Append(i % 3 == 0 ? "true" : "false").Append(",\n")
                .Append("    \"ratio\": ").Append((i % 1000 / 7.0).ToString("F3", CultureInfo.InvariantCulture)).Append(",\n")
                .Append("    \"tags\": [\"alpha\", \"beta\", \"gamma\"],\n")
                .Append("    \"parent\": ").Append(i == 0 ? "null" : (i - 1).ToString(CultureInfo.InvariantCulture)).Append(",\n")
                .Append("    \"description\": \"generated record number ").Append(n).Append(" for the markup benchmark\",\n")
                .Append("    \"kind\": \"").Append((i % 4) switch { 0 => "file", 1 => "folder", 2 => "link", _ => "other" }).Append("\"\n")
                .Append(i == records - 1 ? "  }\n" : "  },\n");
        }

        return builder.Append("]\n").ToString();
    }

    // Each item is 10 lines; the declaration and root add 3.
    public static string Xml(int lines)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<catalog>\n");
        var records = Math.Max(1, (lines - 3) / 10);
        for (var i = 0; i < records; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            builder.Append("  <item id=\"").Append(n).Append("\" enabled=\"").Append(i % 3 == 0 ? "true" : "false").Append("\">\n")
                .Append("    <!-- generated record ").Append(n).Append(" -->\n")
                .Append("    <name>item ").Append(n).Append("</name>\n")
                .Append("    <ratio>").Append((i % 1000 / 7.0).ToString("F3", CultureInfo.InvariantCulture)).Append("</ratio>\n")
                .Append("    <tags>\n")
                .Append("      <tag>alpha</tag>\n")
                .Append("      <tag>beta</tag>\n")
                .Append("    </tags>\n")
                .Append("    <description>generated record number ").Append(n).Append(" for the markup benchmark</description>\n")
                .Append("  </item>\n");
        }

        return builder.Append("</catalog>\n").ToString();
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
                writer.FileHeader($"DaedalusStudio.Fixtures.{name}");
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
