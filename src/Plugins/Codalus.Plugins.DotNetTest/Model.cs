using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.DotNetTest;

/// <summary>
/// A test as a runner discovered it. <see cref="Key"/> identifies it within its project and framework (VSTest's test
/// case id, or the MTP node uid); <see cref="Type"/> and <see cref="Method"/> are the managed names used to merge it with
/// the source candidates. <see cref="Payload"/> is what the runner needs to run it again (a VSTest <c>TestCase</c> or the
/// MTP node's JSON).
/// </summary>
internal sealed record RunnerTest(
    string Key,
    string Project,
    string? TargetFramework,
    string FullyQualifiedName,
    string DisplayName,
    string? Type,
    string? Method,
    string? File,
    int? Line,
    IReadOnlyList<TestTrait> Traits,
    object Payload);

/// <summary>
/// A test project's build output for one framework, and how its runner starts it. <see cref="TargetFramework"/> is set
/// only for multi-targeted projects (it keys their tests); <see cref="Framework"/> is always the framework built.
/// </summary>
internal sealed record TestTarget(
    string Project,
    string ProjectName,
    string Platform,
    string? TargetFramework,
    string AssemblyPath,
    string? RunCommand,
    string? RunArguments,
    string WorkingDirectory)
{
    public string? Framework { get; init; }
}

/// <summary>What a run does.</summary>
internal enum RunMode
{
    Run,
    Debug,
    Coverage,
}

/// <summary>Options shared by every test process of a run.</summary>
internal sealed record RunOptions(
    RunMode Mode,
    IReadOnlyDictionary<string, string> Environment,
    string? RunSettingsFile,
    string? Architecture,
    bool Parallel)
{
    /// <summary>Coverlet settings for coverage runs (ADR 0011).</summary>
    public CoverageOptions? Coverage { get; init; }
}

internal sealed record CoverageOptions(string OutputDirectory, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);

/// <summary>Receives a runner's progress for one project.</summary>
internal interface IRunReporter
{
    /// <summary>The runner reported a test that discovery didn't (dynamic data, for example).</summary>
    void Discovered(RunnerTest test);

    void Started(RunnerTest test);

    void Finished(RunnerTest test, RunnerOutcome outcome, TimeSpan? duration, IReadOnlyList<TestMessage> messages, string? output);

    /// <summary>Output that belongs to no test (the runner's log).</summary>
    void Output(string text);

    /// <summary>A coverage report (Cobertura XML) the run produced.</summary>
    void CoverageFile(string path);
}

internal enum RunnerOutcome
{
    Passed,
    Failed,
    Errored,
    Skipped,
}

/// <summary>Splits and joins command lines like the .NET runtime does (Windows rules, also used on Unix by dotnet).</summary>
internal static class CommandLine
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return args;
        }

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasArgument = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                }

                hasArgument = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArgument = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasArgument)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArgument = false;
                }
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
        }

        if (hasArgument)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}

/// <summary>Managed names from fully qualified names (when the runner doesn't report them).</summary>
internal static class ManagedNames
{
    /// <summary><c>A.B.Method(1, 2)</c> → (<c>A.B</c>, <c>Method</c>).</summary>
    public static (string? Type, string? Method) FromFullyQualifiedName(string fullyQualifiedName)
    {
        var name = fullyQualifiedName;
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            name = name[..paren];
        }

        var dot = name.LastIndexOf('.');
        return dot <= 0 ? (null, null) : (name[..dot], name[(dot + 1)..]);
    }

    /// <summary><c>Method(System.Int32)</c> → <c>Method</c>; generic arity (<c>Method`1</c>) is dropped too.</summary>
    public static string MethodName(string managedMethod)
    {
        var end = managedMethod.IndexOfAny(['(', '`']);
        return end >= 0 ? managedMethod[..end] : managedMethod;
    }

    /// <summary>The namespace of a managed type name (<c>A.B.C+D</c> → <c>A.B</c>).</summary>
    public static string? Namespace(string type)
    {
        var outer = type.Split('+')[0];
        var dot = outer.LastIndexOf('.');
        return dot <= 0 ? null : outer[..dot];
    }

    /// <summary>The type's name without its namespace, with nested types joined by dots (<c>C.D</c>).</summary>
    public static string TypeLabel(string type)
    {
        var ns = Namespace(type);
        var name = ns is null ? type : type[(ns.Length + 1)..];
        return name.Replace('+', '.');
    }
}
