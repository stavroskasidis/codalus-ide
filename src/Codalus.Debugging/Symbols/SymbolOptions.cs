using System.IO.Enumeration;

namespace Codalus.Debugging.Symbols;

/// <summary>Which modules get their symbols looked up without being asked for.</summary>
public enum SymbolModuleFilter
{
    /// <summary>Every module, except those <see cref="SymbolOptions.ExcludedModules"/> names.</summary>
    AllModules,

    /// <summary>Only the modules <see cref="SymbolOptions.IncludedModules"/> names.</summary>
    OnlySpecified,
}

/// <summary>
/// Where the debugger looks for a module's PDB when there is none next to it (M58): folders, symbol servers and the
/// cache the servers' files are kept in. Everything is empty by default, so nothing is searched and nothing leaves the
/// machine until the user lists a folder or checks a server.
/// </summary>
public sealed record SymbolOptions
{
    /// <summary>Microsoft's public symbol server (.NET, Windows).</summary>
    public const string MicrosoftServer = "https://msdl.microsoft.com/download/symbols";

    /// <summary>NuGet.org's symbol server (the <c>.snupkg</c> packages published there).</summary>
    public const string NuGetServer = "https://symbols.nuget.org/download/symbols";

    /// <summary>
    /// Local or UNC folders, searched in order: a PDB directly in the folder, or in the symbol-store layout under it
    /// (<c>name.pdb/id/name.pdb</c>).
    /// </summary>
    public IReadOnlyList<string> SearchPaths { get; init; } = [];

    /// <summary>
    /// Base URLs of symbol servers, asked in order. They must be <c>https://</c>: a plain <c>http://</c> one is refused
    /// unless it is this machine (loopback).
    /// </summary>
    public IReadOnlyList<string> Servers { get; init; } = [];

    /// <summary>The folder downloads are kept in, in the symbol-store layout; null: the servers aren't asked.</summary>
    public string? CacheFolder { get; init; }

    /// <summary>Which of the two lists below decides whether a module is looked up.</summary>
    public SymbolModuleFilter ModuleFilter { get; init; } = SymbolModuleFilter.AllModules;

    /// <summary>
    /// With <see cref="SymbolModuleFilter.AllModules"/>: modules to leave out, as file names with <c>*</c> and <c>?</c>
    /// (<c>Microsoft.*.dll</c>), compared without case.
    /// </summary>
    public IReadOnlyList<string> ExcludedModules { get; init; } = [];

    /// <summary>With <see cref="SymbolModuleFilter.OnlySpecified"/>: the only modules to look up, as file names with <c>*</c> and <c>?</c>.</summary>
    public IReadOnlyList<string> IncludedModules { get; init; } = [];

    /// <summary>Whether the lists let <paramref name="module"/> (a path or a file name) be looked up.</summary>
    public bool Includes(string module)
    {
        var name = SymbolIdentity.FileName(module);
        return ModuleFilter == SymbolModuleFilter.OnlySpecified ? Matches(IncludedModules, name) : !Matches(ExcludedModules, name);
    }

    private static bool Matches(IReadOnlyList<string> patterns, string name)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Trim() is { Length: > 0 } trimmed && FileSystemName.MatchesSimpleExpression(trimmed, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }
}
