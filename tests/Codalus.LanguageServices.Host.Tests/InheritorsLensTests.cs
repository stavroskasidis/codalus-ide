using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// CodeLens's count of derived types on every class something can derive from (its inheritors), not only abstract
/// ones: shown when the class has some, hidden ("-") when it has none, and not offered on sealed or static classes, on
/// classes whose constructors are all private, or on structs.
/// </summary>
public sealed class InheritorsLensTests
{
    private const string Code = """
        namespace Contoso;

        public class Animal { }
        public class Dog : Animal { }
        public class Cat : Animal { }
        public class Puppy : Dog { }
        public class Leaf { }
        public sealed class Closed { }
        public static class Helpers { }
        public record Message;
        public record Reply : Message;
        public record struct Point(int X);
        public struct Size { }
        public interface IVehicle { }
        public class Car : IVehicle { }
        public class Singleton { private Singleton() { } }
        public class Template { protected Template() { } }
        """;

    private static async Task<(Solution Solution, Document Document, SourceText Text)> OpenAsync()
    {
        var workspace = new AdhocWorkspace();
        var path = Path.Combine(Path.GetTempPath(), "codalus-inheritors", "Zoo.cs");
        var project = workspace.CurrentSolution.AddProject("Zoo", "Zoo", LanguageNames.CSharp)
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        var text = SourceText.From(Code);
        var document = project.AddDocument("Zoo.cs", text, filePath: path);
        Assert.True(workspace.TryApplyChanges(document.Project.Solution));
        var current = workspace.CurrentSolution.GetDocument(document.Id)!;
        return (workspace.CurrentSolution, current, await current.GetTextAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The resolved titles of the implementations lens on the declaration of <paramref name="name"/>, or null without one.</summary>
    private static async Task<string?> ImplementationsTitleAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var (solution, document, text) = await OpenAsync();
        var line = text.Lines.Single(l => System.Text.RegularExpressions.Regex.IsMatch(l.ToString(), $@"\b{name}\b[ ;({{:]") && !l.ToString().Contains(": " + name, StringComparison.Ordinal)).LineNumber;
        var lenses = await CodeLensFeature.LensesAsync(document, ct);
        var onLine = lenses.Where(l => l.Range.Start.Line == line).ToList();
        Assert.Contains(onLine, l => l.Data!.Value[3].GetString() == "references"); // every type keeps its references
        if (onLine.SingleOrDefault(l => l.Data!.Value[3].GetString() == "implementations") is not { } lens)
        {
            return null;
        }

        var resolved = await CodeLensFeature.ResolveAsync(solution, lens, ct);
        return resolved.Command!.Title;
    }

    [Theory]
    [InlineData("Animal", "3 implementations")] // Dog, Cat and, through Dog, Puppy
    [InlineData("Dog", "1 implementation")]
    [InlineData("Message", "1 implementation")] // a record class
    [InlineData("IVehicle", "1 implementation")] // an interface, as before
    public async Task A_class_with_derived_types_counts_them(string type, string title) =>
        Assert.Equal(title, await ImplementationsTitleAsync(type));

    [Theory]
    [InlineData("Leaf")]
    [InlineData("Cat")]
    [InlineData("Reply")]
    [InlineData("Template")] // a protected constructor: a class elsewhere can derive from it
    public async Task A_class_nothing_derives_from_hides_its_count(string type) =>
        Assert.Equal("-", await ImplementationsTitleAsync(type));

    [Theory]
    [InlineData("Closed")]
    [InlineData("Helpers")]
    [InlineData("Point")]
    [InlineData("Size")]
    [InlineData("Singleton")] // only private constructors and no nested types: nothing can derive from it
    public async Task Sealed_and_static_classes_and_structs_have_no_count(string type) =>
        Assert.Null(await ImplementationsTitleAsync(type));
}
