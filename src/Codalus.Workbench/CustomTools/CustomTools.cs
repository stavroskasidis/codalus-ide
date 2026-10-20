using Codalus.Rpc;
using Codalus.TextTemplating.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.CustomTools;

/// <summary>Locates <c>codalus-t4host</c>: <c>CODALUS_T4HOST</c>, then <c>t4host/</c> next to the shell.</summary>
public static class TextTemplatingHostLaunch
{
    public const string EnvironmentVariable = "CODALUS_T4HOST";

    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-t4host.exe" : "codalus-t4host";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "t4host", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "t4host", "codalus-t4host.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }
}

/// <summary>
/// The T4 host's connection (ADR 0035): started on the first template, supervised (a crash restarts it), and a transform
/// that takes over two minutes is abandoned by restarting the host.
/// </summary>
public sealed class TextTemplatingService(Func<ProcessLaunchInfo?> launch) : IAsyncDisposable
{
    private static readonly SupervisionPolicy s_policy = new() { MaxRestarts = 5 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupervisedProcess? _process;

    public bool IsRunning => _process?.IsRunning == true;

    public int? ProcessId => _process?.ProcessId;

    public event EventHandler<string>? Log;

    public async Task<T4TransformResult> TransformAsync(T4TransformParams parameters, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(2));
        var json = TextTemplatingJsonContext.Default;
        try
        {
            return await connection.InvokeAsync(TextTemplatingMethods.Transform, parameters, json.T4TransformParams, json.T4TransformResult, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RestartAsync().ConfigureAwait(false);
            return new T4TransformResult(null, ".cs", [new T4Diagnostic(false, "The template ran for more than two minutes and was stopped.", parameters.TemplatePath, 0, 0)]);
        }
        catch (JsonRpcConnectionLostException)
        {
            return new T4TransformResult(null, ".cs", [new T4Diagnostic(false, "The T4 host exited while running the template.", parameters.TemplatePath, 0, 0)]);
        }
    }

    private async Task<JsonRpcConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not { IsRunning: true })
            {
                if (_process is { } stale)
                {
                    await stale.DisposeAsync().ConfigureAwait(false);
                }

                var info = launch() ?? throw new InvalidOperationException("The T4 host (codalus-t4host) was not found.");
                var process = new SupervisedProcess("T4 host", info, s_policy)
                {
                    Initialize = async (connection, _, ct) =>
                    {
                        var json = TextTemplatingJsonContext.Default;
                        await connection.InvokeAsync(TextTemplatingMethods.Initialize, new T4InitializeParams(Environment.ProcessId, TextTemplatingMethods.ProtocolVersion), json.T4InitializeParams, json.T4InitializeResult, ct).ConfigureAwait(false);
                    },
                };
                process.Log += (_, line) => Log?.Invoke(this, line);
                _process = process;
                await process.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            return _process.Connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _process, null) is { } process)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RestartAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>An error or warning of a custom tool, for the Error List.</summary>
public sealed record CustomToolDiagnostic(bool IsWarning, string Message, string File, int Line, int Column);

/// <summary>What a custom tool did: the file it wrote (null when it failed) and its diagnostics.</summary>
public sealed record CustomToolResult(string Input, string Generator, string? Output, IReadOnlyList<CustomToolDiagnostic> Diagnostics)
{
    public bool Succeeded => Output is not null;
}

/// <summary>A plugin's generator (<c>contributes.customTools</c>): its command gets the input and returns the output.</summary>
public sealed record PluginCustomTool(string Name, string Command, string? Extension);

/// <summary>
/// Runs custom tools (single-file generators, ADR 0035) and keeps the project file as Visual Studio does: the input's
/// <c>LastGenOutput</c>, and the output's <c>DesignTime</c>, <c>AutoGen</c> and <c>DependentUpon</c>.
/// </summary>
public sealed class CustomToolService(ProjectItemService items, TextTemplatingService templates)
{
    public const string TextTemplating = "TextTemplatingFileGenerator";
    public const string TextTemplatingPreprocessor = "TextTemplatingFilePreprocessor";

    /// <summary>How long writing the output is tried again while another process has it open.</summary>
    private static readonly TimeSpan s_outputInUseLimit = TimeSpan.FromSeconds(5);

    /// <summary>Plugins' generators by name; the shell sets them from the plugins' manifests.</summary>
    public Func<IReadOnlyList<PluginCustomTool>> PluginTools { get; set; } = () => [];

    /// <summary>Runs a plugin generator's command with <c>{ path, content, namespace, project }</c>; the shell sets it.</summary>
    public Func<PluginCustomTool, string, string, string, string, CancellationToken, Task<(string? Content, string? Extension)>>? RunPluginTool { get; set; }

    /// <summary>Whether a generator name is one Codalus can run.</summary>
    public bool IsKnown(string generator) =>
        generator is ResXGenerator.Internal or ResXGenerator.Public or TextTemplating
        || PluginTools().Any(t => t.Name.Equals(generator, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs <paramref name="generator"/> on <paramref name="file"/> of <paramref name="project"/>. <paramref name="namespace"/>
    /// is the item's <c>CustomToolNamespace</c> or the folder's namespace; <paramref name="trusted"/> allows templates.
    /// </summary>
    public async Task<CustomToolResult> RunAsync(string project, string file, string generator, string @namespace, string rootNamespace, bool trusted, CancellationToken cancellationToken)
    {
        string? content;
        string extension;
        var diagnostics = new List<CustomToolDiagnostic>();
        var stem = Path.GetFileNameWithoutExtension(file);
        if (generator is ResXGenerator.Internal or ResXGenerator.Public)
        {
            try
            {
                var resourceName = ResXGenerator.ManifestResourceName(rootNamespace, Path.GetDirectoryName(project)!, file);
                content = ResXGenerator.Generate(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), ResXGenerator.Identifier(stem), @namespace, resourceName, generator == ResXGenerator.Public);
            }
            catch (System.Xml.XmlException ex)
            {
                diagnostics.Add(new CustomToolDiagnostic(false, ex.Message, file, ex.LineNumber, ex.LinePosition));
                content = null;
            }

            extension = ".Designer.cs";
        }
        else if (generator == TextTemplating)
        {
            if (!trusted)
            {
                return new CustomToolResult(file, generator, null, [new CustomToolDiagnostic(false, "Text templates run only in trusted folders: they run code from the template.", file, 0, 0)]);
            }

            var projectDirectory = Path.GetDirectoryName(project)!;
            var result = await templates.TransformAsync(new T4TransformParams(file, null, @namespace, [projectDirectory], [projectDirectory]), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics.Select(d => new CustomToolDiagnostic(d.IsWarning, d.Message, d.File ?? file, d.Line, d.Column)));
            content = result.Output;
            extension = result.Extension;
        }
        else if (PluginTools().FirstOrDefault(t => t.Name.Equals(generator, StringComparison.OrdinalIgnoreCase)) is { } plugin && RunPluginTool is { } run)
        {
            var (generated, pluginExtension) = await run(plugin, file, await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false), @namespace, project, cancellationToken).ConfigureAwait(false);
            content = generated;
            extension = pluginExtension ?? plugin.Extension ?? ".cs";
            if (content is null)
            {
                diagnostics.Add(new CustomToolDiagnostic(false, $"The custom tool '{generator}' returned nothing.", file, 0, 0));
            }
        }
        else
        {
            return new CustomToolResult(file, generator, null, [new CustomToolDiagnostic(true, $"Codalus IDE has no custom tool named '{generator}'.", file, 0, 0)]);
        }

        if (content is null)
        {
            return new CustomToolResult(file, generator, null, diagnostics);
        }

        var output = Path.Combine(Path.GetDirectoryName(file)!, stem + (extension.StartsWith('.') ? extension : "." + extension));
        await WriteOutputAsync(output, content, cancellationToken).ConfigureAwait(false);
        await items.SetAsync(project, file, null, new Dictionary<string, string?> { ["LastGenOutput"] = Path.GetFileName(output) }, cancellationToken).ConfigureAwait(false);
        if (output.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || output.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
        {
            await items.SetAsync(project, output, null, new Dictionary<string, string?>
            {
                ["DesignTime"] = "True",
                ["AutoGen"] = "True",
                ["DependentUpon"] = Path.GetFileName(file),
            }, cancellationToken).ConfigureAwait(false);
        }

        return new CustomToolResult(file, generator, output, diagnostics);
    }

    /// <summary>
    /// Writes the generated output unless it already holds it. Another process that has the file open for reading without
    /// sharing it for writing (a language server, an indexer, a virus scanner; the shell test polling for the output) makes
    /// the write fail with a sharing violation on Windows. That failed the custom tool, and the save that ran it lost its
    /// regenerated output (PR #74's Windows run), so a file in use is tried again for a few seconds.
    /// </summary>
    private static async Task WriteOutputAsync(string output, string content, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(output) || await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false) != content)
                {
                    await File.WriteAllTextAsync(output, content, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException) && started.Elapsed < s_outputInUseLimit)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(25 * attempt, 250)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
