using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Host.Features;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The editor's estimate of where the C# host's CodeLens will be (<see cref="CSharpLensScanner"/>, which keeps the rows'
/// room before the host answers) against what the host lists (<see cref="CodeLensFeature"/>): the same identifiers, on
/// tricky code and on every C# file of the repository.
/// </summary>
public sealed class CSharpLensScannerTests
{
    /// <summary>The symbols the scanner takes as defined, so the host's parse takes the same <c>#if</c> branches.</summary>
    private static readonly CSharpParseOptions s_options = new(
        LanguageVersion.Preview,
        DocumentationMode.None,
        SourceCodeKind.Regular,
        ["DEBUG", "TRACE", "NET", "NETCOREAPP", .. Enumerable.Range(5, 7).Select(v => $"NET{v}_0_OR_GREATER"),
         "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER"]);

    /// <summary>The repository's folders with C# files.</summary>
    private static readonly string[] s_folders = ["src", "tests", "samples", "benchmarks", "eng", "website"];

    /// <summary>The offsets the host's <c>textDocument/codeLens</c> lists (one per identifier).</summary>
    private static async Task<int[]> HostAsync(Project project, string code, string path, CancellationToken cancellationToken)
    {
        var text = SourceText.From(code);
        var document = project.AddDocument(Path.GetFileName(path), text, filePath: path);
        var lenses = await CodeLensFeature.LensesAsync(document, cancellationToken);
        return [.. lenses.Select(l => text.Lines[l.Range.Start.Line].Start + l.Range.Start.Character).Distinct().Order()];
    }

    private static Project NewProject(AdhocWorkspace workspace) =>
        workspace.CurrentSolution.AddProject("Estimates", "Estimates", LanguageNames.CSharp).WithParseOptions(s_options);

    /// <summary>The identifiers (with what follows them, to tell them apart) where each found a lens, the host's first.</summary>
    private static async Task AssertAsHostAsync(string code)
    {
        using var workspace = new AdhocWorkspace();
        var host = await HostAsync(NewProject(workspace), code, Path.Combine(Path.GetTempPath(), "Estimate.cs"), TestContext.Current.CancellationToken);
        var scanned = CSharpLensScanner.Scan(code);
        Assert.Equal(Describe(code, host), Describe(code, scanned));
    }

    private static List<string> Describe(string code, IEnumerable<int> offsets) =>
        [.. offsets.Select(o => code.Substring(o, Math.Min(16, code.Length - o)).ReplaceLineEndings(" "))];

    [Fact]
    public async Task Types_members_and_their_kinds()
    {
        await AssertAsHostAsync("""
            using System;
            using static System.Math;
            using Point = (int X, int Y);
            [assembly: System.Reflection.AssemblyTitle("x")]
            namespace Shapes.Library
            {
                public interface IShape
                {
                    double Area();
                    string Name { get; }
                    event EventHandler Changed;
                    int this[int index] { get; }
                    static abstract IShape operator +(IShape a, IShape b);
                }

                [Serializable]
                public abstract partial class Shape<T> : IShape where T : struct, IComparable<T>
                {
                    private readonly int _count = 1, _other = 2;
                    public const string Prefix = "shape";
                    private fixed byte _buffer[16];
                    public event EventHandler Changed = delegate { };
                    public event EventHandler Moved { add { } remove { } }
                    event EventHandler IShape.Changed { add { } remove { } }
                    protected Shape() : base() { }
                    static Shape() { }
                    ~Shape() { }
                    public abstract double Area();
                    public virtual string Name => GetType().Name;
                    public int this[int index] => index;
                    int IShape.this[int index] => index;
                    double IShape.Area() => 0;
                    public System.Collections.Generic.Dictionary<string, (int A, string[] B)>? Map { get; } = new() { ["a"] = (1, []) };
                    public ref readonly int First => ref _items[0];
                    public static Shape<T> operator -(Shape<T> a) => a;
                    public static implicit operator int(Shape<T> s) => 0;
                    public static explicit operator Shape<T>(int i) => null!;
                    public T Get<TKey>(TKey key) where TKey : notnull => default;
                    public (int, string) Pair() { return (1, ""); }
                    public unsafe delegate*<int, void> Pointer;
                    public unsafe int* Raw;
                    public delegate void Handler(object sender);
                    public delegate (int A, int B) Splitter(string s);
                    internal enum Kind : byte { Round = 1 << 0, Square }
                    private sealed record Snapshot(int Count) : IComparable<Snapshot> { public int CompareTo(Snapshot? other) => 0; }
                    private record struct Cell(int X, int Y);
                    private readonly ref struct Span;
                    private int[] _items = { 1, 2 };
                }
            }
            """);
    }

    [Fact]
    public async Task Strings_comments_and_directives_dont_count()
    {
        await AssertAsHostAsync("""""
            namespace Strings;

            // class NotAType { void M() { } }
            /* interface INot { void M(); } */
            /// <summary>class Doc { }</summary>
            public class Holder
            {
                private string _regular = "class A { void M() { } } \" { ";
                private string _verbatim = @"class B { "" void M() {
                    } {";
                private string _empty = "", _verbatimEmpty = @"", _quote = @"""";
                private string _interpolated = $"{(true ? "}" : "{")} {{ class C {{ void M() {{ }} }} {new { X = 1 }.X:N2}";
                private string _verbatimInterpolated = $@"{"{"} "" {{ class D {{";
                private string _raw = """
                    class E { void M() { } } " "" {
                    """;
                private string _rawInterpolated = $$"""
                    {{_regular}} {class F { void M() { } } }
                    """;
                private char _brace = '{', _quote2 = '\'', _slash = '\\';
                private byte[] _utf8 = "class G { "u8.ToArray();

            #if DEBUG
                public void InDebug() { }
            #else
                public void InRelease() { }
                public void Unbalanced() {
            #endif

            #if !NET8_0_OR_GREATER || NETFRAMEWORK
                public void Legacy() {
            #elif false
                public void Never() { }
            #elif NET && (TRACE || UNDEFINED)
                public void Modern() { }
            #else
                public void Other() { }
            #endif

            #region Members
                public int Count { get; set; }
            #endregion
            #pragma warning disable CS0169
                private int _unused;
            #pragma warning restore CS0169

                public string After() => $$"""
                    {{Count}} } {
                    """;
            }
            """"");
    }

    [Fact]
    public async Task Top_level_statements_their_local_functions_and_the_types_after_them()
    {
        await AssertAsHostAsync("""
            using System;

            Console.WriteLine(Add(1, 2));
            if (args.Length > 0) { Console.WriteLine("args"); } else { Run(() => { return; }); }
            var record = new { A = 1 };
            int Add(int a, int b) => a + b;
            static void Run(Action action) { action(); }
            async System.Threading.Tasks.Task Later() { await System.Threading.Tasks.Task.Yield(); }
            do { } while (false);

            public record Person(string Name)
            {
                public string Greeting => $"Hello, {Name}";
            }

            enum Color { Red, Green }

            delegate int Combine(int a, int b);
            """);
    }

    [Fact]
    public async Task Extension_blocks_and_nested_types()
    {
        await AssertAsHostAsync("""
            namespace Extensions
            {
                public static class StringExtensions
                {
                    extension(string text)
                    {
                        public bool IsBlank => string.IsNullOrWhiteSpace(text);
                        public string Twice() => text + text;
                    }

                    extension<T>(T[] array) where T : class
                    {
                        public int Size => array.Length;
                    }

                    public static string Reverse(this string text) => text;

                    public class Nested
                    {
                        public interface IInner { void Run(); }
                        public struct Value { public int X { get; init; } }
                    }
                }
            }
            """);
    }

    [Theory]
    [InlineData("namespace N;\npublic sealed class A { }", true)]
    [InlineData("namespace N { [Flags] internal enum E { A, B } }", true)]
    [InlineData("public readonly record struct P(int X);", true)]
    [InlineData("file static partial class F { }", true)]
    [InlineData("using System;\nConsole.WriteLine(\"class A { }\");\nvoid Local() { }", false)] // top-level statements
    [InlineData("// class A { }\n/* struct B */\n#if NEVER\nclass C { }\n#endif\n", false)]
    [InlineData("namespace N;\npublic delegate void D(int x);", false)] // a delegate isn't a type the navigation bar lists
    [InlineData("", false)]
    public void Whether_the_text_declares_a_type(string code, bool declares)
    {
        // The navigation bar keeps its room from the first frame when the C# host's symbols will list a type.
        Assert.Equal(declares, CSharpLensScanner.DeclaresType(code));
    }

    [Fact]
    public async Task Code_that_doesnt_compile_yet()
    {
        // What the editor holds while typing: the estimate needn't be right, but must end, and agree where it can.
        Assert.Empty(CSharpLensScanner.Scan(string.Empty));
        Assert.Empty(CSharpLensScanner.Scan(") ] } ) class"));
        CSharpLensScanner.Scan("class A { void M( { \"unterminated\n } } $\"{ @\" /* ");
        CSharpLensScanner.Scan("#if X\nclass A {\n#elif\n#else\n#endif\n#endif\n#else");
        await AssertAsHostAsync("""
            namespace Typing
            {
                public class Unfinished
                {
                    public void Complete() { }

                    public int Started { get; set; }

                    public void Next() { }
                }
            }
            """);
    }

    [Fact]
    public async Task Every_CSharp_file_of_the_repository_has_its_lenses_where_the_host_lists_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = s_folders
            .Select(folder => Path.Combine(RepoPaths.Root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".work" or "node_modules"))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(files.Count > 1000, $"only {files.Count} files found under {RepoPaths.Root}");

        using var workspace = new AdhocWorkspace();
        var project = NewProject(workspace);
        var mismatches = new List<string>();
        var lenses = 0;
        var scanTime = TimeSpan.Zero;
        var characters = 0L;
        foreach (var file in files)
        {
            var code = await File.ReadAllTextAsync(file, ct);
            var host = await HostAsync(project, code, file, ct);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var scanned = CSharpLensScanner.Scan(code);
            scanTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
            characters += code.Length;
            lenses += host.Length;
            var text = SourceText.From(code);
            foreach (var missing in host.Except(scanned))
            {
                mismatches.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{text.Lines.GetLineFromPosition(missing).LineNumber + 1}: not found: {text.Lines.GetLineFromPosition(missing)}");
            }

            foreach (var extra in scanned.Except(host))
            {
                mismatches.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{text.Lines.GetLineFromPosition(extra).LineNumber + 1}: not the host's: {text.Lines.GetLineFromPosition(extra)}");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{files.Count} files, {characters / 1024} KB, {lenses} lenses; scanned in {scanTime.TotalMilliseconds:0} ms");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {lenses} lenses differ:\n{string.Join('\n', mismatches.Take(60))}");
    }
}
