using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Whether a document's project was loaded without its framework: a design-time build that ran before the restore
/// brought the reference pack (no <c>System.Object</c>). Roslyn's completion and Quick Info providers throw on such a
/// compilation, and Roslyn then disables them for the rest of the session; the features answer nothing until the project
/// loads again with its references.
/// </summary>
internal static class FrameworkGuard
{
    public static async Task<bool> IsMissingAsync(Document document, CancellationToken cancellationToken) =>
        await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation
        && compilation.GetSpecialType(SpecialType.System_Object).TypeKind == TypeKind.Error;
}
