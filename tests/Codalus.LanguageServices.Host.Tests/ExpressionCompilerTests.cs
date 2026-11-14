using System.Reflection;
using System.Runtime.Loader;
using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// M58 (ADR 0094): the debugger expressions the Codalus debugger's own evaluator doesn't read, compiled where a frame
/// is stopped. Each case compiles against a small program and runs the result against that program loaded here, as the
/// debugger runs it in the debugged one: the frame's <c>this</c>, locals and parameters as the method's parameters,
/// members the program's own code couldn't reach, the frame's type parameters.
/// </summary>
public sealed class ExpressionCompilerTests
{
    private const string Shop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace Shop;

        public sealed class Cart
        {
            private static int s_count = 7;
            private readonly List<int> _prices = new() { 3, 5, 8 };

            public string Name { get; set; } = "cart";

            private int Secret => 42;

            public int Total(int extra)
            {
                var factor = 2;
                return _prices.Sum() * factor + extra + s_count; // [here]
            }

            public static T First<T>(IEnumerable<T> items) where T : struct
            {
                var seen = items.ToList();
                return seen[0]; // [generic]
            }

            private void Rename(string name) => Name = name;

            private static int Double(int value) => value * 2;

            private sealed class Tag
            {
                public string Text => "tag";
            }
        }
        """;

    private const string Basket = """
        Imports System.Collections.Generic
        Imports System.Linq

        Namespace Shop
            Public Class Basket
                Private _items As New List(Of Integer) From {1, 2, 3}
                Private Shared s_limit As Integer = 9

                Public Property Name As String = "basket"

                Friend Function Twice(value As Integer) As Integer
                    Return value * 2
                End Function

                Private Sub Clear()
                    _items.Clear()
                End Sub

                Public Function Count(extra As Integer) As Integer
                    Dim factor = 2
                    Return _items.Count * factor + extra ' [here]
                End Function
            End Class
        End Namespace
        """;

    private sealed class Program : IDisposable
    {
        private readonly AdhocWorkspace _workspace = new();
        private readonly AssemblyLoadContext _context = new("expressions", isCollectible: true);
        private readonly string _path;
        private readonly string _source;

        public Program(string source, string language)
        {
            _source = source;
            _path = Path.Combine(Path.GetTempPath(), "codalus-expressions", language == LanguageNames.CSharp ? "Cart.cs" : "Basket.vb");
            var project = ProjectId.CreateNewId();
            var framework = WorkspaceManager.FrameworkReferences(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            CompilationOptions options = language == LanguageNames.CSharp
                ? new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                : new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            var solution = _workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(project, VersionStamp.Create(), "Shop", "Shop", language, compilationOptions: options, metadataReferences: framework))
                .AddDocument(DocumentId.CreateNewId(project), Path.GetFileName(_path), SourceText.From(source), filePath: _path);
            Assert.True(_workspace.TryApplyChanges(solution));
            Feature = new ExpressionCompilerFeature(() => _workspace.CurrentSolution);

            // The program itself, loaded as the debugged process has it.
            using var stream = new MemoryStream();
            var emitted = _workspace.CurrentSolution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!.Emit(stream);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            stream.Position = 0;
            Assembly = _context.LoadFromStream(stream);
        }

        public ExpressionCompilerFeature Feature { get; }

        public Assembly Assembly { get; }

        private int Line(string marker) => Array.FindIndex(_source.Split('\n'), l => l.Contains("[" + marker + "]", StringComparison.Ordinal)) + 1;

        public Task<CompileExpressionResult> CompileAsync(string expression, string marker = "here") =>
            Feature.CompileAsync(new CompileExpressionParams(expression, _path, Line(marker), 0, "/out/Shop.dll"), TestContext.Current.CancellationToken);

        /// <summary>Compiles, loads and calls, with <paramref name="frame"/> as the frame's <c>this</c> and variables; a parameter like <c>this.Name</c> is read from <c>this</c>.</summary>
        public async Task<object?> EvaluateAsync(string expression, Dictionary<string, object?> frame, string marker = "here", Type[]? typeArguments = null)
        {
            var compiled = await CompileAsync(expression, marker);
            Assert.True(compiled.Errors.Length == 0, string.Join("\n", compiled.Errors));
            var method = _context.LoadFromStream(new MemoryStream(compiled.Assembly!)).GetType(compiled.TypeName!, throwOnError: true)!.GetMethod(compiled.MethodName!)!;
            Assert.Equal(typeArguments?.Length ?? 0, compiled.TypeParameters);
            if (typeArguments is not null)
            {
                method = method.MakeGenericMethod(typeArguments);
            }

            var arguments = compiled.Parameters.Select(p => p.StartsWith("this.", StringComparison.Ordinal)
                ? frame["this"]!.GetType().GetField(p[5..], BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(frame["this"])
                : frame[p]).ToArray();
            return method.Invoke(null, arguments);
        }

        public void Dispose()
        {
            _workspace.Dispose();
            _context.Unload();
        }
    }

    private static Dictionary<string, object?> CartFrame(Program program) => new()
    {
        ["this"] = Activator.CreateInstance(program.Assembly.GetType("Shop.Cart", throwOnError: true)!),
        ["factor"] = 2,
        ["extra"] = 1,
    };

    [Fact]
    public async Task A_lambda_over_a_private_field_takes_this_and_the_locals_it_names()
    {
        using var program = new Program(Shop, LanguageNames.CSharp);
        var compiled = await program.CompileAsync("_prices.Where(p => p > factor).Sum() + extra");
        Assert.Empty(compiled.Errors);
        Assert.Equal(["this", "factor", "extra"], compiled.Parameters);
        Assert.Equal("Shop.__CodalusExpression", compiled.TypeName);
        Assert.Equal("Evaluate", compiled.MethodName);
        Assert.Equal(0, compiled.TypeParameters);
        Assert.Equal(17, await program.EvaluateAsync("_prices.Where(p => p > factor).Sum() + extra", CartFrame(program)));
    }

    [Fact]
    public async Task Members_the_programs_own_code_could_not_reach_are_reached()
    {
        using var program = new Program(Shop, LanguageNames.CSharp);
        var frame = CartFrame(program);

        // A private property through this, a private static field, and this itself; only what's named is a parameter.
        Assert.Equal(49, await program.EvaluateAsync("Secret + s_count", frame));
        Assert.Equal(["this"], (await program.CompileAsync("Secret + s_count")).Parameters);
        Assert.Equal(4, await program.EvaluateAsync("this.Name.Length", frame));
        Assert.Empty((await program.CompileAsync("System.Math.Max(1, 2)")).Parameters);
        Assert.Equal(7, await program.EvaluateAsync("Cart.s_count", frame));

        // A static method and a nested type by their simple names, as the type's own code names them.
        Assert.Equal(14, await program.EvaluateAsync("Double(s_count)", frame));
        Assert.Equal("tag", await program.EvaluateAsync("new Tag().Text", frame));
        Assert.Equal("Tag", await program.EvaluateAsync("typeof(Tag).Name", frame));
        Assert.Equal(1, await program.EvaluateAsync("new List<Tag> { new Tag() }.Count", frame));

        // A method that returns nothing is called, and gives null; a private one at that.
        Assert.Null(await program.EvaluateAsync("Rename(\"renamed\")", frame));
        Assert.Equal("renamed", await program.EvaluateAsync("Name", frame));
    }

    [Fact]
    public async Task What_the_evaluator_does_not_read_compiles_as_csharp()
    {
        using var program = new Program(Shop, LanguageNames.CSharp);
        var frame = CartFrame(program);
        Assert.Equal("Cart", await program.EvaluateAsync("typeof(Cart).Name", frame));
        Assert.Equal(true, await program.EvaluateAsync("extra is > 0 and < 5", frame));
        Assert.Equal("fresh", await program.EvaluateAsync("new Cart { Name = \"fresh\" }.Name", frame)); // the initializer's Name isn't this.Name
        Assert.Equal([10, 16], Assert.IsAssignableFrom<IEnumerable<int>>(await program.EvaluateAsync("from p in _prices where p > 3 select p * factor", frame)));
        Assert.Equal("cart has 3", await program.EvaluateAsync("$\"{Name} has {_prices.Count}\"", frame));
        Assert.Equal(3, await program.EvaluateAsync("new List<int>(_prices).Count", frame));
        Assert.Equal(2, await program.EvaluateAsync("_prices.Count(p => p % 2 == 1)", frame));
        Assert.Equal("int", await program.EvaluateAsync("factor switch { 2 => nameof(System.Int32).Length == 5 ? \"int\" : \"?\", _ => \"other\" }", frame));
    }

    [Fact]
    public async Task A_generic_frames_type_parameters_are_the_methods()
    {
        using var program = new Program(Shop, LanguageNames.CSharp);
        var frame = new Dictionary<string, object?> { ["seen"] = new List<int> { 4, 5 }, ["items"] = new[] { 4, 5 } };
        Assert.Equal(2, await program.EvaluateAsync("seen.Select(s => s).Count()", frame, "generic", [typeof(int)]));
        Assert.Equal(0, await program.EvaluateAsync("default(T)", frame, "generic", [typeof(int)]));
        Assert.Equal("Int32", await program.EvaluateAsync("typeof(T).Name", frame, "generic", [typeof(int)]));

        // No this in a static method.
        Assert.Contains("'this' isn't available", Assert.Single((await program.CompileAsync("this.Name", "generic")).Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_does_not_compile_says_so_in_the_compilers_words()
    {
        using var program = new Program(Shop, LanguageNames.CSharp);
        Assert.Contains("CS0103", Assert.Single((await program.CompileAsync("nothere + 1")).Errors), StringComparison.Ordinal);
        Assert.NotEmpty((await program.CompileAsync("_prices.Where(")).Errors);
        Assert.Contains("'base'", Assert.Single((await program.CompileAsync("base.ToString()")).Errors), StringComparison.Ordinal);
        Assert.Null((await program.CompileAsync("nothere")).Assembly);

        // A file no project has.
        var elsewhere = await program.Feature.CompileAsync(new CompileExpressionParams("1", "/nowhere/Other.cs", 1), TestContext.Current.CancellationToken);
        Assert.Contains("isn't in a loaded project", Assert.Single(elsewhere.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// A project whose references haven't arrived (the workspace has it before its design-time build ends): the answer
    /// says so, in both languages, where it was the compiler's errors for a program with no core library ("BC30002: Type
    /// 'System.String' is not defined"; a Visual Basic expression evaluated right after the stop, in the shell's tests).
    /// </summary>
    [Theory]
    [InlineData(LanguageNames.CSharp, "Cart.cs", "namespace Shop { public class Cart { public int Total() { return 1; } } }", "1 + 1")]
    [InlineData(LanguageNames.VisualBasic, "Basket.vb", "Public Class Basket\n    Public Function Total() As Integer\n        Return 1\n    End Function\nEnd Class\n", "1 + 1")]
    public async Task A_project_without_its_references_yet_says_it_is_still_loading(string language, string file, string source, string expression)
    {
        using var workspace = new AdhocWorkspace();
        var project = ProjectId.CreateNewId();
        var path = Path.Combine(Path.GetTempPath(), "codalus-expressions-loading", file);
        CompilationOptions options = language == LanguageNames.CSharp
            ? new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            : new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(project, VersionStamp.Create(), "Shop", "Shop", language, compilationOptions: options))
            .AddDocument(DocumentId.CreateNewId(project), file, SourceText.From(source.Replace("\\n", "\n", StringComparison.Ordinal)), filePath: path)));
        var feature = new ExpressionCompilerFeature(() => workspace.CurrentSolution);

        var result = await feature.CompileAsync(new CompileExpressionParams(expression, path, 3, 0, "/out/Shop.dll"), TestContext.Current.CancellationToken);

        Assert.Null(result.Assembly);
        var error = Assert.Single(result.Errors);
        Assert.Contains("isn't in a loaded project yet", error, StringComparison.Ordinal);
        Assert.Contains("still loading", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_compiler_options_that_ignore_accessibility_are_still_there()
    {
        // Two options that aren't public (ExpressionCompilerFeature.IgnoreAccessibility): a Roslyn that renames them fails here.
        var options = ExpressionCompilerFeature.IgnoreAccessibility(new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Equal(MetadataImportOptions.All, options.MetadataImportOptions);
    }

    [Fact]
    public async Task Visual_basic_expressions_compile_with_private_fields_of_me_read_by_the_debugger()
    {
        using var program = new Program(Basket, LanguageNames.VisualBasic);
        var frame = new Dictionary<string, object?>
        {
            ["this"] = Activator.CreateInstance(program.Assembly.GetType("Shop.Basket", throwOnError: true)!),
            ["factor"] = 2,
            ["extra"] = 1,
        };

        // A private field of Me is a parameter the debugger reads; a Friend member is called through Me.
        var compiled = await program.CompileAsync("_items.Where(Function(i) i > 1).Sum() * factor + extra");
        Assert.Empty(compiled.Errors);
        Assert.Equal(["this._items", "factor", "extra"], compiled.Parameters);
        Assert.Equal(11, await program.EvaluateAsync("_items.Where(Function(i) i > 1).Sum() * factor + extra", frame));
        Assert.Equal(8, await program.EvaluateAsync("Twice(factor) * 2", frame));
        Assert.Equal("BASKET", await program.EvaluateAsync("Me.Name.ToUpper()", frame));
        Assert.Equal(true, await program.EvaluateAsync("TypeOf Me Is Basket AndAlso extra = 1", frame));
        Assert.Equal(3, await program.EvaluateAsync("If(Me._items.Count > 2, _items.Count, 0)", frame));

        // A private Shared field: read by the debugger too, by its type.
        Assert.Equal(["Shop.Basket.s_limit"], (await program.CompileAsync("s_limit.ToString()")).Parameters);

        // A private method can't be called from there, and it says so.
        Assert.Contains("private method", Assert.Single((await program.CompileAsync("Clear()")).Errors), StringComparison.Ordinal);
        Assert.Contains("BC30451", Assert.Single((await program.CompileAsync("nothere + 1")).Errors), StringComparison.Ordinal);
    }
}
