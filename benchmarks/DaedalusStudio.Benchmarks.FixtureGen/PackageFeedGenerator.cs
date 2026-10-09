using System.Diagnostics;

namespace DaedalusStudio.Benchmarks.FixtureGen;

/// <summary>
/// The local package feed of <c>samples/Packages</c> (M9, ADR 0012): small packages built with <c>dotnet pack</c> from
/// generated projects. They cover a README, a licence expression, licence acceptance, prerelease versions, several
/// versions of one package and a package dependency (for transitive packages and "why"). The <c>.nupkg</c> files are
/// committed; run this again only to change them.
/// </summary>
internal static class PackageFeedGenerator
{
    private sealed record Package(
        string Id,
        string Version,
        string Description,
        string Code,
        string? Dependency = null,
        bool RequireLicenseAcceptance = false,
        string License = "MIT",
        string? Readme = null);

    private static readonly Package[] s_packages =
    [
        new("Daedalus.Sample.Text", "1.0.0", "Text helpers used by Daedalus.Sample.Greeting.", "public static class Text { public static string Shout(string s) => s.ToUpperInvariant() + \"!\"; }"),
        new("Daedalus.Sample.Greeting", "1.0.0", "Greets people. A fixture package of Daedalus Studio.", "public static class Greeter { public static string Hello(string name) => \"Hello, \" + name; }", Readme: "# Daedalus.Sample.Greeting\n\nGreets people.\n\n```csharp\nGreeter.Hello(\"Ada\");\n```\n"),
        new("Daedalus.Sample.Greeting", "1.1.0", "Greets people, loudly. A fixture package of Daedalus Studio.", "public static class Greeter { public static string Hello(string name) => \"Hello, \" + name; public static string Shout(string name) => Daedalus.Sample.Text.Text.Shout(Hello(name)); }", Dependency: "Daedalus.Sample.Text/1.0.0", Readme: "# Daedalus.Sample.Greeting\n\nGreets people, **loudly** in 1.1.\n\n- `Greeter.Hello`\n- `Greeter.Shout`\n"),
        new("Daedalus.Sample.Greeting", "2.0.0-beta.1", "Greets people (preview).", "public static class Greeter { public static string Hello(string name, string greeting = \"Hello\") => greeting + \", \" + name; }", Readme: "# Daedalus.Sample.Greeting 2.0 preview\n"),
        new("Daedalus.Sample.Licensed", "1.0.0", "A package whose licence must be accepted before it is installed.", "public static class Licensed { public const int Answer = 42; }", RequireLicenseAcceptance: true, License: "Apache-2.0"),
    ];

    public static void Write(string feed)
    {
        Directory.CreateDirectory(feed);
        var work = Path.Combine(Path.GetTempPath(), "daedalus-feedgen-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var package in s_packages)
            {
                var dir = Path.Combine(work, package.Id + "." + package.Version);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(work, "Directory.Build.props"), "<Project />");
                File.WriteAllText(Path.Combine(work, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(dir, "Code.cs"), $"namespace {package.Id};\n\n{package.Code}\n");
                var readme = package.Readme is null ? string.Empty : "    <PackageReadmeFile>README.md</PackageReadmeFile>\n";
                if (package.Readme is not null)
                {
                    File.WriteAllText(Path.Combine(dir, "README.md"), package.Readme);
                }

                var dependency = package.Dependency?.Split('/') is [var id, var version]
                    ? $"  <ItemGroup>\n    <PackageReference Include=\"{id}\" Version=\"{version}\" />\n  </ItemGroup>\n"
                    : string.Empty;
                File.WriteAllText(Path.Combine(dir, "Package.csproj"), $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net8.0</TargetFramework>
                        <PackageId>{package.Id}</PackageId>
                        <AssemblyName>{package.Id}</AssemblyName>
                        <Version>{package.Version}</Version>
                        <Authors>Daedalus Studio fixtures</Authors>
                        <Description>{package.Description}</Description>
                        <PackageTags>daedalus fixture sample</PackageTags>
                        <PackageLicenseExpression>{package.License}</PackageLicenseExpression>
                        <PackageRequireLicenseAcceptance>{(package.RequireLicenseAcceptance ? "true" : "false")}</PackageRequireLicenseAcceptance>
                        <PackageProjectUrl>https://example.invalid/{package.Id}</PackageProjectUrl>
                        <Deterministic>true</Deterministic>
                        <IncludeSymbols>false</IncludeSymbols>
                        <GenerateDocumentationFile>false</GenerateDocumentationFile>
                    {readme}  </PropertyGroup>
                    {(package.Readme is null ? string.Empty : "  <ItemGroup>\n    <None Include=\"README.md\" Pack=\"true\" PackagePath=\"\\\" />\n  </ItemGroup>\n")}{dependency}</Project>
                    """);
                Run(dir, "pack", "-c", "Release", "-o", feed, "--source", feed, "--source", "https://api.nuget.org/v3/index.json");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void Run(string directory, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', args)} failed in {directory}.");
        }
    }
}
