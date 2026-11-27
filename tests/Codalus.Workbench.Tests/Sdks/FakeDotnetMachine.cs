using Codalus.Platform;
using Codalus.Workbench.Sdks;

namespace Codalus.Workbench.Tests.Sdks;

/// <summary>A described machine for the SDK manager: folders and files as paths, nothing on disk.</summary>
internal sealed class FakeDotnetMachine(PlatformKind platform = PlatformKind.Linux, string home = "/home/u") : IDotnetMachine
{
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);

    public PlatformKind Platform => platform;

    public string Home => home;

    /// <summary>The folders the user can write to (others need an administrator).</summary>
    public HashSet<string> Writable { get; } = new(StringComparer.Ordinal);

    public List<string> Deleted { get; } = [];

    private char Separator => platform == PlatformKind.Windows ? '\\' : '/';

    /// <summary>Adds folders, and every folder above each.</summary>
    public FakeDotnetMachine With(params string[] folders)
    {
        foreach (var folder in folders)
        {
            for (var path = folder; path.Length > 1;)
            {
                _folders.Add(path);
                var parent = Parent(path);
                if (parent == path)
                {
                    break;
                }

                path = parent;
            }
        }

        return this;
    }

    public FakeDotnetMachine WithFile(string file)
    {
        _files.Add(file);
        return With(Parent(file));
    }

    /// <summary>A .NET folder with SDKs, and the runtimes, packs and templates each named runtime brings.</summary>
    public FakeDotnetMachine WithRoot(string root, string[] sdks, params (string Framework, string Version)[] runtimes)
    {
        With(root);
        foreach (var sdk in sdks)
        {
            With($"{root}{Separator}sdk{Separator}{sdk}");
        }

        foreach (var (framework, version) in runtimes)
        {
            With($"{root}{Separator}shared{Separator}{framework}{Separator}{version}", $"{root}{Separator}packs{Separator}{framework}.Ref{Separator}{version}");
            if (framework == DotnetFrameworks.NetCore)
            {
                With($"{root}{Separator}packs{Separator}Microsoft.NETCore.App.Host.linux-x64{Separator}{version}", $"{root}{Separator}templates{Separator}{version}");
            }
        }

        return this;
    }

    public bool DirectoryExists(string path) => _folders.Contains(path);

    public IReadOnlyList<string> Subfolders(string path) =>
        [.. _folders.Where(f => Parent(f) == path).Select(f => f[(path.Length + 1)..]).Order(StringComparer.Ordinal)];

    public IReadOnlyList<string> Files(string path) =>
        [.. _files.Where(f => Parent(f) == path).Select(f => f[(path.Length + 1)..]).Order(StringComparer.Ordinal)];

    public bool CanWrite(string path) => Writable.Contains(path);

    public void DeleteDirectory(string path)
    {
        Deleted.Add(path);
        _folders.RemoveWhere(f => f == path || f.StartsWith(path + Separator, StringComparison.Ordinal));
        _files.RemoveWhere(f => f.StartsWith(path + Separator, StringComparison.Ordinal));
    }

    private string Parent(string path)
    {
        var at = path.LastIndexOf(Separator);
        return at <= 0 ? (platform == PlatformKind.Windows ? path : "/") : path[..at];
    }
}
