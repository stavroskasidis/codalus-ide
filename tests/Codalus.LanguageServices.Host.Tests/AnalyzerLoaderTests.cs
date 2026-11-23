using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// An analyzer whose dependency is another analyzer the project names, in another folder (Windows Forms' C# generator in
/// <c>analyzers/dotnet/cs</c> uses <c>System.Windows.Forms.Analyzers</c> in <c>analyzers/dotnet</c>): the loader finds it
/// there, as the compiler does, and gives it to both as one assembly. Generated assemblies, in a temp folder.
/// </summary>
public sealed class AnalyzerLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-analyzerloader-" + Guid.NewGuid().ToString("N"));

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

    private static byte[] Compile(string name, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return stream.ToArray();
    }

    /// <summary><c>analyzers/dotnet/Shared.dll</c> and <c>analyzers/dotnet/cs/Generator.dll</c>, which uses it.</summary>
    private (string Shared, string Generator) Layout()
    {
        var shared = Compile("Shared", "namespace Shared { public static class Names { public static string Of() => \"ApplicationConfiguration\"; } }", []);
        var generator = Compile("Generator", "public class G { public string Run() => Shared.Names.Of(); public System.Type Uses() => typeof(Shared.Names); }", [MetadataReference.CreateFromImage(shared)]);
        var dotnet = Path.Combine(_root, "analyzers", "dotnet");
        var cs = Path.Combine(dotnet, "cs");
        Directory.CreateDirectory(cs);
        var sharedPath = Path.Combine(dotnet, "Shared.dll");
        var generatorPath = Path.Combine(cs, "Generator.dll");
        File.WriteAllBytes(sharedPath, shared);
        File.WriteAllBytes(generatorPath, generator);
        return (sharedPath, generatorPath);
    }

    private static object? Call(Assembly generator, string method)
    {
        var type = generator.GetType("G", throwOnError: true)!;
        return type.GetMethod(method)!.Invoke(Activator.CreateInstance(type), null);
    }

    [Fact]
    public void An_analyzers_dependency_in_another_folder_the_project_names_is_found_there()
    {
        var (shared, generator) = Layout();
        var loader = new AnalyzerLoader();
        loader.AddDependencyLocation(shared);
        loader.AddDependencyLocation(generator);

        Assert.Equal("ApplicationConfiguration", Call(loader.LoadFromPath(generator), "Run"));

        // One assembly for both: the shared analyzer loaded as itself is the one the generator uses.
        Assert.Same(loader.LoadFromPath(shared), ((Type)Call(loader.LoadFromPath(generator), "Uses")!).Assembly);
    }

    [Fact]
    public void Without_the_dependency_named_the_generator_fails_as_it_runs()
    {
        var (_, generator) = Layout();
        var loader = new AnalyzerLoader();
        loader.AddDependencyLocation(generator);
        var error = Assert.Throws<TargetInvocationException>(() => Call(loader.LoadFromPath(generator), "Run"));
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }
}
