using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// Metadata references are made once per unchanged file: with aliases too (a new one each update dropped the
/// project's compilation), and shared with what the host made for Miscellaneous Files.
/// </summary>
public sealed class MetadataReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-metadata-" + Guid.NewGuid().ToString("N"));

    public MetadataReuseTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CopyAssembly(string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy(typeof(MetadataReuseTests).Assembly.Location, path);
        return path;
    }

    [Fact]
    public void An_aliased_reference_is_the_same_instance_until_its_file_changes()
    {
        var dll = CopyAssembly("Aliased.dll");
        var loader = new ProjectLoader();
        var aliased = MetadataReferenceProperties.Assembly.WithAliases(ImmutableArray.Create("Old"));
        var first = loader.Metadata(dll, aliased);
        Assert.NotNull(first);
        Assert.Same(first, loader.Metadata(dll, aliased));
        Assert.Same(first, loader.Metadata(dll, MetadataReferenceProperties.Assembly.WithAliases(ImmutableArray.Create("Old"))));
        Assert.NotSame(first, loader.Metadata(dll, MetadataReferenceProperties.Assembly.WithEmbedInteropTypes(true)));
        Assert.Equal(["Old"], first!.Properties.Aliases);

        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
        Assert.NotSame(first, loader.Metadata(dll, aliased));
    }

    [Fact]
    public void Projects_take_the_references_the_host_shares_while_the_file_is_unchanged()
    {
        var dll = CopyAssembly("Shared.dll");
        var shared = MetadataReference.CreateFromFile(dll);
        ProjectLoader.Share([shared]);
        Assert.Same(shared, new ProjectLoader().Metadata(dll, MetadataReferenceProperties.Assembly));

        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
        Assert.NotSame(shared, new ProjectLoader().Metadata(dll, MetadataReferenceProperties.Assembly));
    }

    [Fact]
    public void A_kept_reference_image_is_read_once_until_it_or_its_documentation_changes()
    {
        var dll = CopyAssembly("Image.dll");
        var xml = Path.Combine(_root, "Image.xml");
        File.WriteAllText(xml, "<doc><members/></doc>");
        var first = LanguageStateCache.Reference(dll, xml);
        Assert.NotNull(first);
        Assert.Same(first, LanguageStateCache.Reference(dll, xml));

        // Its documentation rewritten with the same length: a new time written, a new reference.
        File.WriteAllText(xml, "<doc><members/></doc>");
        File.SetLastWriteTimeUtc(xml, DateTime.UtcNow.AddMinutes(1));
        var second = LanguageStateCache.Reference(dll, xml);
        Assert.NotSame(first, second);

        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(2));
        Assert.NotSame(second, LanguageStateCache.Reference(dll, xml));

        LanguageStateCache.ForgetReferences();
        Assert.NotNull(LanguageStateCache.Reference(dll, xml));
    }

    [Fact]
    public void A_packages_assemblies_are_known_by_its_marker_and_a_broken_one_is_no_reference()
    {
        var package = Path.Combine(_root, "packages", "contoso.lib", "1.0.0");
        Directory.CreateDirectory(Path.Combine(package, "lib", "net10.0"));
        File.WriteAllText(Path.Combine(package, ".nupkg.metadata"), "{}");
        var dll = Path.Combine(package, "lib", "net10.0", "Contoso.Lib.dll");
        File.Copy(typeof(MetadataReuseTests).Assembly.Location, dll);
        Assert.True(ProjectLoader.IsPackageAssembly(dll));
        Assert.False(ProjectLoader.IsPackageAssembly(Path.Combine(_root, "App", "bin", "Debug", "App.dll")));

        // Mapped (or read, on Windows), it binds as a read one does.
        var reference = new ProjectLoader().Metadata(dll, MetadataReferenceProperties.Assembly);
        Assert.NotNull(reference);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Check", references: [reference!]);
        Assert.NotNull(compilation.GetAssemblyOrModuleSymbol(reference!));

        var broken = Path.Combine(package, "lib", "net10.0", "Broken.dll");
        File.WriteAllText(broken, "not an assembly");
        Assert.Null(new ProjectLoader().Metadata(broken, MetadataReferenceProperties.Assembly));
    }
}
