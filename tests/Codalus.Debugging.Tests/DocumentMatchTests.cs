using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>
/// Which of a PDB's documents a breakpoint's file is (<see cref="ClrModuleInfo.MatchDocument(IEnumerable{string}, string)"/>):
/// the same path, else the only document of that file name in a folder of the same name, or under a path map's root.
/// </summary>
public sealed class DocumentMatchTests
{
    [Fact]
    public void Another_projects_file_of_the_same_name_isnt_the_breakpoints()
    {
        // An Aspire session: each program's debugger gets every file's breakpoints, and the Frontend's PDB has one
        // Program.cs. A breakpoint on the Api's Program.cs line 13 bound at the Frontend's line 13, app.Run(), and the
        // Frontend never started (Linux CI, run 43371744474).
        string[] frontend = ["/tmp/s/samples/Aspire/Frontend/Program.cs", "/tmp/s/samples/Aspire/Frontend/obj/Debug/net10.0/Frontend.AssemblyInfo.cs"];
        Assert.Null(ClrModuleInfo.MatchDocument(frontend, "/tmp/s/samples/Aspire/Api/Program.cs"));
        Assert.Equal(frontend[0], ClrModuleInfo.MatchDocument(frontend, "/tmp/s/samples/Aspire/Frontend/Program.cs"));
    }

    [Theory]
    [InlineData("/tmp/build/samples/Api/Program.cs", "/tmp/copy/samples/Api/Program.cs")] // a build copied elsewhere
    [InlineData("/_/samples/Api/Program.cs", "/home/user/repo/samples/Api/Program.cs")] // a deterministic build (PathMap)
    [InlineData("/_/Program.cs", "/home/user/Api/Program.cs")] // the project's folder mapped to the root
    [InlineData("/_1/Program.cs", "/home/user/Api/Program.cs")]
    [InlineData(@"C:\src\Api\Program.cs", "/home/user/src/Api/Program.cs")] // a Windows build, debugged elsewhere
    public void A_pdb_built_under_other_paths_still_has_the_file(string document, string path) =>
        Assert.Equal(document, ClrModuleInfo.MatchDocument([document, "/other/Helpers.cs"], path));

    [Fact]
    public void Two_documents_of_the_name_are_neither()
    {
        string[] documents = ["/b/One/Program.cs", "/b/Two/Program.cs"];
        Assert.Null(ClrModuleInfo.MatchDocument(documents, "/a/One/Program.cs"));
        Assert.Equal(documents[1], ClrModuleInfo.MatchDocument(documents, "/b/Two/Program.cs"));
    }
}
