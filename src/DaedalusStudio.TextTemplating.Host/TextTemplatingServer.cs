using DaedalusStudio.Rpc;
using DaedalusStudio.TextTemplating.Protocol;
using Mono.TextTemplating;

namespace DaedalusStudio.TextTemplating.Host;

/// <summary>
/// The T4 host (ADR 0035): runs text templates with Mono.TextTemplating and its in-process Roslyn compiler. Template code
/// runs here, never in the shell or the build host; the shell starts this process only for trusted folders.
/// </summary>
internal sealed class TextTemplatingServer(JsonRpcConnection connection)
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ExitRequested => _exit.Task;

    public void Register()
    {
        var json = TextTemplatingJsonContext.Default;
        connection.OnRequest(TextTemplatingMethods.Initialize, json.T4InitializeParams, json.T4InitializeResult, (_, _) =>
            Task.FromResult(new T4InitializeResult("daedalus-t4host", typeof(TextTemplatingServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", TextTemplatingMethods.ProtocolVersion)));
        connection.OnRequest(TextTemplatingMethods.Transform, json.T4TransformParams, json.T4TransformResult, (p, ct) => TransformAsync(p, ct));
        connection.OnRequest(TextTemplatingMethods.Shutdown, json.T4Empty, json.T4Empty, (_, _) => Task.FromResult(new T4Empty()));
        connection.OnNotification(TextTemplatingMethods.Exit, json.T4Empty, _ => _exit.TrySetResult());
    }

    internal static async Task<T4TransformResult> TransformAsync(T4TransformParams parameters, CancellationToken cancellationToken)
    {
        var template = Path.GetFullPath(parameters.TemplatePath);
        var content = parameters.Content ?? await File.ReadAllTextAsync(template, cancellationToken).ConfigureAwait(false);
        var generator = new TemplateGenerator();
        generator.UseInProcessCompiler();
        var folder = Path.GetDirectoryName(template)!;
        generator.IncludePaths.Add(folder);
        generator.IncludePaths.AddRange(parameters.IncludeFolders ?? []);
        generator.ReferencePaths.Add(folder);
        generator.ReferencePaths.AddRange(parameters.ReferenceFolders ?? []);
        generator.ReferencePaths.Add(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        if (parameters.Namespace is { Length: > 0 } ns)
        {
            generator.AddParameter(null, null, "namespace", ns);
        }

        foreach (var (name, value) in parameters.Parameters ?? [])
        {
            generator.AddParameter(null, null, name, value);
        }

        // The template parser's errors have 1-based positions; the in-process compiler's (which only runs once the
        // template parses) are 0-based. Parse first to tell them apart.
        generator.ParseTemplate(template, content);
        var parsed = !generator.Errors.HasErrors;
        var defaultOutput = Path.ChangeExtension(template, ".cs");
        string? output = null;
        var outputFile = defaultOutput;
        try
        {
            if (!parsed)
            {
                return new T4TransformResult(null, ".cs", Diagnostics(generator, template, compiled: false));
            }

            generator.Errors.Clear();
            var (file, text, success) = await generator.ProcessTemplateAsync(template, content, defaultOutput, cancellationToken).ConfigureAwait(false);
            outputFile = file ?? defaultOutput;
            output = success ? text : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new T4TransformResult(null, Path.GetExtension(outputFile), [new T4Diagnostic(false, ex.Message, template, 0, 0)]);
        }

        var diagnostics = Diagnostics(generator, template, compiled: true);
        return new T4TransformResult(diagnostics.Any(d => !d.IsWarning) ? null : output, Path.GetExtension(outputFile) is { Length: > 0 } extension ? extension : ".cs", diagnostics);
    }

    /// <summary>The generator's errors; after compiling, those with a file are the compiler's, whose positions are 0-based.</summary>
    private static T4Diagnostic[] Diagnostics(TemplateGenerator generator, string template, bool compiled) =>
        [.. generator.Errors.Cast<System.CodeDom.Compiler.CompilerError>().Select(e =>
        {
            var shift = compiled && !string.IsNullOrEmpty(e.FileName) ? 1 : 0;
            return new T4Diagnostic(e.IsWarning, e.ErrorText, string.IsNullOrEmpty(e.FileName) ? template : e.FileName, e.Line + shift, e.Column + shift);
        })];
}
