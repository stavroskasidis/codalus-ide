using System.Security.Cryptography;
using System.Text;

namespace DaedalusStudio.Workbench.Node;

/// <summary>
/// The web language servers (ADR 0024): VS Code's HTML and CSS servers (<c>vscode-langservers-extracted</c>, MIT) and
/// <c>typescript-language-server</c> with TypeScript (Apache-2.0). They are installed on demand with the pinned
/// <see cref="NodeRuntime"/>'s npm, from a lockfile shipped with the shell (<c>npm ci</c> checks every package's
/// integrity hash), into the cache folder. <paramref name="manifestFolder"/> holds that <c>package.json</c> and
/// <c>package-lock.json</c>.
/// </summary>
public sealed class WebLanguageServers(NodeRuntime node, string manifestFolder, string cacheFolder) : IDisposable
{
    private readonly SemaphoreSlim _install = new(1, 1);

    /// <summary>The install folder: one per lockfile, so an updated lockfile installs afresh.</summary>
    public string Folder
    {
        get
        {
            var lockfile = Path.Combine(manifestFolder, "package-lock.json");
            var hash = File.Exists(lockfile) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(lockfile)))[..12] : "missing";
            return Path.Combine(cacheFolder, "web-language-servers", hash);
        }
    }

    public bool IsAvailable => File.Exists(Path.Combine(manifestFolder, "package-lock.json"));

    public bool IsInstalled => node.IsInstalled && File.Exists(Path.Combine(Folder, ".installed")) && File.Exists(HtmlServer) && File.Exists(TypeScriptServer);

    public string NodePath => node.NodePath;

    private string Modules => Path.Combine(Folder, "node_modules");

    public string HtmlServer => Path.Combine(Modules, "vscode-langservers-extracted", "bin", "vscode-html-language-server");

    public string CssServer => Path.Combine(Modules, "vscode-langservers-extracted", "bin", "vscode-css-language-server");

    /// <summary>vscode-eslint's server (ADR 0027); it runs the project's own ESLint.</summary>
    public string EslintServer => Path.Combine(Modules, "vscode-langservers-extracted", "bin", "vscode-eslint-language-server");

    public string TypeScriptServer => Path.Combine(Modules, "typescript-language-server", "lib", "cli.mjs");

    /// <summary>TypeScript's library folder (the <c>tsserver</c> the TypeScript server runs).</summary>
    public string TypeScriptLibrary => Path.Combine(Modules, "typescript", "lib");

    /// <summary>Installs Node.js and the servers if needed. <paramref name="progress"/> gets messages.</summary>
    public async Task EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            return;
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException($"The web language servers' lockfile is missing ({manifestFolder}).");
        }

        await _install.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another process (a second window, a parallel test assembly) may be installing into the same cache.
            using var cacheLock = await CacheLock.AcquireAsync(Folder + ".lock", cancellationToken).ConfigureAwait(false);
            if (IsInstalled)
            {
                return;
            }

            await node.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
            var folder = Folder;
            Directory.CreateDirectory(folder);
            foreach (var file in new[] { "package.json", "package-lock.json" })
            {
                File.Copy(Path.Combine(manifestFolder, file), Path.Combine(folder, file), overwrite: true);
            }

            progress?.Report("Installing the HTML, CSS and TypeScript language servers (npm ci)…");
            var (exitCode, output) = await node.NpmAsync(["ci", "--ignore-scripts", "--no-audit", "--no-fund", "--loglevel=error"], folder, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"npm ci failed (exit code {exitCode}): {Tail(output)}");
            }

            await File.WriteAllTextAsync(Path.Combine(folder, ".installed"), $"Node.js {NodeRuntime.Version}\n", Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            progress?.Report("The web language servers are ready.");
        }
        finally
        {
            _install.Release();
        }
    }

    private static string Tail(string text) => text.Length <= 2000 ? text.Trim() : "…" + text[^2000..].Trim();

    public void Dispose() => _install.Dispose();
}
