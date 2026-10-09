namespace DaedalusStudio.Plugins.Proposed;

/// <summary>The open solution as the build host evaluated it, plus test discovery from source.</summary>
public interface IWorkspace
{
    /// <summary>The open solution, or null when none is open.</summary>
    Task<WorkspaceSolution?> GetSolutionAsync(CancellationToken cancellationToken);

    /// <summary>A solution was opened or closed, or a project was (re)evaluated. Call <see cref="GetSolutionAsync"/>.</summary>
    event EventHandler? SolutionChanged;

    /// <summary>Documents were edited or saved (debounced), as <c>file:</c> URIs.</summary>
    event EventHandler<DocumentsChangedEventArgs>? DocumentsChanged;

    /// <summary>The project's build output for <paramref name="targetFramework"/> (default: the active framework).</summary>
    Task<ProjectOutput> GetProjectOutputAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken);

    /// <summary>
    /// Test methods and classes found in the documents' syntax (open documents use their unsaved text). Works before
    /// design-time builds and builds.
    /// </summary>
    Task<IReadOnlyList<DocumentTestCandidates>> FindTestCandidatesAsync(IReadOnlyList<string> documentUris, CancellationToken cancellationToken);

    /// <summary>The class, record or struct at a zero-based position of a document, with its methods; null when there is none.</summary>
    Task<TypeOutline?> GetTypeOutlineAsync(string documentUri, int line, int character, CancellationToken cancellationToken);
}

/// <summary>A method's parameter, its type as written in source.</summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Type">Its type as written.</param>
public sealed record ParameterOutline(string Name, string Type);

/// <summary>A public or internal method of a <see cref="TypeOutline"/>.</summary>
/// <param name="Name">The method's name.</param>
/// <param name="ReturnType">Its return type as written.</param>
/// <param name="Parameters">Its parameters.</param>
/// <param name="IsStatic">Whether it is static.</param>
/// <param name="IsAsync">Whether it is async or returns a task.</param>
/// <param name="Line">The zero-based line of its name.</param>
public sealed record MethodOutline(string Name, string ReturnType, IReadOnlyList<ParameterOutline> Parameters, bool IsStatic, bool IsAsync, int Line);

/// <summary>A type of a document, from syntax.</summary>
/// <param name="Name">The type's name.</param>
/// <param name="Namespace">Its namespace, or null.</param>
/// <param name="NestedName"><c>Outer.Inner</c> for nested types; the name otherwise.</param>
/// <param name="IsStatic">Whether it is a static class.</param>
/// <param name="HasParameterlessConstructor">Whether <c>new T()</c> compiles.</param>
/// <param name="Methods">Its public and internal methods.</param>
/// <param name="SelectedMethod">The method at the position, if any.</param>
public sealed record TypeOutline(
    string Name,
    string? Namespace,
    string NestedName,
    bool IsStatic,
    bool HasParameterlessConstructor,
    IReadOnlyList<MethodOutline> Methods,
    string? SelectedMethod);

/// <summary>Arguments of <see cref="IWorkspace.DocumentsChanged"/>.</summary>
public sealed class DocumentsChangedEventArgs(IReadOnlyList<string> uris) : EventArgs
{
    /// <summary>The changed documents.</summary>
    public IReadOnlyList<string> Uris { get; } = uris;
}

/// <summary>
/// The open solution. <see cref="SdkPath"/> is the .NET SDK folder MSBuild uses (it contains
/// <c>vstest.console.dll</c>). Projects are evaluated only when <see cref="Trusted"/>.
/// </summary>
public sealed record WorkspaceSolution(
    string Path,
    bool Trusted,
    string? Configuration,
    string? Platform,
    string? SdkPath,
    IReadOnlyList<WorkspaceProject> Projects);

/// <summary>
/// A project of the solution. <see cref="State"/> is <c>loading</c>, <c>loaded</c>, <c>failed</c>… (as in the
/// Solution Explorer). <see cref="TestPlatform"/> is <c>vstest</c>, <c>mtp</c> (Microsoft.Testing.Platform) or null.
/// <see cref="CompileFiles"/> are absolute paths.
/// </summary>
public sealed record WorkspaceProject(
    string Path,
    string Name,
    string State,
    IReadOnlyList<string> TargetFrameworks,
    string? OutputType,
    bool IsTestProject,
    string? TestPlatform,
    IReadOnlyList<string> CompileFiles,
    IReadOnlyList<string> ProjectReferences);

/// <summary>What a build of the project produces and how <c>dotnet run</c> would start it.</summary>
public sealed record ProjectOutput(
    string Path,
    string? TargetFramework,
    string? TargetPath,
    string? RunCommand,
    string? RunArguments,
    string? RunWorkingDirectory);

/// <summary>
/// A test class or method found in source. <see cref="Type"/> is the managed type name runners report
/// (<c>Namespace.Outer+Inner</c>); <see cref="Name"/> is the method name, or the class's simple name.
/// <see cref="Parameterized"/> marks theories and data-driven tests, whose cases only a runner can list.
/// </summary>
public sealed record TestCandidate(
    string Kind,
    string Name,
    string Type,
    string? Namespace,
    TestRange Range,
    TestRange SelectionRange,
    bool Parameterized)
{
    /// <summary><see cref="Kind"/> of a test class.</summary>
    public const string ClassKind = "class";

    /// <summary><see cref="Kind"/> of a test method.</summary>
    public const string MethodKind = "method";
}

/// <summary>The test candidates of one document.</summary>
/// <param name="Uri">The document.</param>
/// <param name="Candidates">Classes first, each followed by its methods, in document order.</param>
public sealed record DocumentTestCandidates(string Uri, IReadOnlyList<TestCandidate> Candidates);

/// <summary>Builds through the shell.</summary>
public interface IBuild
{
    /// <summary>
    /// Builds <paramref name="projects"/> (and what they reference), or the solution when null. Waits for a build in
    /// progress to finish first. Output goes to the Build channel and the Error List.
    /// </summary>
    Task<BuildResult> BuildAsync(IReadOnlyList<string>? projects, CancellationToken cancellationToken);

    /// <summary>Any build finished (also the user's).</summary>
    event EventHandler<BuildFinishedEventArgs>? Finished;
}

/// <summary>How a build ended.</summary>
/// <param name="Succeeded">No errors, and not cancelled.</param>
/// <param name="Canceled">The build was cancelled.</param>
/// <param name="Errors">The number of errors.</param>
/// <param name="Warnings">The number of warnings.</param>
public sealed record BuildResult(bool Succeeded, bool Canceled, int Errors, int Warnings);

/// <summary>Arguments of <see cref="IBuild.Finished"/>. <see cref="Project"/> is null for a solution build.</summary>
public sealed class BuildFinishedEventArgs(string target, string? project, BuildResult result) : EventArgs
{
    /// <summary><c>build</c>, <c>rebuild</c> or <c>clean</c>.</summary>
    public string Target { get; } = target;

    /// <summary>The project built, or null for the solution.</summary>
    public string? Project { get; } = project;

    /// <summary>How it ended.</summary>
    public BuildResult Result { get; } = result;
}

/// <summary>Debug sessions (netcoredbg, ADR 0007). One session at a time.</summary>
public interface IDebug
{
    /// <summary>Starts a program under the debugger; completes once its process id is known.</summary>
    Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken cancellationToken);

    /// <summary>Attaches to a running .NET process.</summary>
    Task<IDebugSession> AttachAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>How to start a program. <see cref="Arguments"/> are passed as they are (no shell parsing).</summary>
public sealed record DebugLaunchOptions(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>Environment variables added to the shell's environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>A debug session started by a plugin.</summary>
public interface IDebugSession
{
    /// <summary>The debuggee's process id.</summary>
    int ProcessId { get; }

    /// <summary>Completes when the session ends, with the exit code when the program exited.</summary>
    Task<int?> Ended { get; }

    /// <summary>Ends the session (terminates a launched program, detaches from an attached one).</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
