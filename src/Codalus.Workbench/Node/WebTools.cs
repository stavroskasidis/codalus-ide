using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.LanguageServices.Protocol;
using Codalus.Platform;

namespace Codalus.Workbench.Node;

/// <summary>
/// ESLint (ADR 0027): the project's own ESLint, run by vscode-eslint's language server (shipped with the web language
/// servers, ADR 0024) as a linter next to the TypeScript server, for files under an ESLint configuration.
/// </summary>
public static class Eslint
{
    /// <summary>Whether ESLint lints the files it is configured for (default true).</summary>
    public const string Setting = "web.eslint";

    private static readonly string[] s_configs =
    [
        "eslint.config.js", "eslint.config.mjs", "eslint.config.cjs", "eslint.config.ts", "eslint.config.mts", "eslint.config.cts",
        ".eslintrc", ".eslintrc.js", ".eslintrc.cjs", ".eslintrc.json", ".eslintrc.yml", ".eslintrc.yaml",
    ];

    /// <summary>vscode-eslint's server requests (status, a missing library or configuration) that the client acknowledges.</summary>
    public static IReadOnlyList<string> ServerRequests { get; } = ["eslint/openDoc", "eslint/noLibrary", "eslint/noConfig", "eslint/probeFailed", "eslint/confirmESLintExecution"];

    /// <summary>The folder of the nearest ESLint configuration at or above <paramref name="file"/>'s folder, or null.</summary>
    public static string? ConfigFolder(string file) => NearestFolder(file, folder => s_configs.Any(c => File.Exists(Path.Combine(folder, c))));

    internal static string? NearestFolder(string file, Func<string, bool> matches)
    {
        try
        {
            for (var folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (matches(folder))
                {
                    return folder;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return null;
    }

    /// <summary>
    /// The settings vscode-eslint's server asks for (<c>workspace/configuration</c>, section <c>""</c>) for a file:
    /// lint while typing, the folder of its configuration as the workspace, quick fixes that disable a rule.
    /// </summary>
    public static JsonElement Settings(string? fileUri)
    {
        var path = fileUri is null ? null : DocumentUris.ToPath(fileUri);
        var folder = path is null ? null : ConfigFolder(path) ?? Path.GetDirectoryName(path);
        var settings = new JsonObject
        {
            ["validate"] = "on",
            ["packageManager"] = "npm",
            ["useESLintClass"] = false,
            ["experimental"] = new JsonObject { ["useFlatConfig"] = false },
            ["useFlatConfig"] = null,
            ["codeActionOnSave"] = new JsonObject { ["enable"] = false, ["mode"] = "all" },
            ["format"] = false,
            ["quiet"] = false,
            ["onIgnoredFiles"] = "off",
            ["options"] = new JsonObject(),
            ["rulesCustomizations"] = new JsonArray(),
            ["run"] = "onType",
            ["problems"] = new JsonObject { ["shortenToSingleLine"] = false },
            ["nodePath"] = null,
            ["codeAction"] = new JsonObject
            {
                ["disableRuleComment"] = new JsonObject { ["enable"] = true, ["location"] = "separateLine", ["commentStyle"] = "line" },
                ["showDocumentation"] = new JsonObject { ["enable"] = false },
            },
        };
        if (folder is not null)
        {
            settings["workspaceFolder"] = new JsonObject { ["uri"] = DocumentUris.FromPath(folder), ["name"] = Path.GetFileName(folder) };
        }

        using var document = JsonDocument.Parse(settings.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Prettier (ADR 0027): Format Document (and format on save) runs the project's own Prettier when the project uses it,
/// that is when it is installed in <c>node_modules</c> above the file and a Prettier configuration is found.
/// </summary>
public static class Prettier
{
    /// <summary><c>auto</c> (the project's Prettier when it has one) or <c>off</c>.</summary>
    public const string Setting = "web.prettier";

    private static readonly string[] s_configs =
    [
        ".prettierrc", ".prettierrc.json", ".prettierrc.json5", ".prettierrc.yaml", ".prettierrc.yml", ".prettierrc.toml",
        ".prettierrc.js", ".prettierrc.cjs", ".prettierrc.mjs", ".prettierrc.ts", "prettier.config.js", "prettier.config.cjs", "prettier.config.mjs", "prettier.config.ts",
    ];

    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".css", ".scss", ".less", ".html", ".htm", ".vue",
        ".json", ".jsonc", ".json5", ".md", ".markdown", ".yaml", ".yml", ".graphql", ".gql", ".hbs", ".handlebars",
    };

    /// <summary>Whether Prettier formats files like <paramref name="path"/>.</summary>
    public static bool Formats(string path) => s_extensions.Contains(Path.GetExtension(path));

    /// <summary>The folder of the nearest Prettier configuration (or a package.json with a "prettier" key) above <paramref name="file"/>.</summary>
    public static string? ConfigFolder(string file) => Eslint.NearestFolder(file, folder =>
        s_configs.Any(c => File.Exists(Path.Combine(folder, c))) || PackageHasPrettierKey(Path.Combine(folder, "package.json")));

    private static bool PackageHasPrettierKey(string packageJson)
    {
        try
        {
            if (!File.Exists(packageJson))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(packageJson), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("prettier", out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The project's Prettier command-line script above <paramref name="file"/> (Prettier 3, or 2), or null.</summary>
    public static string? Module(string file)
    {
        string? found = null;
        Eslint.NearestFolder(file, folder =>
        {
            var package = Path.Combine(folder, "node_modules", "prettier");
            found = new[] { Path.Combine(package, "bin", "prettier.cjs"), Path.Combine(package, "bin-prettier.js") }.FirstOrDefault(File.Exists);
            return found is not null;
        });
        return found;
    }

    /// <summary>The Prettier that formats <paramref name="file"/>, when its project has one and is configured for it; else null.</summary>
    public static string? For(string file) => Formats(file) && ConfigFolder(file) is not null && Module(file) is { } module ? module : null;

    /// <summary>
    /// Formats <paramref name="text"/> as <paramref name="file"/> with <paramref name="prettier"/> on <paramref name="node"/>
    /// (stdin to stdout, so the file's configuration and ignore rules apply). Throws with Prettier's message when it fails.
    /// </summary>
    public static async Task<string> FormatAsync(string node, string prettier, string file, string text, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(file)!,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add(prettier);
        start.ArgumentList.Add("--stdin-filepath");
        start.ArgumentList.Add(file);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Prettier did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessTree.Kill(process);
            throw;
        }

        var formatted = await output.ConfigureAwait(false);
        return process.ExitCode == 0 ? formatted : throw new InvalidOperationException((await error.ConfigureAwait(false)).Trim() is { Length: > 0 } message ? message : $"Prettier exited with code {process.ExitCode}.");
    }
}
