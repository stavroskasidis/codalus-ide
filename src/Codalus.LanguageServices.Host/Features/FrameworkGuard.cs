using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Whether a document's project, or a project it references, was loaded without its framework: a design-time build
/// that ran before the restore brought the reference pack (no <c>System.Object</c>). Roslyn's completion and Quick Info
/// providers throw on such a compilation, and Roslyn then disables them for the rest of the session; the features
/// answer nothing until the projects load again with their references.
/// </summary>
/// <remarks>
/// A referenced project counts too: the projects get their framework one design-time build at a time, and a Visual
/// Basic project that had its own while the C# library it uses had none yet threw just the same (RestoreRaceTests on
/// macOS CI, job 120942281263: "formatter." never completed again after the restore).
/// </remarks>
internal static class FrameworkGuard
{
    public static async Task<bool> IsMissingAsync(Document document, CancellationToken cancellationToken)
    {
        var project = document.Project;
        if (await IsMissingAsync(project, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // The document's compilation is made from theirs: these are compilations Roslyn has (or needs) anyway.
        foreach (var id in project.Solution.GetProjectDependencyGraph().GetProjectsThatThisProjectTransitivelyDependsOn(project.Id))
        {
            if (project.Solution.GetProject(id) is { } referenced && await IsMissingAsync(referenced, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IsMissingAsync(Project project, CancellationToken cancellationToken) =>
        await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is { } compilation
        && compilation.GetSpecialType(SpecialType.System_Object).TypeKind == TypeKind.Error;
}
