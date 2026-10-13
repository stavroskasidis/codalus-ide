using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>File URIs from other servers: VS Code's servers escape the drive's colon and lower-case the letter.</summary>
public sealed class DocumentUrisTests
{
    [Fact]
    public void Escaped_and_lower_case_drive_letters_give_the_same_path()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "site", "app.ts"));
        var ours = DocumentUris.FromPath(path);
        Assert.Equal(path, DocumentUris.ToPath(ours));
        if (OperatingSystem.IsWindows())
        {
            var drive = char.ToLowerInvariant(path[0]);
            var rest = path[2..].Replace('\\', '/');
            Assert.Equal(path, DocumentUris.ToPath($"file:///{drive}%3A{rest}"));
            Assert.Equal(path, DocumentUris.ToPath($"file:///{drive}:{rest}"));
        }

        Assert.Null(DocumentUris.ToPath("untitled:Untitled-1"));
    }
}
