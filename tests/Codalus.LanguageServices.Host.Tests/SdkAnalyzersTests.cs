using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// ADR 0025: an SDK's generator built for a newer compiler than the host's Roslyn is replaced by the same folder of
/// another installed SDK that the host can load. The SDK layout here is fake, with generated assemblies.
/// </summary>
public sealed class SdkAnalyzersTests : IDisposable
{
    private static readonly Version s_host = new(5, 9, 0, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-sdkanalyzers-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>Writes <c>sdk/&lt;version&gt;/Sdks/Razor/gen/Generator.dll</c>, referencing Microsoft.CodeAnalysis <paramref name="roslyn"/>.</summary>
    private string Sdk(string version, Version roslyn, bool helper = true)
    {
        var folder = Path.Combine(_root, "dotnet", "sdk", version, "Sdks", "Razor", "gen");
        Directory.CreateDirectory(folder);
        var stub = Compile("Microsoft.CodeAnalysis", $"[assembly: System.Reflection.AssemblyVersion(\"{roslyn}\")] namespace Microsoft.CodeAnalysis {{ public class Compilation {{ }} }}", []);
        var generator = Compile("Generator", "public class G { public Microsoft.CodeAnalysis.Compilation C; }", [MetadataReference.CreateFromImage(stub)]);
        File.WriteAllBytes(Path.Combine(folder, "Generator.dll"), generator);
        if (helper)
        {
            File.WriteAllBytes(Path.Combine(folder, "Helper.dll"), Compile("Helper", "public class H { }", []));
        }

        return folder;
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

    [Fact]
    public void A_generator_for_a_newer_compiler_is_replaced_by_an_older_sdks()
    {
        var newer = Sdk("11.0.100-rc.1.26425.128", new Version(5, 11, 0, 0));
        var older = Sdk("10.0.401", new Version(5, 9, 0, 0), helper: false);
        Sdk("9.0.318", new Version(4, 12, 0, 0));
        Assert.Equal(new Version(5, 11, 0, 0), SdkAnalyzers.RoslynReferencedBy(newer));

        var log = new List<string>();
        Assert.Equal(Path.Combine(older, "Generator.dll"), SdkAnalyzers.Resolve(Path.Combine(newer, "Generator.dll"), log.Add, s_host));
        Assert.Null(SdkAnalyzers.Resolve(Path.Combine(newer, "Helper.dll"), log.Add, s_host)); // not in the older folder
        var message = Assert.Single(log);
        Assert.Contains("need version 5.11.0.0 of the C# compiler", message, StringComparison.Ordinal);
        Assert.Contains("the .NET SDK 10.0.401 instead", message, StringComparison.Ordinal);

        // The older SDK's own generator, and anything outside an SDK, stay as they are.
        Assert.Equal(Path.Combine(older, "Generator.dll"), SdkAnalyzers.Resolve(Path.Combine(older, "Generator.dll"), log.Add, s_host));
        var package = Path.Combine(_root, "packages", "x", "1.0.0", "analyzers", "Generator.dll");
        Assert.Equal(package, SdkAnalyzers.Resolve(package, log.Add, s_host));
    }

    [Fact]
    public void Without_a_loadable_alternative_the_generator_stays_and_the_reason_is_logged()
    {
        var newer = Sdk("11.0.100", new Version(5, 11, 0, 0));
        var log = new List<string>();
        Assert.Equal(Path.Combine(newer, "Generator.dll"), SdkAnalyzers.Resolve(Path.Combine(newer, "Generator.dll"), log.Add, s_host));
        Assert.Contains("No other installed .NET SDK", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public void Sdk_folders_are_recognized_by_their_sdk_and_version_segments()
    {
        var folder = Path.Combine(_root, "dotnet", "sdk", "11.0.100", "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators");
        Assert.Equal((Path.Combine(_root, "dotnet", "sdk"), "11.0.100", Path.Combine("Sdks", "Microsoft.NET.Sdk.Razor", "source-generators")), SdkAnalyzers.SdkFolder(folder));
        Assert.Null(SdkAnalyzers.SdkFolder(Path.Combine(_root, "packages", "x", "1.0.0", "analyzers")));
    }
}
