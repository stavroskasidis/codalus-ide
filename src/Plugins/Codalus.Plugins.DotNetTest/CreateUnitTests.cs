using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.DotNetTest;

/// <summary>The test framework a generated test class uses.</summary>
internal enum TestFramework
{
    XUnit,
    NUnit,
    MSTest,
    TUnit,
}

/// <summary>
/// Create Unit Tests (ADR 0010): on a class or method, generates a test class with a test stub per public method (or
/// the chosen method) in a test project the user picks, adds a reference to the code's project, and opens the file.
/// Arguments (from the editor context menu): the document URI and the zero-based caret line and column.
/// </summary>
internal static class CreateUnitTests
{
    public static async Task<object?> RunAsync(IPluginContext context, IPluginContext api, CommandInvocation args, CancellationToken cancellationToken)
    {
        var (uri, line, character) = await PositionAsync(context, args, cancellationToken).ConfigureAwait(false);
        if (uri is null)
        {
            await context.Window.ShowNotificationAsync(NotificationSeverity.Info, "Open a C# file and place the cursor in a class or method to create unit tests for it.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        var outline = await api.Workspace.GetTypeOutlineAsync(uri, line, character, cancellationToken).ConfigureAwait(false);
        if (outline is null || outline.Methods.Count == 0)
        {
            await context.Window.ShowNotificationAsync(NotificationSeverity.Info, "Place the cursor in a class (or one of its methods) that has public methods.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        var solution = await api.Workspace.GetSolutionAsync(cancellationToken).ConfigureAwait(false);
        var path = new Uri(uri).LocalPath;
        var source = solution?.Projects.FirstOrDefault(p => p.CompileFiles.Contains(path, StringComparer.OrdinalIgnoreCase));
        var testProjects = solution?.Projects.Where(p => p.IsTestProject && p.Path != source?.Path).ToList() ?? [];
        if (testProjects.Count == 0)
        {
            await context.Window.ShowNotificationAsync(
                NotificationSeverity.Info,
                "The solution has no test project. Add one with File → New Project (an xUnit, NUnit, MSTest or TUnit template), then try again.",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Prefer a test project named after the code's project (App → App.Tests).
        var ordered = testProjects.OrderByDescending(p => source is not null && p.Name.StartsWith(source.Name, StringComparison.OrdinalIgnoreCase)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var pickedProject = ordered.Count == 1
            ? ordered[0]
            : (await context.Window.ShowQuickPickAsync(
                [.. ordered.Select(p => new QuickPickItem(p.Name) { Description = FrameworkName(Detect(p.Path)), Tag = p })],
                "Test project for the new tests",
                cancellationToken).ConfigureAwait(false))?.Tag as WorkspaceProject;
        if (pickedProject is null)
        {
            return null;
        }

        var framework = Detect(pickedProject.Path);
        var methods = outline.SelectedMethod is { } selected ? outline.Methods.Where(m => m.Name == selected).ToList() : [.. outline.Methods];
        var folder = Path.GetDirectoryName(pickedProject.Path)!;
        var file = UniquePath(folder, outline.Name + "Tests");
        var text = Generate(outline, methods, framework, RootNamespace(pickedProject));
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        if (source is not null && !pickedProject.ProjectReferences.Contains(source.Path, StringComparer.OrdinalIgnoreCase))
        {
            await context.Commands.ExecuteAsync("project.addReferenceTo", cancellationToken, pickedProject.Path, source.Path).ConfigureAwait(false);
        }

        await context.Commands.ExecuteAsync("workbench.openFile", cancellationToken, file).ConfigureAwait(false);
        return file;
    }

    private static async Task<(string? Uri, int Line, int Character)> PositionAsync(IPluginContext context, CommandInvocation args, CancellationToken cancellationToken)
    {
        if (args.Arg<string>(0) is { } uri)
        {
            return (uri, args.Arg<int>(1), args.Arg<int>(2));
        }

        if (await context.Commands.ExecuteAsync("editor.activePosition", cancellationToken).ConfigureAwait(false) is { ValueKind: JsonValueKind.Array } position
            && position.GetArrayLength() == 3)
        {
            return (position[0].GetString(), position[1].GetInt32(), position[2].GetInt32());
        }

        return (null, 0, 0);
    }

    /// <summary>The framework a test project uses, from its package references (or SDK).</summary>
    internal static TestFramework Detect(string projectPath)
    {
        try
        {
            var project = XDocument.Load(projectPath);
            var sdk = (string?)project.Root?.Attribute("Sdk") ?? string.Empty;
            var packages = project.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => (string?)e.Attribute("Include") ?? string.Empty).ToList();
            if (sdk.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) || packages.Any(p => p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)))
            {
                return TestFramework.MSTest;
            }

            if (packages.Any(p => p.Equals("NUnit", StringComparison.OrdinalIgnoreCase)))
            {
                return TestFramework.NUnit;
            }

            if (packages.Any(p => p.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)))
            {
                return TestFramework.TUnit;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
        }

        return TestFramework.XUnit;
    }

    private static string FrameworkName(TestFramework framework) => framework switch
    {
        TestFramework.NUnit => "NUnit",
        TestFramework.MSTest => "MSTest",
        TestFramework.TUnit => "TUnit",
        _ => "xUnit",
    };

    private static string RootNamespace(WorkspaceProject project)
    {
        try
        {
            var value = XDocument.Load(project.Path).Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
        }

        return string.Concat(project.Name.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".cs");
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(folder, name + i.ToString(CultureInfo.InvariantCulture) + ".cs");
        }

        return path;
    }

    /// <summary>The test class: one stub per method, failing until written (Arrange, Act, Assert).</summary>
    internal static string Generate(TypeOutline type, IReadOnlyList<MethodOutline> methods, TestFramework framework, string testNamespace)
    {
        var text = new StringBuilder();
        var usings = new SortedSet<string>(StringComparer.Ordinal);
        if (type.Namespace is { } ns && ns != testNamespace)
        {
            usings.Add(ns);
        }

        switch (framework)
        {
            case TestFramework.XUnit:
                usings.Add("Xunit");
                break;
            case TestFramework.NUnit:
                usings.Add("NUnit.Framework");
                break;
            case TestFramework.MSTest:
                usings.Add("Microsoft.VisualStudio.TestTools.UnitTesting");
                break;
        }

        foreach (var name in usings)
        {
            text.Append("using ").Append(name).AppendLine(";");
        }

        if (usings.Count > 0)
        {
            text.AppendLine();
        }

        text.Append("namespace ").Append(testNamespace).AppendLine(";").AppendLine();
        switch (framework)
        {
            case TestFramework.NUnit:
                text.AppendLine("[TestFixture]");
                break;
            case TestFramework.MSTest:
                text.AppendLine("[TestClass]");
                break;
        }

        var className = type.NestedName.Replace(".", string.Empty, StringComparison.Ordinal) + "Tests";
        text.Append("public class ").AppendLine(className).AppendLine("{");
        var attribute = framework switch
        {
            TestFramework.XUnit => "[Fact]",
            TestFramework.MSTest => "[TestMethod]",
            _ => "[Test]",
        };
        var first = true;
        foreach (var group in methods.GroupBy(m => m.Name))
        {
            var overloads = group.ToList();
            for (var i = 0; i < overloads.Count; i++)
            {
                var method = overloads[i];
                if (!first)
                {
                    text.AppendLine();
                }

                first = false;
                var async = method.IsAsync || framework == TestFramework.TUnit;
                var testName = method.Name + (overloads.Count > 1 ? (i + 1).ToString(CultureInfo.InvariantCulture) : string.Empty) + "_Works";
                text.Append("    ").AppendLine(attribute);
                text.Append("    public ").Append(async ? "async Task " : "void ").Append(testName).AppendLine("()");
                text.AppendLine("    {");
                text.AppendLine("        // Arrange");
                var target = type.NestedName;
                if (!method.IsStatic)
                {
                    target = "subject";
                    text.Append("        var subject = ").Append(type.IsStatic || !type.HasParameterlessConstructor ? "default(" + type.NestedName + ")!" : "new " + type.NestedName + "()").AppendLine(";");
                }

                foreach (var parameter in method.Parameters)
                {
                    text.Append("        ").Append(parameter.Type).Append(' ').Append(Identifier(parameter.Name)).Append(" = default!;").AppendLine();
                }

                text.AppendLine();
                text.AppendLine("        // Act");
                var call = $"{target}.{method.Name}({string.Join(", ", method.Parameters.Select(p => Identifier(p.Name)))})";
                var returns = method.ReturnType is not ("void" or "Task" or "ValueTask");
                text.Append("        ").Append(returns ? "var result = " : string.Empty).Append(method.IsAsync ? "await " : string.Empty).Append(call).AppendLine(";");
                text.AppendLine();
                text.AppendLine("        // Assert");
                text.AppendLine("        Assert.Fail(\"Write this test.\");");
                if (framework == TestFramework.TUnit && !method.IsAsync)
                {
                    text.AppendLine("        await Task.CompletedTask;");
                }

                text.AppendLine("    }");
            }
        }

        text.AppendLine("}");
        return text.ToString();
    }

    private static string Identifier(string name) => name is "class" or "event" or "object" or "string" or "int" or "operator" or "params" ? "@" + name : name;
}
