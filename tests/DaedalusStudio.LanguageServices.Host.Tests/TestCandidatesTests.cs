using DaedalusStudio.LanguageServices.Host.Features;
using DaedalusStudio.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DaedalusStudio.LanguageServices.Host.Tests;

public class TestCandidatesTests
{
    private static TestCandidate[] Find(string source)
    {
        var text = SourceText.From(source);
        return TestCandidatesFeature.Find(CSharpSyntaxTree.ParseText(text).GetRoot(), text);
    }

    [Fact]
    public void Finds_xunit_nunit_mstest_and_tunit_tests()
    {
        var candidates = Find("""
            namespace Tests.Unit;

            public class XunitTests
            {
                [Fact] public void Plain() { }
                [Xunit.Theory, InlineData(1)] public void Data(int x) { }
                [SkippableFact] public void Custom() { }
                public void Helper() { }
            }

            [TestFixture]
            public class NUnitTests
            {
                [Test] public void One() { }
                [TestCase(1)] public void Cases(int x) { }
            }

            [TestClass]
            public class MSTests
            {
                [TestMethod] public void One() { }
                [DataTestMethod, DataRow(1)] public void Rows(int x) { }
            }

            public class TUnitTests
            {
                [Test, Arguments(1)] public async Task Args(int x) { }
            }

            public abstract class Base
            {
                [Fact] public void Inherited() { }
            }
            """);

        Assert.Equal(
            [
                "class Tests.Unit.XunitTests", "method Tests.Unit.XunitTests.Plain", "method Tests.Unit.XunitTests.Data*", "method Tests.Unit.XunitTests.Custom",
                "class Tests.Unit.NUnitTests", "method Tests.Unit.NUnitTests.One", "method Tests.Unit.NUnitTests.Cases*",
                "class Tests.Unit.MSTests", "method Tests.Unit.MSTests.One", "method Tests.Unit.MSTests.Rows*",
                "class Tests.Unit.TUnitTests", "method Tests.Unit.TUnitTests.Args*",
            ],
            candidates.Select(c => $"{c.Kind} {c.Type}{(c.Kind == TestCandidateKinds.Method ? "." + c.Name : string.Empty)}{(c.Parameterized ? "*" : string.Empty)}"));
        Assert.All(candidates, c => Assert.Equal("Tests.Unit", c.Namespace));
    }

    [Fact]
    public void Nested_and_generic_types_use_metadata_names()
    {
        var candidates = Find("""
            namespace A { namespace B {
                public class Outer
                {
                    public class Inner<T>
                    {
                        [Fact] public void Test() { }
                    }
                }
            } }
            """);

        var method = Assert.Single(candidates, c => c.Kind == TestCandidateKinds.Method);
        Assert.Equal("A.B.Outer+Inner`1", method.Type);
        Assert.Equal("A.B", method.Namespace);
    }

    [Fact]
    public void Ranges_cover_the_declaration_and_name()
    {
        var candidates = Find("""
            public class C
            {
                [Fact]
                public void Name() { }
            }
            """);

        var method = Assert.Single(candidates, c => c.Kind == TestCandidateKinds.Method);
        Assert.Null(method.Namespace);
        Assert.Equal(new Position(3, 4), method.Range.Start); // after the attributes
        Assert.Equal(new Position(3, 26), method.Range.End);
        Assert.Equal(new Position(3, 16), method.SelectionRange.Start);
        Assert.Equal(new Position(3, 20), method.SelectionRange.End);
    }

    [Fact]
    public void The_type_outline_lists_public_methods_and_the_one_at_the_caret()
    {
        var source = """
            namespace App.Core
            {
                public class Outer
                {
                    public sealed class Calc(int seed)
                    {
                        public int Add(int a, int b) => a + b + seed;
                        internal async Task<string> LoadAsync(string path) => await File.ReadAllTextAsync(path);
                        private void Hidden() { }
                        public static Calc Create() => new(0);
                    }
                }
            }
            """;
        var text = SourceText.From(source);
        var root = CSharpSyntaxTree.ParseText(text, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);

        var outline = TypeOutlineFeature.Outline(root, text, source.IndexOf("a + b", StringComparison.Ordinal))!;

        Assert.Equal("Calc", outline.Name);
        Assert.Equal("App.Core", outline.Namespace);
        Assert.Equal("Outer.Calc", outline.NestedName);
        Assert.False(outline.HasParameterlessConstructor); // a primary constructor with parameters
        Assert.Equal(["Add", "LoadAsync", "Create"], outline.Methods.Select(m => m.Name));
        Assert.Equal("Add", outline.SelectedMethod);
        Assert.Equal(["a:int", "b:int"], outline.Methods[0].Parameters.Select(p => p.Name + ":" + p.Type));
        Assert.True(outline.Methods[1].IsAsync);
        Assert.True(outline.Methods[2].IsStatic);
        Assert.Null(TypeOutlineFeature.Outline(root, text, 0)); // outside any type
    }
}
