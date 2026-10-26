using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Codalus.Interactive.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace Codalus.Interactive.Host;

/// <summary>
/// One C# Interactive state (ADR 0015): a chain of submissions on Roslyn scripting. Each successful submission (one
/// that compiles) extends the chain, even when it throws, so variables declared before the exception stay usable.
/// </summary>
internal sealed class ReplSession
{
    /// <summary>What a new state imports, as the classic IDE's C# Interactive window does.</summary>
    public static readonly string[] DefaultUsings =
        ["System", "System.IO", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading.Tasks"];

    private static readonly CSharpParseOptions ScriptParseOptions = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script);

    private readonly ScriptMetadataResolver _resolver;
    private ScriptOptions _options;
    private ScriptState<object?>? _state;

    private ReplSession(ScriptOptions options, ScriptMetadataResolver resolver, NuGetReferences nuget)
    {
        _options = options;
        _resolver = resolver;
        NuGet = nuget;
    }

    public NuGetReferences NuGet { get; }

    /// <summary>The options for compiling against the current state (completion and tokens use them too).</summary>
    public ScriptOptions Options => _options;

    public ScriptState<object?>? State => _state;

    public static async Task<(ReplSession Session, string[] Errors)> CreateAsync(ResetParams parameters, Action<string> message, CancellationToken cancellationToken)
    {
        var workingDirectory = Directory.Exists(parameters.WorkingDirectory) ? Path.GetFullPath(parameters.WorkingDirectory) : Environment.CurrentDirectory;
        Environment.CurrentDirectory = workingDirectory;
        var nuget = new NuGetReferences(workingDirectory);
        nuget.Message += message;
        var errors = new List<string>();

        var references = new List<MetadataReference>(FrameworkReferences());
        var framework = references.OfType<PortableExecutableReference>().Select(r => Path.GetFileNameWithoutExtension(r.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in parameters.References)
        {
            if (reference.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
            {
                var resolution = nuget.Resolve(reference["nuget:".Length..]);
                if (resolution.Error is { } error)
                {
                    errors.Add(error);
                }

                references.AddRange(resolution.References);
            }
            else if (File.Exists(reference))
            {
                // A project's output folder also holds native libraries and copies of framework assemblies: the
                // framework the host runs on wins, and files without metadata are skipped.
                if (!framework.Contains(Path.GetFileNameWithoutExtension(reference)) && IsAssembly(reference))
                {
                    references.Add(MetadataReference.CreateFromFile(reference));
                }
            }
            else
            {
                errors.Add($"Reference not found: {reference}");
            }
        }

        var resolver = ScriptMetadataResolver.Default.WithBaseDirectory(workingDirectory);
        var options = ScriptOptions.Default
            .WithReferences(references)
            .WithImports(DefaultUsings.Concat(parameters.Usings).Distinct())
            .WithMetadataResolver(new ReplMetadataResolver(resolver, nuget))
            .WithSourceResolver(ScriptSourceResolver.Default.WithBaseDirectory(workingDirectory))
            .WithFilePath(Path.Combine(workingDirectory, "interactive.csx"))
            .WithAllowUnsafe(true)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithEmitDebugInformation(true)
            .WithFileEncoding(System.Text.Encoding.UTF8);

        var session = new ReplSession(options, resolver, nuget);
        session._state = await CSharpScript.RunAsync<object?>(string.Empty, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var file in parameters.LoadFiles)
        {
            var result = await session.ExecuteAsync($"#load {SymbolDisplay.FormatLiteral(file, quote: true)}", cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                errors.Add(result.Error ?? string.Join('\n', result.Diagnostics.Select(d => $"{Path.GetFileName(file)}: {d.Id}: {d.Message}")));
            }
        }

        return (session, [.. errors]);
    }

    /// <summary>Compiles and runs one submission. Compile errors leave the state unchanged.</summary>
    public async Task<ExecuteResult> ExecuteAsync(string code, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        RestorePackages(code);
        var script = _state is null
            ? CSharpScript.Create<object?>(code, _options)
            : _state.Script.ContinueWith<object?>(code, _options);
        var diagnostics = script.Compile(cancellationToken);
        var reported = diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(ToReplDiagnostic)
            .ToArray();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return new ExecuteResult(false, null, null, reported, stopwatch.Elapsed.TotalMilliseconds);
        }

        ScriptState<object?> state;
        try
        {
            state = _state is null
                ? await script.RunAsync(globals: null, catchException: _ => true, cancellationToken).ConfigureAwait(false)
                : await script.RunFromAsync(_state, catchException: _ => true, cancellationToken).ConfigureAwait(false);
        }
        catch (CompilationErrorException e)
        {
            return new ExecuteResult(false, null, null, [.. e.Diagnostics.Select(ToReplDiagnostic)], stopwatch.Elapsed.TotalMilliseconds);
        }

        _state = state;
        if (state.Exception is { } exception)
        {
            return new ExecuteResult(false, null, Format(exception), reported, stopwatch.Elapsed.TotalMilliseconds);
        }

        string? value = null;
        if (HasValue(script.GetCompilation()))
        {
            value = Format(state.ReturnValue);
        }

        return new ExecuteResult(true, value, null, reported, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// A compilation of <paramref name="code"/> as the next submission, for completion, signature help and tokens. It
    /// never restores packages (the text is still being typed): <c>#r "nuget:"</c> only sees packages restored before.
    /// </summary>
    public (Compilation Compilation, SyntaxTree Tree) Pending(string code)
    {
        var options = _options.WithMetadataResolver(new ReplMetadataResolver(_resolver, NuGet, restore: false));
        var script = _state is null
            ? CSharpScript.Create<object?>(code, options)
            : _state.Script.ContinueWith<object?>(code, options);
        var compilation = script.GetCompilation();
        return (compilation, compilation.SyntaxTrees.Last());
    }

    /// <summary>True when the submission ends in an expression without a semicolon whose type isn't void.</summary>
    private static bool HasValue(Compilation compilation)
    {
        var tree = compilation.SyntaxTrees.Last();
        if (tree.GetRoot() is not Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax { Members: [.., Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax { Statement: Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax { SemicolonToken.IsMissing: true } statement }] })
        {
            return false;
        }

        return compilation.GetSemanticModel(tree).GetTypeInfo(statement.Expression).Type is { SpecialType: not SpecialType.System_Void };
    }

    /// <summary>
    /// Restores the packages of the submission's <c>#r "nuget:"</c> directives and adds all their assemblies to the
    /// options: Roslyn binds a <c>#r</c> to a single assembly, but a package brings its dependencies.
    /// </summary>
    private void RestorePackages(string code)
    {
        var root = SyntaxFactory.ParseSyntaxTree(code, ScriptParseOptions).GetCompilationUnitRoot();
        foreach (var directive in root.GetReferenceDirectives())
        {
            var file = directive.File.ValueText;
            if (!file.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolution = NuGet.Resolve(file["nuget:".Length..]);
            if (resolution.Error is { } error)
            {
                Console.Error.WriteLine(error);
            }
            else
            {
                _options = _options.AddReferences(resolution.References);
            }
        }
    }

    internal static string Format(object? value)
    {
        try
        {
            return CSharpObjectFormatter.Instance.FormatObject(value, new PrintOptions { MaximumOutputLength = 16 * 1024 });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"<{value?.GetType().Name}: {e.Message}>";
        }
    }

    internal static string Format(Exception exception)
    {
        try
        {
            return CSharpObjectFormatter.Instance.FormatException(exception);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return exception.ToString();
        }
    }

    private static ReplDiagnostic ToReplDiagnostic(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };
        return new ReplDiagnostic(diagnostic.Id, severity, diagnostic.GetMessage(CultureInfo.InvariantCulture), position.Line, position.Character);
    }

    private static bool IsAssembly(string path)
    {
        try
        {
            _ = System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The shared framework the host runs on (everything in its runtime directory that is a managed assembly), so scripts
    /// see the whole BCL like a project targeting the same framework.
    /// </summary>
    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty).Split(Path.PathSeparator);
        foreach (var path in trusted)
        {
            if (path.Length == 0 || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar), runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("Microsoft.DiaSymReader", StringComparison.Ordinal) || name.EndsWith(".Native", StringComparison.Ordinal))
            {
                continue;
            }

            yield return MetadataReference.CreateFromFile(path);
        }
    }

    /// <summary>Paths and <c>#r "nuget: ..."</c> for <c>#r</c> directives.</summary>
    private sealed class ReplMetadataResolver(ScriptMetadataResolver inner, NuGetReferences nuget, bool restore = true) : MetadataReferenceResolver
    {
        public override bool ResolveMissingAssemblies => inner.ResolveMissingAssemblies;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
        {
            if (!reference.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
            {
                return inner.ResolveReference(reference, baseFilePath, properties);
            }

            // The directive binds to the package's first assembly; RestorePackages added the rest to the options.
            var specification = reference["nuget:".Length..];
            var resolution = restore ? nuget.Resolve(specification) : nuget.Cached(specification);
            return resolution is { References: [var first, ..] } ? [first] : [];
        }

        public override PortableExecutableReference? ResolveMissingAssembly(MetadataReference definition, AssemblyIdentity referenceIdentity) =>
            inner.ResolveMissingAssembly(definition, referenceIdentity);

        public override bool Equals(object? other) =>
            other is ReplMetadataResolver resolver && resolver.Inner.Equals(inner) && ReferenceEquals(resolver.NuGet, nuget) && resolver.Restore == restore;

        public override int GetHashCode() => inner.GetHashCode();

        private ScriptMetadataResolver Inner => inner;

        private NuGetReferences NuGet => nuget;

        private bool Restore => restore;
    }
}
