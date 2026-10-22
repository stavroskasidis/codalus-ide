using System.Text;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>
/// New Project's .NET Framework templates (M43a, ADR 0075): SDK-style console apps and class libraries for <c>net48</c>
/// (or another .NET Framework 4.x), written by the IDE itself. The SDK's <c>console</c> and <c>classlib</c> templates
/// don't target .NET Framework, and forcing them (<c>--target-framework-override net48</c>) gives projects that don't
/// compile there: top-level statements, implicit usings and nullable reference types need C# 8 to 10, and .NET
/// Framework's is 7.3. They build on every OS (the reference assemblies packages) and run on Windows. New Project lists
/// them as rows of their own (<see cref="ProjectTemplateKind.NetFramework"/>, ADR 0082).
/// </summary>
public static class NetFrameworkTemplates
{
    public const string ConsoleShortName = "netfx-console";

    public const string LibraryShortName = "netfx-classlib";

    /// <summary>The templates in the display language: short name, name, description and default project name.</summary>
    public static IReadOnlyList<(string ShortName, string Name, string Description, string DefaultName)> All =>
    [
        (ConsoleShortName, Localizer.Get("templates.netfx.console", ".NET Framework Console App"),
            Localizer.Get("templates.netfx.console.description", "An SDK-style command-line application for .NET Framework 4.x, in C# 7.3. It builds on every OS and runs on Windows."), "ConsoleApp1"),
        (LibraryShortName, Localizer.Get("templates.netfx.classlib", ".NET Framework Class Library"),
            Localizer.Get("templates.netfx.classlib.description", "An SDK-style class library for .NET Framework 4.x, in C# 7.3. It builds on every OS; its code runs on Windows."), "ClassLibrary1"),
    ];

    /// <summary>The frameworks New Project offers, the default first.</summary>
    public static readonly string[] Frameworks = ["net48", "net481", "net472", "net462"];

    public static bool Is(string shortName) => shortName is ConsoleShortName or LibraryShortName;

    /// <summary>The files of a new project (paths relative to its folder) with LF line endings.</summary>
    public static IReadOnlyList<(string Path, string Text)> Files(string shortName, string name, string framework)
    {
        var console = shortName == ConsoleShortName;
        var ns = Namespace(name);
        var project = new StringBuilder("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n");
        if (console)
        {
            project.Append("    <OutputType>Exe</OutputType>\n");
        }

        project.Append("    <TargetFramework>").Append(framework).Append("</TargetFramework>\n");
        if (ns != name)
        {
            project.Append("    <RootNamespace>").Append(ns).Append("</RootNamespace>\n");
        }

        project.Append("  </PropertyGroup>\n\n</Project>\n");
        var files = new List<(string, string)> { (name + ".csproj", project.ToString()) };
        if (console)
        {
            files.Add(("Program.cs", $$"""
                using System;

                namespace {{ns}}
                {
                    internal class Program
                    {
                        private static void Main(string[] args)
                        {
                            Console.WriteLine("Hello, World!");
                        }
                    }
                }

                """));
            files.Add(("App.config", $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <startup>
                    <supportedRuntime version="v4.0" sku=".NETFramework,Version={{VersionOf(framework)}}" />
                  </startup>
                </configuration>

                """));
        }
        else
        {
            files.Add(("Class1.cs", $$"""
                namespace {{ns}}
                {
                    public class Class1
                    {
                    }
                }

                """));
        }

        return files;
    }

    /// <summary><c>net472</c> → <c>v4.7.2</c>.</summary>
    public static string VersionOf(string framework) => "v" + string.Join('.', framework[3..].Select(c => c.ToString()));

    /// <summary>A project name as a namespace: characters a namespace can't have become <c>_</c>, a leading digit gets one.</summary>
    public static string Namespace(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var text = new string([.. part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
            return text.Length > 0 && char.IsDigit(text[0]) ? "_" + text : text;
        });
        return string.Join('.', parts);
    }
}
