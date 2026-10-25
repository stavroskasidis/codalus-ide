using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Codalus.LanguageServices.Host.Features;

/// <summary>
/// Code fix providers, refactoring providers and IDE analyzers. Roslyn's built-in ones live in the Features assemblies,
/// exported for MEF; they are found by their export attributes and created through their (importing) parameterless
/// constructors, the way other Roslyn hosts outside the classic IDE do it. Providers that need MEF-imported services
/// in their constructor are skipped. Projects' analyzer packages add their own code fixes.
/// </summary>
internal static class ProviderCatalog
{
    private static readonly Assembly[] s_featureAssemblies =
    [
        typeof(Microsoft.CodeAnalysis.Completion.CompletionService).Assembly,
        Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"),
        Assembly.Load("Microsoft.CodeAnalysis.VisualBasic.Features"), // M35
    ];

    private static readonly ConcurrentDictionary<string, ImmutableArray<CodeFixProvider>> s_builtInFixes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ImmutableArray<CodeRefactoringProvider>> s_refactorings = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ImmutableArray<DiagnosticAnalyzer>> s_ideAnalyzers = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(AnalyzerReference, string), ImmutableArray<CodeFixProvider>> s_packageFixes = new();

    /// <summary>Roslyn's own analyzers behind the IDE's suggestions and fades (IDE0005 unnecessary usings…), for a language (C# or VB).</summary>
    public static ImmutableArray<DiagnosticAnalyzer> IdeAnalyzers(string language) =>
        s_ideAnalyzers.GetOrAdd(language, l => [.. s_featureAssemblies.SelectMany(a => Create<DiagnosticAnalyzer>(a, t => IsAnalyzer(t, l)))]);

    public static ImmutableArray<CodeRefactoringProvider> Refactorings(string language) =>
        s_refactorings.GetOrAdd(language, l => [.. s_featureAssemblies.SelectMany(a => Create<CodeRefactoringProvider>(a, t => IsRefactoring(t, l)))]);

    /// <summary>Built-in code fixes plus those of the project's analyzer packages, for the project's language.</summary>
    public static IEnumerable<CodeFixProvider> CodeFixes(Project project) =>
        s_builtInFixes.GetOrAdd(project.Language, l => [.. s_featureAssemblies.SelectMany(a => Create<CodeFixProvider>(a, t => IsCodeFix(t, l)))])
            .Concat(project.AnalyzerReferences.SelectMany(r => s_packageFixes.GetOrAdd((r, project.Language), key => PackageFixes(key.Item1, key.Item2))));

    private static ImmutableArray<CodeFixProvider> PackageFixes(AnalyzerReference reference, string language)
    {
        try
        {
            return reference is AnalyzerFileReference file ? [.. Create<CodeFixProvider>(file.GetAssembly(), t => IsCodeFix(t, language))] : [];
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ReflectionTypeLoadException or FileLoadException)
        {
            return [];
        }
    }

    private static bool IsCodeFix(Type type, string language) =>
        type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } export && export.Languages.Contains(language);

    private static bool IsRefactoring(Type type, string language) =>
        type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>() is { } export && export.Languages.Contains(language);

    private static bool IsAnalyzer(Type type, string language) =>
        type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is { } attribute && attribute.Languages.Contains(language);

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
