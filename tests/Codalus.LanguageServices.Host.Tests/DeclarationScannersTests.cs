using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using LanguageVersion = Microsoft.CodeAnalysis.CSharp.LanguageVersion;
using SymbolKind = Codalus.LanguageServices.Protocol.SymbolKind;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The editor's estimates of what a language service will list before it answers (<see cref="DeclarationScanners"/>):
/// whether the navigation bar will show types, and F#'s CodeLens lines. The C# host's languages (C#, Visual Basic,
/// XAML) are checked against the host's own symbols on every file of the repository; F#, JavaScript and TypeScript, and
/// style sheets against what FsAutoComplete 0.85.3, typescript-language-server 6.0.2 (TypeScript 5.9.3) and
/// vscode-langservers-extracted 4.10.0 answered for the same text (recorded: tests run no external server here).
/// </summary>
public sealed class DeclarationScannersTests
{
    private static readonly string[] s_folders = ["src", "tests", "samples", "benchmarks", "eng", "website"];

    private static readonly CSharpParseOptions s_csharp = new(LanguageVersion.Preview, DocumentationMode.None, SourceCodeKind.Regular,
        ["DEBUG", "TRACE", "NET", "NETCOREAPP", .. Enumerable.Range(5, 7).Select(v => $"NET{v}_0_OR_GREATER")]);

    private static List<string> RepositoryFiles(params string[] patterns) =>
        [.. s_folders
            .Select(folder => Path.Combine(RepoPaths.Root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => patterns.SelectMany(pattern => Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)))
            .Where(file => !file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".work" or "node_modules"))
            .Order(StringComparer.Ordinal)];

    /// <summary>Whether the navigation bar lists types from these symbols: classes, interfaces, structs and enums, in namespaces and modules too (<c>NavigationBar.Rebuild</c>).</summary>
    private static bool ListsTypes(IEnumerable<DocumentSymbol> symbols) =>
        symbols.Any(s => s.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum
            || s.Kind is SymbolKind.Namespace or SymbolKind.Module && ListsTypes(s.Children ?? []));

    private static bool CSharpHost(string code) =>
        ListsTypes(DocumentSymbolsFeature.Children(CSharpSyntaxTree.ParseText(code, s_csharp).GetRoot(), SourceText.From(code)));

    private static bool VisualBasicHost(string code) =>
        ListsTypes(VisualBasicStructure.Symbols(VisualBasicSyntaxTree.ParseText(code).GetRoot(), SourceText.From(code)));

    private static async Task<bool> XamlHostAsync(string code, string path)
    {
        using var workspace = new AdhocWorkspace();
        var context = await Xaml.XamlContext.CreateAsync(workspace.CurrentSolution, path, SourceText.From(code), TestContext.Current.CancellationToken);
        return context is not null && ListsTypes(Xaml.XamlAnalysis.Symbols(context));
    }

    [Fact]
    public async Task The_csharp_hosts_languages_on_every_file_of_the_repository()
    {
        var mismatches = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (var file in RepositoryFiles("*.cs", "*.vb", "*.xaml", "*.axaml"))
        {
            var code = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var (languageId, host) = extension switch
            {
                ".cs" => ("csharp", CSharpHost(code)),
                ".vb" => ("vb", VisualBasicHost(code)),
                _ => (extension[1..], await XamlHostAsync(code, file)),
            };
            counts[extension] = counts.GetValueOrDefault(extension) + 1;
            if (DeclarationScanners.DeclaresType(languageId, file, code) != host)
            {
                mismatches.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}: the host {(host ? "lists" : "doesn't list")} types");
            }
        }

        Assert.True(counts.GetValueOrDefault(".cs") > 1000 && counts.GetValueOrDefault(".vb") > 0 && counts.GetValueOrDefault(".xaml") + counts.GetValueOrDefault(".axaml") > 50,
            string.Join(", ", counts.Select(c => $"{c.Key}: {c.Value}")));
        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches.Take(60)));
    }

    [Theory]
    [InlineData("Public Class A\nEnd Class", true)]
    [InlineData("<Serializable>\nFriend NotInheritable Class A\nEnd Class", true)]
    [InlineData("Namespace N\n    Public Structure P\n    End Structure\nEnd Namespace", true)]
    [InlineData("Module M\n    Public Enum E\n        A\n    End Enum\nEnd Module", true)] // a type in a module
    [InlineData("Partial Interface I : End Interface", true)]
    [InlineData("Module Program\n    Sub Main()\n        Console.WriteLine(\"Class A\")\n    End Sub\nEnd Module", false)]
    [InlineData("' Class A\nREM Class B\nModule M\nEnd Module", false)]
    [InlineData("Public Delegate Sub D()", false)]
    [InlineData("Imports System\nModule M\n    Dim x = 1 : Dim [Class] = 2\nEnd Module", false)]
    public void Visual_basic_as_the_host(string code, bool declares)
    {
        Assert.Equal(VisualBasicHost(code), declares);
        Assert.Equal(declares, VisualBasicTypeScanner.DeclaresType(code));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- a comment <Fake/> -->\n<Window xmlns=\"x\"/>", true)]
    [InlineData("<UserControl/>", true)]
    [InlineData("<!-- only a comment -->", false)]
    [InlineData("", false)]
    public void Xaml_has_a_root_element(string code, bool declares) => Assert.Equal(declares, DeclarationScanners.HasRootElement(code));

    // ------------------------------------------------------------------ F# (FsAutoComplete 0.85.3)

    /// <summary>A module of FsAutoComplete's lens rules, with the zero-based lines it listed lenses on (signature and references alike).</summary>
    private const string FSharpModule = """
        module Big

        open System

        /// doc
        let topValue = 42

        let rec even n = if n = 0 then true else odd (n - 1)
        and odd n = if n = 0 then false else even (n - 1)

        let outer x =
            let inner y = y + 1
            let z = 3
            inner x + z

        let (|Even|Odd|) n = if n % 2 = 0 then Even else Odd

        [<AbstractClass>]
        type Base() =
            let hidden = 1
            let helper x = x + hidden
            do printfn "init"
            abstract Run: unit -> int
            default this.Run() = 1
            member val Auto = 0 with get, set
            member this.Prop = helper 2
            member this.Method(a: int) = a
            static member Create() = Base()
            override this.ToString() = "b"
            interface IDisposable with
                member this.Dispose() = ()

        type Rec =
            { A: int }
            member r.Twice = r.A * 2
            static member Zero = { A = 0 }

        type U =
            | One
            | Two of int
            member u.IsOne = match u with One -> true | _ -> false

        module Nested =
            let a = 1
            module Deeper =
                let b = 2
                type T() =
                    member _.M = 1

        let private priv = 1
        let inline inl x = x
        let mutable mut = 0
        let a, b = 1, 2
        let ``spaced name`` = 1
        [<Literal>]
        let Lit = "x"
        """;

    private static readonly int[] s_fsharpModuleLenses = [5, 7, 8, 10, 15, 19, 20, 23, 24, 25, 26, 27, 28, 30, 34, 35, 40, 43, 45, 47, 49, 50, 51, 53, 55];

    private const string FSharpEdges = """"
        module Edge

        type C(x: int) =
            static let counter = 0
            let _ = 1
            new() = C(0)
            val mutable Field: int
            member this.P
                with get () = 1
                and set (v: int) = ()
            member _.Q with get () = 2
            static member val S = 3

        type System.String with
            member s.Shout = s.ToUpper()

        let _ = 1
        let () = ()
        let x: int = 1
        let f = fun a -> a
        let inline private g a = a
        let rec private h a = a
        let o = { new System.IDisposable with member _.Dispose() = () }
        let s = """let notALens = 1"""
        (* let commented = 1 *)
        let mult = (*) 2 3
        let ch = 'a'
        let gen (v: 'T) = v
        let tuple (a, b) = a + b
        type R = { A: int } with member r.B = 1
        let multi = [
            1
            2 ]
        let after = 1
        """";

    private static readonly int[] s_fsharpEdgeLenses = [3, 5, 6, 7, 10, 11, 14, 18, 19, 20, 21, 22, 23, 25, 26, 27, 28, 29, 30, 33];

    private const string FSharpOperators = """
        module Ops
        let (+.) a b = a + b
        let ( *** ) a b = a * b
        let x' = 1
        let [<Literal>] Lit2 = "x"
        let rec f1 x = x
        and [<CompiledName("G")>] g1 x = x
        #if DEBUG
        let dbg = 1
        #else
        let rel = 1
        #endif
        let (Pair(a, b)) = 1
        let {| A = r |} = {| A = 1 |}
        type T() =
            member val private P = 1 with get
            member inline this.I = 1
            member private this.Pv = 1
        """;

    private static readonly int[] s_fsharpOperatorLenses = [1, 2, 3, 4, 5, 6, 10, 15, 16, 17]; // as a script: DEBUG isn't defined

    private static int[] Lines(string code, IEnumerable<int> offsets) =>
        [.. offsets.Select(o => code.AsSpan(0, o).Count('\n')).Distinct().Order()];

    [Fact]
    public void Fsharp_lenses_where_fsautocomplete_lists_them()
    {
        var edges = FSharpEdges;
        Assert.Equal(s_fsharpModuleLenses, Lines(FSharpModule, FSharpDeclarationScanner.Lenses(FSharpModule, script: true)));
        Assert.Equal(s_fsharpModuleLenses, Lines(FSharpModule, FSharpDeclarationScanner.Lenses(FSharpModule, script: false))); // the same in a project
        Assert.Equal(s_fsharpEdgeLenses, Lines(edges, FSharpDeclarationScanner.Lenses(edges, script: true)));
        Assert.Equal(s_fsharpOperatorLenses, Lines(FSharpOperators, FSharpDeclarationScanner.Lenses(FSharpOperators, script: true)));

        // In a project's Debug build, DEBUG is defined: the #if branch instead of the #else's.
        Assert.Equal([1, 2, 3, 4, 5, 6, 8, 15, 16, 17], Lines(FSharpOperators, FSharpDeclarationScanner.Lenses(FSharpOperators, script: false)));
    }

    [Theory]
    [InlineData("module Shapes\n\ntype Shape =\n    | Circle of float\n", true)]
    [InlineData("namespace N\ntype Alias = int\n", true)] // an abbreviation is a class too
    [InlineData("module M\nexception MyError of string\n", true)]
    [InlineData("let f x = x + 1\nlet y = f 2\nprintfn \"%d\" y\nmodule M =\n    let z = 3\n", false)] // modules and values only
    [InlineData("// type A = int\n(* type B = int *)\nlet s = \"type C = int\"\n", false)]
    [InlineData("#if NEVER\ntype A = int\n#endif\nlet x = 1\n", false)]
    public void Fsharp_types_as_fsautocomplete_lists_them(string code, bool declares) =>
        Assert.Equal(declares, FSharpDeclarationScanner.DeclaresType(code, script: true));

    [Fact]
    public void Fsharp_lenses_of_a_scripts_top_level_lets()
    {
        // FsAutoComplete: lenses on f, y and z (a let in a module); none on the expression or the module.
        const string code = "let f x = x + 1\nlet y = f 2\nprintfn \"%d\" y\nmodule M =\n    let z = 3\n";
        Assert.Equal([0, 1, 4], Lines(code, FSharpDeclarationScanner.Lenses(code, script: true)));

        // A lambda's lets in a top-level expression are local (FsAutoComplete: a lens on z alone).
        const string pipeline = "[1; 2]\n|> List.map (fun x ->\n    let y = x\n    y)\n|> ignore\nlet z = 1\n";
        Assert.Equal([5], Lines(pipeline, FSharpDeclarationScanner.Lenses(pipeline, script: true)));
    }

    // ------------------------------------------------------------------ JavaScript and TypeScript (typescript-language-server 6.0.2)

    [Theory]
    [InlineData("import * as fs from \"fs\";\nexport interface IShape { area(): number; }\n", true)]
    [InlineData("export class Circle { area() { return 1; } }", true)]
    [InlineData("export enum Color { Red, Green }", true)]
    [InlineData("const enum CE { A }", true)]
    [InlineData("export abstract class B {}", true)]
    [InlineData("declare module \"m\" { export interface I {} }", true)] // in a module
    [InlineData("declare global { interface Window { x: 1 } }", true)]
    [InlineData("namespace NS { export class Inner {} }", true)]
    [InlineData("export default class { x() {} }", true)] // listed as "default"
    [InlineData("@Component({ selector: 'x' })\nexport class C {}", true)]
    [InlineData("@injectable\nclass C {}", true)]
    [InlineData("const fs = require(\"fs\");\nfunction main() { return 1; }\nclass Animal { speak() {} }\nmodule.exports = { main };", true)]
    [InlineData("const a = 1;\nfunction f() {}\nexport default () => 2;", false)]
    [InlineData("export function helper() { class Local {} return 1; }", false)] // in a function: not listed as a type
    [InlineData("const A = class { m() {} };\nlet o = { class: 1 }; o.class = 2;", false)]
    [InlineData("const s = `class Fake ${ \"{\" } }`;\nconst r = /class X {/g;\n// class C {}\n/* interface I {} */", false)]
    [InlineData("export type T = { a: 1 };", false)]
    [InlineData("const x = 1\nclass AfterNoSemicolon {}", true)]
    [InlineData("const t = a / b; class C {}", true)]
    public void Javascript_and_typescript_types_as_the_typescript_server_lists_them(string code, bool declares) =>
        Assert.Equal(declares, JavaScriptTypeScanner.DeclaresType(code));

    [Theory]
    [InlineData("var Store = function (state) { this.state = state; };\nStore.prototype = {\n  get: function () {}\n};", true)]
    [InlineData("function Point(x) { this.x = x; }\nPoint.prototype.norm = function () { return 1; };", true)]
    [InlineData("var Ctor = function Iterator() {};\nCtor.prototype = IteratorPrototype;", false)] // not an object literal
    [InlineData("function B() {}\n(function () { B.prototype.y = 2; })();", false)] // assigned in a function
    [InlineData("function Outer() {\n  function Inner() {}\n  Inner.prototype.x = 1;\n}", false)] // a class in a function
    [InlineData("function C() {}\nC.prototype;", false)]
    public void Javascript_classes_from_prototypes(string code, bool declares)
    {
        Assert.Equal(declares, DeclarationScanners.DeclaresType("javascript", "app.js", code));
        Assert.False(DeclarationScanners.DeclaresType("typescript", "app.ts", code)); // TypeScript files don't make classes of them
    }

    // ------------------------------------------------------------------ CSS, SCSS and Less (vscode-langservers-extracted 4.10.0)

    [Theory]
    [InlineData("css", ":root { --x: 1px; }", true)]
    [InlineData("css", "@media (max-width: 600px) { .btn { color: blue; } }", true)]
    [InlineData("css", "@import url(\"x.css\");\n@font-face { font-family: X; }", true)]
    [InlineData("css", "@keyframes spin { from { opacity: 0; } }", true)]
    [InlineData("css", "@supports (display: grid) { .g { color: red; } }", true)]
    [InlineData("css", "@starting-style { .s { opacity: 0; } }", true)]
    [InlineData("css", "@scope (.card) { img { border: 0; } }", true)]
    [InlineData("css", "@import url(\"x.css\");\n@charset \"utf-8\";\n/* .a { } */", false)]
    [InlineData("css", "@page { margin: 1cm; }\n@property --x { syntax: '<length>'; }\n@counter-style thumbs { system: cyclic; }", false)]
    [InlineData("css", "@font-feature-values Font One { @styleset { nice-style: 12; } }\n@view-transition { navigation: auto; }", false)]
    [InlineData("css", "@namespace svg url(http://www.w3.org/2000/svg);", false)]
    [InlineData("scss", "$base: 4px;\n@mixin m { color: red; }\n.nav { a { color: blue; } }", true)]
    [InlineData("scss", "@use \"sass:math\";\n@function f($a) { @return $a; }\n@mixin m { .inner { color: red; } }", false)] // in a mixin: not listed as a type
    [InlineData("scss", "%placeholder { color: red; }", true)]
    [InlineData("scss", "@if $a { .ifrule { color: red; } }", true)]
    [InlineData("scss", "@each $n in a, b { .each-#{$n} { color: red; } }", true)]
    [InlineData("scss", "@include breakpoint { .inc { color: red; } }", true)]
    [InlineData("scss", "@at-root { .root { color: red; } }", true)]
    [InlineData("scss", "@media screen { @include m; }\n// .commented { }", false)]
    [InlineData("less", "@x: 1px;\n.mixin() { color: red; }\n.a { .mixin(); }", true)]
    [InlineData("less", "@x: 1px;\n.mixin() { color: red; }", false)]
    [InlineData("less", ".m(@a; @b: 2) { color: red; }\n@detached: { background: red; };", false)]
    [InlineData("less", ".guard when (@mode = dark) { color: red; }", true)]
    [InlineData("less", "#ns { .m() { color: red; } }", true)]
    public void Style_sheets_rules_as_the_css_server_lists_them(string languageId, string code, bool declares) =>
        Assert.Equal(declares, DeclarationScanners.DeclaresType(languageId, "style." + languageId, code));
}
