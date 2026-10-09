using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Daedalus.LanguageServices.Host.Features;

/// <summary>
/// Code fix providers, refactoring providers and IDE analyzers. Roslyn's built-in ones live in the Features assemblies,
/// exported for MEF; they are found by their export attributes and created through their (importing) parameterless
/// constructors, the way other Roslyn hosts outside Visual Studio do it. Providers that need MEF-imported services
/// in their constructor are skipped. Projects' analyzer packages add their own code fixes.
/// </summary>
internal static class ProviderCatalog
{
    private static readonly Assembly[] s_featureAssemblies =
    [
        typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly,
        Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"),
    ];

    private static readonly Lazy<ImmutableArray<CodeFixProvider>> s_builtInFixes = new(() => [.. s_featureAssemblies.SelectMany(a => Create<CodeFixProvider>(a, IsCSharpCodeFix))]);

    private static readonly Lazy<ImmutableArray<CodeRefactoringProvider>> s_refactorings = new(() => [.. s_featureAssemblies.SelectMany(a => Create<CodeRefactoringProvider>(a, IsCSharpRefactoring))]);

    private static readonly Lazy<ImmutableArray<DiagnosticAnalyzer>> s_ideAnalyzers = new(() => [.. s_featureAssemblies.SelectMany(a => Create<DiagnosticAnalyzer>(a, IsCSharpAnalyzer))]);

    private static readonly ConcurrentDictionary<AnalyzerReference, ImmutableArray<CodeFixProvider>> s_packageFixes = new();

    /// <summary>Roslyn's own analyzers behind the IDE's suggestions and fades (IDE0005 unnecessary usings…).</summary>
    public static ImmutableArray<DiagnosticAnalyzer> IdeAnalyzers => s_ideAnalyzers.Value;

    public static ImmutableArray<CodeRefactoringProvider> Refactorings => s_refactorings.Value;

    /// <summary>Built-in code fixes plus those of the project's analyzer packages.</summary>
    public static IEnumerable<CodeFixProvider> CodeFixes(Project project) =>
        s_builtInFixes.Value.Concat(project.AnalyzerReferences.SelectMany(r => s_packageFixes.GetOrAdd(r, PackageFixes)));

    private static ImmutableArray<CodeFixProvider> PackageFixes(AnalyzerReference reference)
    {
        try
        {
            return reference is AnalyzerFileReference file ? [.. Create<CodeFixProvider>(file.GetAssembly(), IsCSharpCodeFix)] : [];
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ReflectionTypeLoadException or FileLoadException)
        {
            return [];
        }
    }

    private static bool IsCSharpCodeFix(Type type) =>
        type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } export && export.Languages.Contains(LanguageNames.CSharp);

    private static bool IsCSharpRefactoring(Type type) =>
        type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>() is { } export && export.Languages.Contains(LanguageNames.CSharp);

    private static bool IsCSharpAnalyzer(Type type) =>
        type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is { } attribute && attribute.Languages.Contains(LanguageNames.CSharp);

    private static IEnumerable<T> Create<T>(Assembly assembly, Func<Type, bool> exported)
        where T : class
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.OfType<Type>()];
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(T).IsAssignableFrom(type) || type.ContainsGenericParameters || !exported(type))
            {
                continue;
            }

            var constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
            if (constructor is null)
            {
                continue;
            }

            T? instance = null;
            try
            {
                instance = (T)constructor.Invoke(null);
            }
            catch (TargetInvocationException)
            {
            }

            if (instance is not null)
            {
                yield return instance;
            }
        }
    }
}
