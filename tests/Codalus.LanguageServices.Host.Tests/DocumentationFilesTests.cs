using System.Globalization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// Referenced assemblies (NuGet packages, reference packs) carry their XML documentation: the file next to the
/// assembly, or the UI culture's localized one, shared by every reference to it. Miscellaneous Files compile against the
/// reference pack of the host's .NET, which has the documentation the runtime's own assemblies don't.
/// </summary>
public sealed class DocumentationFilesTests : IDisposable
{
    private const string Source = """
        namespace Greetings;

        /// <summary>Greets people.</summary>
        public static class Greeter
        {
            /// <summary>Says hello.</summary>
            /// <param name="name">Who to greet.</param>
            public static string Hello(string name) => "Hello " + name;
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-docfiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes Greeter.dll with its Greeter.xml next to it, as a NuGet package has them, and a copy of the documentation
    /// in each of <paramref name="cultures"/>' folders, whose summary names the folder.
    /// </summary>
    private string Library(params string[] cultures)
    {
        Directory.CreateDirectory(_root);
        var compilation = CSharpCompilation.Create(
            "Greeter",
            [CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose))],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dll = Path.Combine(_root, "Greeter.dll");
        var xml = Path.Combine(_root, "Greeter.xml");
        using (var peStream = File.Create(dll))
        using (var xmlStream = File.Create(xml))
        {
            var result = compilation.Emit(peStream, xmlDocumentationStream: xmlStream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        foreach (var culture in cultures)
        {
            Directory.CreateDirectory(Path.Combine(_root, culture));
            File.WriteAllText(Path.Combine(_root, culture, "Greeter.xml"), File.ReadAllText(xml).Replace("Says hello.", $"Says hello ({culture}).", StringComparison.Ordinal));
        }

        return dll;
    }

    /// <summary>The summary of Greeter.Hello as a compilation that references <paramref name="reference"/> has it, or null.</summary>
    private static string? Summary(MetadataReference reference)
    {
        var compilation = CSharpCompilation.Create("User", [], [reference, MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var hello = compilation.GetTypeByMetadataName("Greetings.Greeter")!.GetMembers("Hello").Single();
        return hello.GetDocumentationCommentXml() is { Length: > 0 } xml ? XElement.Parse(xml).Element("summary")?.Value.Trim() : null;
    }

    [Fact]
    public void A_referenced_assembly_has_the_documentation_next_to_it()
    {
        var dll = Library();
        var reference = new ProjectLoader().Metadata(dll, MetadataReferenceProperties.Assembly);

        Assert.NotNull(reference);
        Assert.Equal("Says hello.", Summary(reference));
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("zh-CN", "zh-hans")]
    [InlineData("fr-FR", null)]
    [InlineData("en-US", null)]
    [InlineData("", null)]
    public void The_ui_cultures_documentation_comes_before_the_neutral_one(string culture, string? folder)
    {
        // The folders as Microsoft's localized IntelliSense names them: lowercase, the neutral culture where there is one.
        var dll = Library("de", "pt-br", "zh-hans");
        var found = DocumentationFiles.Find(dll, CultureInfo.GetCultureInfo(culture));

        Assert.Equal(folder is null ? Path.Combine(_root, "Greeter.xml") : Path.Combine(_root, folder, "Greeter.xml"), found, Paths.Comparer);
        var reference = MetadataReference.CreateFromFile(dll, documentation: DocumentationFiles.For(dll, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(folder is null ? "Says hello." : $"Says hello ({folder}).", Summary(reference));
    }

    [Fact]
    public void An_assembly_without_documentation_has_none()
    {
        var dll = Library();
        File.Delete(Path.Combine(_root, "Greeter.xml"));

        Assert.Null(DocumentationFiles.For(dll, CultureInfo.InvariantCulture));
        Assert.Null(Summary(new ProjectLoader().Metadata(dll, MetadataReferenceProperties.Assembly)!));
    }

    [Fact]
    public void A_documentation_file_has_one_provider_until_it_changes()
    {
        var dll = Library();
        var first = DocumentationFiles.For(dll, CultureInfo.InvariantCulture);
        Assert.NotNull(first);
        Assert.Same(first, DocumentationFiles.For(dll, CultureInfo.InvariantCulture));

        // Rebuilt (a library's output referenced as a file): read again.
        var xml = Path.Combine(_root, "Greeter.xml");
        File.WriteAllText(xml, File.ReadAllText(xml).Replace("Says hello.", "Says hello again.", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(xml, File.GetLastWriteTimeUtc(xml).AddMinutes(1));
        var second = DocumentationFiles.For(dll, CultureInfo.InvariantCulture);
        Assert.NotSame(first, second);
        Assert.Equal("Says hello again.", Summary(MetadataReference.CreateFromFile(dll, documentation: second)));
    }

    [Fact]
    public void Miscellaneous_files_compile_against_the_reference_pack_of_the_hosts_runtime()
    {
        var dotnet = Path.Combine(_root, "dotnet");
        string Pack(string version, string framework)
        {
            var folder = Path.Combine(dotnet, "packs", "Microsoft.NETCore.App.Ref", version, "ref", framework);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "System.Runtime.dll"), string.Empty);
            return folder;
        }

        string Runtime(string version)
        {
            var folder = Path.Combine(dotnet, "shared", "Microsoft.NETCore.App", version);
            Directory.CreateDirectory(folder);
            return folder + Path.DirectorySeparatorChar; // as RuntimeEnvironment.GetRuntimeDirectory() has it
        }

        var ten = Pack("10.0.12", "net10.0");
        var tenOlder = Pack("10.0.9", "net10.0");
        var eleven = Pack("11.0.0-rc.2.26504.105", "net11.0");
        Directory.CreateDirectory(Path.Combine(dotnet, "packs", "Microsoft.NETCore.App.Ref", "10.0.20")); // incomplete

        Assert.Equal(tenOlder, WorkspaceManager.ReferencePack(Runtime("10.0.9"))); // its own version
        Assert.Equal(ten, WorkspaceManager.ReferencePack(Runtime("10.0.13"))); // else the newest of its major version
        Assert.Equal(eleven, WorkspaceManager.ReferencePack(Runtime("11.0.1")));
        Assert.Null(WorkspaceManager.ReferencePack(Runtime("9.0.21"))); // none: the runtime's own assemblies
        Assert.Null(WorkspaceManager.ReferencePack(Path.Combine(_root, "app", "10.0.12"))); // not a .NET folder's runtime

        // This host's own: the SDK that runs the tests has the reference pack of its runtime.
        var own = WorkspaceManager.ReferencePack(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        Assert.NotNull(own);
        Assert.True(File.Exists(Path.Combine(own, "System.Runtime.xml")), own);
    }
}
