using Daedalus.Rpc;

namespace Daedalus.Workbench.Languages;

/// <summary>
/// A language service: which files and projects it owns and how to start its host (an LSP server, ADR 0005).
/// <see cref="ProjectLanguage"/> is the design-time build's language (<c>C#</c>, <c>VB</c>, <c>F#</c>) whose projects
/// it receives, or null for services without a project model.
/// </summary>
public sealed record LanguageServiceDescriptor(
    string LanguageId,
    string DisplayName,
    IReadOnlyList<string> FileExtensions,
    string? ProjectLanguage,
    Func<ProcessLaunchInfo?> Launch);

/// <summary>
/// The registered language services. C# is registered by the shell as a built-in.
/// TODO(issue): a <c>languageServers</c> plugin contribution point, so F# and VB services can be plugins (post-MVP).
/// </summary>
public sealed class LanguageServiceRegistry
{
    private readonly List<LanguageServiceDescriptor> _services = [];
    private readonly Lock _gate = new();

    public void Register(LanguageServiceDescriptor descriptor)
    {
        lock (_gate)
        {
            _services.RemoveAll(s => s.LanguageId == descriptor.LanguageId);
            _services.Add(descriptor);
        }
    }

    public IReadOnlyList<LanguageServiceDescriptor> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _services];
            }
        }
    }

    public LanguageServiceDescriptor? ForFile(string path)
    {
        var extension = Path.GetExtension(path);
        lock (_gate)
        {
            return _services.FirstOrDefault(s => s.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        }
    }

    public LanguageServiceDescriptor? ForProjectLanguage(string language)
    {
        lock (_gate)
        {
            return _services.FirstOrDefault(s => s.ProjectLanguage == language);
        }
    }

    public LanguageServiceDescriptor? Get(string languageId)
    {
        lock (_gate)
        {
            return _services.FirstOrDefault(s => s.LanguageId == languageId);
        }
    }
}
