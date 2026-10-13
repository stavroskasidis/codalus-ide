using System.Diagnostics;
using System.Text.Json;
using Codalus.Plugins.DotNetTest;
using Codalus.Plugins.Proposed;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>Create Unit Tests (ADR 0010): generated stubs per framework, and the command end to end on samples/Testing.</summary>
[Collection(DotNetTestGroup.Name)]
public sealed class CreateUnitTestsTests(DotNetTestFixture fixture)
{
    private static readonly TypeOutline s_type = new(
        "Arithmetic",
        "Calculator",
        "Arithmetic",
        IsStatic: true,
        HasParameterlessConstructor: false,
        [
            new MethodOutline("Add", "int", [new ParameterOutline("a", "int"), new ParameterOutline("b", "int")], IsStatic: true, IsAsync: false, 6),
            new MethodOutline("Load", "Task<string>", [new ParameterOutline("path", "string")], IsStatic: true, IsAsync: true, 8),
        ],
        SelectedMethod: null);

    [Theory]
    [InlineData("XUnit", "[Fact]", "using Xunit;")]
    [InlineData("NUnit", "[Test]", "[TestFixture]")]
    [InlineData("MSTest", "[TestMethod]", "[TestClass]")]
    [InlineData("TUnit", "[Test]", "await Task.CompletedTask;")]
    public void Stubs_use_the_framework_and_call_each_method(string framework, string attribute, string marker)
    {
        var text = CreateUnitTests.Generate(s_type, s_type.Methods, Enum.Parse<TestFramework>(framework), "Calculator.Tests");

        Assert.Contains("namespace Calculator.Tests;", text, StringComparison.Ordinal);
        Assert.Contains("using Calculator;", text, StringComparison.Ordinal);
        Assert.Contains("public class ArithmeticTests", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split(attribute).Length - 1);
        Assert.Contains(marker, text, StringComparison.Ordinal);
        Assert.Contains("var result = Arithmetic.Add(a, b);", text, StringComparison.Ordinal);
        Assert.Contains("public async Task Load_Works()", text, StringComparison.Ordinal);
        Assert.Contains("var result = await Arithmetic.Load(path);", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_writes_a_test_class_that_builds_and_references_the_code()
    {
        var created = new List<string>();
        var references = new List<(string Project, string Reference)>();
        var commands = fixture.Workbench.Commands;
        using var open = commands.Register(new CommandDescriptor("workbench.openFile", "Open File", ContributionSource.Shell), (args, _) =>
        {
            created.Add(args[0].GetString()!);
            return Task.FromResult<JsonElement?>(null);
        });
        using var reference = commands.Register(new CommandDescriptor("project.addReferenceTo", "Add Reference", ContributionSource.Shell), async (args, ct) =>
        {
            references.Add((args[0].GetString()!, args[1].GetString()!));
            await fixture.Solutions.AddProjectReferenceAsync(args[0].GetString()!, args[1].GetString()!, ct);
            return null;
        });
        fixture.Plugins.Prompts = new PickByLabel("NUnitTests");
        try
        {
            var source = fixture.File("src", "Calculator", "Calculator.cs");
            var line = Array.FindIndex(File.ReadAllLines(source), l => l.Contains("public static int Divide", StringComparison.Ordinal));
            var uri = new Uri(source).AbsoluteUri;
            await commands.ExecuteAsync("dotnettest.createUnitTests", [Json(uri), Json(line), Json(30)], TestContext.Current.CancellationToken);

            var file = Assert.Single(created);
            Assert.Equal(fixture.File("tests", "NUnitTests", "ArithmeticTests.cs"), file);
            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("[Test]", text, StringComparison.Ordinal);
            Assert.Contains("Arithmetic.Divide(a, b)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Arithmetic.Add(", text, StringComparison.Ordinal); // only the method at the caret
            Assert.Empty(references); // NUnitTests already references Calculator

            // The generated class compiles.
            using var build = Process.Start(new ProcessStartInfo("dotnet", ["build", fixture.File("tests", "NUnitTests", "NUnitTests.csproj"), "-nologo", "-v", "quiet"]) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = await build.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(build.ExitCode == 0, output);
            File.Delete(file);
        }
        finally
        {
            fixture.Plugins.Prompts = null;
        }
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class PickByLabel(string label) : IPluginPrompts
    {
        public Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(items.Select((item, index) => (item, index)).First(x => x.item.Label == label).index);

        public Task<string?> InputAsync(string title, string? prompt, string? value, string source, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
