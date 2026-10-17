using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Diagnostics;

/// <summary>
/// A <c>[Benchmark]</c> method (or, with <see cref="IsType"/>, a class with some) found in a source file: its
/// namespace, type (nested types joined with dots) and method, and the 1-based line of its name.
/// </summary>
public sealed record BenchmarkMethod(string? Namespace, string Type, string Method, int Line, bool IsType = false)
{
    public string FullType => Namespace is null ? Type : Namespace + "." + Type;

    /// <summary>BenchmarkDotNet's <c>--filter</c>: the method's full name, or every method of the type.</summary>
    public string Filter => IsType ? FullType + ".*" : FullType + "." + Method;

    public string Title => IsType ? Type : Type + "." + Method;
}

/// <summary>
/// Finds benchmarks in C# source without compiling it (ADR 0046): attribute lists naming <c>Benchmark</c> (not
/// <c>BenchmarkCategory</c>), the method declared after them, and the classes and namespaces around them. Comments,
/// strings and character literals are skipped; it reads the editor's text, so glyphs follow edits.
/// </summary>
public static class BenchmarkFinder
{
    public static IReadOnlyList<BenchmarkMethod> Find(string text)
    {
        var results = new List<BenchmarkMethod>();
        var types = new List<(string Name, int Depth, int Line)>();
        var namespaces = new List<(string Name, int Depth)>();
        string? fileNamespace = null;
        var depth = 0;
        var line = 1;
        var pendingBenchmark = false;
        string? pendingType = null; // "class" / "struct" / "record" just read: the next identifier is its name
        string? pendingNamespace = null;
        var namespaceBuilder = new StringBuilder();
        var typesWithBenchmarks = new HashSet<(string, int)>();
        string? lastIdentifier = null;
        var lastIdentifierLine = 0;
        var i = 0;

        string? CurrentNamespace()
        {
            var parts = new List<string>();
            if (fileNamespace is not null)
            {
                parts.Add(fileNamespace);
            }

            parts.AddRange(namespaces.Select(n => n.Name));
            return parts.Count == 0 ? null : string.Join('.', parts);
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            // Comments and literals.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    line += text[i] == '\n' ? 1 : 0;
                    i++;
                }

                i += 2;
                continue;
            }

            if (c is '"' or '\'')
            {
                var verbatim = i > 0 && text[i - 1] == '@';
                var raw = c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                if (raw)
                {
                    var quotes = 0;
                    while (i < text.Length && text[i] == '"')
                    {
                        quotes++;
                        i++;
                    }

                    var closing = new string('"', quotes);
                    var end = text.IndexOf(closing, i, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + quotes;
                    line += text.AsSpan(i, end - i).Count('\n');
                    i = end;
                    continue;
                }

                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\' && !verbatim)
                    {
                        i++;
                    }
                    else if (text[i] == '\n')
                    {
                        line++;
                        if (!verbatim)
                        {
                            break;
                        }
                    }

                    i++;
                }

                i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@'))
                {
                    i++;
                }

                var word = text[start..i].TrimStart('@');
                if (pendingNamespace is not null)
                {
                    namespaceBuilder.Append(word);
                    continue;
                }

                if (pendingType is not null)
                {
                    types.Add((word, depth, line));
                    pendingType = null;
                    continue;
                }

                switch (word)
                {
                    case "namespace":
                        pendingNamespace = "namespace";
                        namespaceBuilder.Clear();
                        continue;
                    case "class" or "struct" or "record" or "interface":
                        pendingType = word;
                        continue;
                }

                lastIdentifier = word;
                lastIdentifierLine = line;
                continue;
            }

            if (pendingNamespace is not null)
            {
                if (c == '.')
                {
                    namespaceBuilder.Append('.');
                }
                else if (c == ';')
                {
                    fileNamespace = namespaceBuilder.ToString();
                    pendingNamespace = null;
                }
                else if (c == '{')
                {
                    namespaces.Add((namespaceBuilder.ToString(), depth));
                    pendingNamespace = null;
                    depth++;
                }

                i++;
                continue;
            }

            switch (c)
            {
                case '[':
                    {
                        // An attribute list: [Benchmark], [Benchmark(Baseline = true)], [Arguments(1), Benchmark].
                        var end = AttributeEnd(text, i);
                        var list = text[(i + 1)..end];
                        if (NamesBenchmark(list))
                        {
                            pendingBenchmark = true;
                        }

                        line += list.Count(ch => ch == '\n');
                        i = end + 1;
                        continue;
                    }

                case '(':
                    if (pendingBenchmark && lastIdentifier is not null && types.Count > 0)
                    {
                        var type = string.Join('.', types.Select(t => t.Name));
                        results.Add(new BenchmarkMethod(CurrentNamespace(), type, lastIdentifier, lastIdentifierLine));
                        typesWithBenchmarks.Add((type, types[^1].Line));
                    }

                    pendingBenchmark = false;
                    SkipParentheses(text, ref i, ref line);
                    continue;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    while (types.Count > 0 && types[^1].Depth >= depth)
                    {
                        types.RemoveAt(types.Count - 1);
                    }

                    while (namespaces.Count > 0 && namespaces[^1].Depth >= depth)
                    {
                        namespaces.RemoveAt(namespaces.Count - 1);
                    }

                    break;
                case ';' or '=':
                    pendingBenchmark = false; // a field or property, not a method
                    break;
            }

            i++;
        }

        // A glyph for each class with benchmarks, on the line of its name, to run them all.
        foreach (var (type, typeLine) in typesWithBenchmarks)
        {
            var first = results.First(r => r.Type == type);
            results.Add(new BenchmarkMethod(first.Namespace, type, "*", typeLine, IsType: true));
        }

        return [.. results.OrderBy(r => r.Line)];
    }

    private static int AttributeEnd(string text, int open)
    {
        var nesting = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[':
                    nesting++;
                    break;
                case ']':
                    if (--nesting == 0)
                    {
                        return i;
                    }

                    break;
                case '"':
                    i = text.IndexOf('"', i + 1) is >= 0 and var close ? close : text.Length - 1;
                    break;
            }
        }

        return text.Length - 1;
    }

    /// <summary>Whether an attribute list (without its brackets) has <c>Benchmark</c> or <c>BenchmarkAttribute</c>.</summary>
    internal static bool NamesBenchmark(string list)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= list.Length; i++)
        {
            var c = i < list.Length ? list[i] : ',';
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                var attribute = list[start..i];
                var paren = attribute.IndexOf('(', StringComparison.Ordinal);
                var name = (paren >= 0 ? attribute[..paren] : attribute).Trim();
                var colon = name.IndexOf(':', StringComparison.Ordinal);
                name = colon >= 0 ? name[(colon + 1)..].Trim() : name; // [method: Benchmark]
                var dot = name.LastIndexOf('.');
                name = dot >= 0 ? name[(dot + 1)..] : name;
                if (name is "Benchmark" or "BenchmarkAttribute")
                {
                    return true;
                }

                start = i + 1;
            }
        }

        return false;
    }

    private static void SkipParentheses(string text, ref int i, ref int line)
    {
        var nesting = 0;
        for (; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    nesting++;
                    break;
                case ')':
                    if (--nesting == 0)
                    {
                        i++;
                        return;
                    }

                    break;
                case '\n':
                    line++;
                    break;
            }
        }
    }
}

/// <summary>One benchmark's result in a run (times in nanoseconds, as BenchmarkDotNet reports them).</summary>
public sealed record BenchmarkResult(
    string FullName,
    string Type,
    string Method,
    string? Parameters,
    double Mean,
    double? Error,
    double StandardDeviation,
    double Median,
    long? Allocated,
    double? Gen0);

/// <summary>A run of benchmarks: when, which project, filters and job, and the results.</summary>
public sealed record BenchmarkRun(DateTimeOffset At, string Project, IReadOnlyList<string> Filters, string Job, bool InProcess, int ExitCode, IReadOnlyList<BenchmarkResult> Results);

/// <summary>The runs kept for a solution (<c>.codalus/benchmarks/history.json</c>), the newest last.</summary>
public sealed record BenchmarkHistory(IReadOnlyList<BenchmarkRun> Runs);

/// <summary>What a run asks BenchmarkDotNet for.</summary>
public sealed record BenchmarkRequest(string Project, IReadOnlyList<string> Filters, string Job = "Short", bool InProcess = false);

/// <summary>
/// BenchmarkDotNet integration (M18b, ADR 0046): the benchmarks of the solution's projects that reference
/// BenchmarkDotNet, running them (<c>dotnet run -c Release</c> with <c>--filter</c>, the job, the JSON exporter and an
/// artifacts folder of our own), their results read from BenchmarkDotNet's JSON report, and the history of runs. The
/// user's code runs, so the shell asks for a trusted solution first. UI-agnostic; events arrive on any thread.
/// </summary>
/// <remarks>
/// Given the IDE's build (the shell's), the project is built by it first and <c>dotnet run</c> gets <c>--no-build</c>,
/// as <c>dotnet ef</c> and test runs do: the IDE's build waits for the build host's restore and holds off the restores
/// asked for meanwhile, where <c>dotnet run</c>'s own restore ran at the same time as the restore after opening the
/// solution and both wrote the project's <c>obj/*.nuget.g.props</c> ("already exists", the run failed: Linux CI).
/// </remarks>
public sealed class BenchmarkService
{
    public const int MaxRuns = 200;

    /// <summary>The configuration benchmarks build and run in (BenchmarkDotNet measures optimized code).</summary>
    public const string Configuration = "Release";

    private readonly Func<string?> _folder;
    private readonly string _stateFolder;
    private readonly Func<string, CancellationToken, Task<bool>>? _build;
    private readonly Lock _gate = new();
    private Dictionary<string, string> _projectOfFile = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Project, string File, BenchmarkMethod Method)> _all = [];
    private CancellationTokenSource? _running;

    /// <param name="solutionFolder">The open solution's folder (history and artifacts go to its <c>.codalus/benchmarks</c>).</param>
    /// <param name="stateFolder">Where they go without a solution.</param>
    /// <param name="build">
    /// Builds a project in <see cref="Configuration"/> through the IDE's build (the build host's, coordinated with its
    /// restores) and tells whether it succeeded; null: <c>dotnet run</c> restores and builds on its own.
    /// </param>
    public BenchmarkService(Func<string?> solutionFolder, string stateFolder, Func<string, CancellationToken, Task<bool>>? build = null)
    {
        _folder = solutionFolder;
        _stateFolder = stateFolder;
        _build = build;
    }

    /// <summary>The benchmarks found, or a run started or finished.</summary>
    public event EventHandler? Changed;

    /// <summary>A line of a run's output.</summary>
    public event EventHandler<string>? Output;

    public IReadOnlyList<(string Project, string File, BenchmarkMethod Method)> All
    {
        get
        {
            lock (_gate)
            {
                return _all;
            }
        }
    }

    public IReadOnlyList<string> Projects => [.. All.Select(b => b.Project).Distinct(StringComparer.Ordinal)];

    public bool IsRunning => _running is not null;

    public string Root => Path.Combine(_folder() is { } folder ? Path.Combine(folder, ".codalus") : _stateFolder, "benchmarks");

    public string HistoryPath => Path.Combine(Root, "history.json");

    /// <summary>Whether a project references BenchmarkDotNet (its project file names the package).</summary>
    public static bool IsBenchmarkProject(string project)
    {
        try
        {
            return File.Exists(project) && File.ReadAllText(project).Contains("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Finds the benchmarks of <paramref name="projects"/> (those referencing BenchmarkDotNet).</summary>
    public void Refresh(IEnumerable<string> projects)
    {
        var all = new List<(string, string, BenchmarkMethod)>();
        var files = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var project in projects.Where(IsBenchmarkProject))
        {
            var folder = Path.GetDirectoryName(project)!;
            foreach (var file in SourceFiles(folder))
            {
                files[file] = project;
                try
                {
                    all.AddRange(BenchmarkFinder.Find(File.ReadAllText(file)).Where(m => !m.IsType).Select(m => (project, file, m)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        lock (_gate)
        {
            _all = all;
            _projectOfFile = files;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = [.. Directory.EnumerateFiles(at, "*.cs")];
                foreach (var directory in Directory.EnumerateDirectories(at))
                {
                    if (Path.GetFileName(directory) is not ("bin" or "obj" or "BenchmarkDotNet.Artifacts" or ".codalus" or "node_modules") && !Path.GetFileName(directory).StartsWith('.'))
                    {
                        pending.Push(directory);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                yield return entry;
            }
        }
    }

    /// <summary>The benchmark project a source file belongs to, or null (no glyphs then).</summary>
    public string? ProjectOf(string file)
    {
        lock (_gate)
        {
            return _projectOfFile.GetValueOrDefault(file);
        }
    }

    /// <summary>Runs benchmarks, streaming BenchmarkDotNet's output; the run goes to the history.</summary>
    public async Task<BenchmarkRun> RunAsync(BenchmarkRequest request, CancellationToken cancellationToken)
    {
        if (_running is not null)
        {
            throw new InvalidOperationException("Benchmarks are already running.");
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (_build is not null)
            {
                Output?.Invoke(this, $"Building {Path.GetFileNameWithoutExtension(request.Project)} ({Configuration})…");
                var built = await _build(request.Project, running.Token).ConfigureAwait(false);
                running.Token.ThrowIfCancellationRequested();
                if (!built)
                {
                    Output?.Invoke(this, "The build failed; see the Build output and the Error List.");
                    throw new InvalidOperationException($"The build of {Path.GetFileNameWithoutExtension(request.Project)} failed; see the Error List.");
                }
            }

            var at = DateTimeOffset.Now;
            var artifacts = Path.Combine(Root, "runs", at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(artifacts);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(request.Project)!,
            };
            foreach (var argument in Arguments(request, artifacts, noBuild: _build is not null))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            NoLingeringBuildProcesses(start);
            Output?.Invoke(this, "> dotnet " + string.Join(' ', start.ArgumentList));
            int exit;
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var outputEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var errorEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, _) => exited.TrySetResult();
                process.OutputDataReceived += (_, e) => OnLine(e.Data, outputEnded);
                process.ErrorDataReceived += (_, e) => OnLine(e.Data, errorEnded);
                if (!process.Start())
                {
                    throw new InvalidOperationException("dotnet did not start.");
                }

                process.StandardInput.Close(); // BenchmarkDotNet asks which benchmark to run when nothing matches
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (process.HasExited)
                {
                    exited.TrySetResult();
                }

                try
                {
                    // The process's exit, not the end of its output as WaitForExitAsync does (see DrainTimeout).
                    await exited.Task.WaitAsync(running.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }

                await Task.WhenAll(outputEnded.Task, errorEnded.Task).WaitAsync(DrainTimeout, running.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                running.Token.ThrowIfCancellationRequested();
                exit = process.ExitCode;
            }

            var results = ReadResults(artifacts);
            var run = new BenchmarkRun(at, request.Project, request.Filters, request.Job, request.InProcess, exit, results);
            if (results.Count > 0)
            {
                AddToHistory(run);
            }

            return run;
        }
        finally
        {
            _running = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops the run in progress.</summary>
    public void Cancel() => _running?.Cancel();

    /// <summary>
    /// How long a run's output is still read after <c>dotnet</c> exits. A process its build starts (an MSBuild node or
    /// server, the compiler server) can inherit the output pipes and keep them open for many minutes after the run ends,
    /// and WaitForExitAsync waits for the end of the output too, so such a process held the run (as it held
    /// <c>dotnet ef</c>, which the build host's DotnetProcess.DrainAsync bounds the same way). The build is asked to
    /// leave no such process (<see cref="NoLingeringBuildProcesses"/>), and what arrives within this time of the exit is
    /// all of the output.
    /// </summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The variables of a <c>dotnet</c> that builds: nothing it starts outlives it holding its pipes, and its build
    /// doesn't go to MSBuild nodes or an MSBuild server another build left running (a Windows runner logged one that
    /// didn't answer during the run that timed out), so each run builds alone.
    /// </summary>
    internal static void NoLingeringBuildProcesses(ProcessStartInfo start)
    {
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["UseSharedCompilation"] = "false";
    }

    private void OnLine(string? line, TaskCompletionSource ended)
    {
        if (line is null)
        {
            ended.TrySetResult();
        }
        else
        {
            Output?.Invoke(this, line);
        }
    }

    /// <summary>
    /// The <c>dotnet run</c> arguments of a request (BenchmarkDotNet measures optimized code: Release); with
    /// <paramref name="noBuild"/>, the project was built already and <c>dotnet run</c> neither restores nor builds.
    /// </summary>
    public static IReadOnlyList<string> Arguments(BenchmarkRequest request, string artifacts, bool noBuild = false)
    {
        List<string> arguments = ["run", "-c", Configuration, "--project", request.Project];
        if (noBuild)
        {
            arguments.Add("--no-build");
        }

        arguments.AddRange(["--", "--filter", .. request.Filters, "--exporters", "json", "--artifacts", artifacts]);
        if (request.Job is { Length: > 0 } job && job != "Default")
        {
            arguments.AddRange(["--job", job]);
        }

        if (request.InProcess)
        {
            arguments.Add("--inProcess");
        }

        return arguments;
    }

    /// <summary>Reads BenchmarkDotNet's JSON reports (<c>results/*-report-full*.json</c>) in an artifacts folder.</summary>
    public static IReadOnlyList<BenchmarkResult> ReadResults(string artifacts)
    {
        var folder = Path.Combine(artifacts, "results");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var results = new List<BenchmarkResult>();
        foreach (var file in Directory.EnumerateFiles(folder, "*-report*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                results.AddRange(ParseReport(File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        return [.. results.DistinctBy(r => r.FullName)];
    }

    /// <summary>The benchmarks of one JSON report (BenchmarkDotNet's <c>JsonExporter</c>).</summary>
    public static IReadOnlyList<BenchmarkResult> ParseReport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var results = new List<BenchmarkResult>();
        if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks) || benchmarks.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        static double? Number(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        foreach (var benchmark in benchmarks.EnumerateArray())
        {
            if (!benchmark.TryGetProperty("Statistics", out var statistics) || statistics.ValueKind != JsonValueKind.Object)
            {
                continue; // failed: no measurements
            }

            var memory = benchmark.TryGetProperty("Memory", out var m) ? m : default;
            var margin = statistics.TryGetProperty("ConfidenceInterval", out var interval) ? Number(interval, "Margin") : null;
            var bytes = Number(memory, "BytesAllocatedPerOperation");
            var operations = Number(memory, "TotalOperations");
            var gen0 = Number(memory, "Gen0Collections") is { } collections && operations is > 0 ? collections * 1000 / operations : (double?)null;
            var parameters = Text(benchmark, "Parameters");
            results.Add(new BenchmarkResult(
                Text(benchmark, "FullName") ?? $"{Text(benchmark, "Namespace")}.{Text(benchmark, "Type")}.{Text(benchmark, "Method")}",
                Text(benchmark, "Type") ?? string.Empty,
                Text(benchmark, "MethodTitle") ?? Text(benchmark, "Method") ?? string.Empty,
                string.IsNullOrEmpty(parameters) ? null : parameters,
                Number(statistics, "Mean") ?? 0,
                margin,
                Number(statistics, "StandardDeviation") ?? 0,
                Number(statistics, "Median") ?? 0,
                bytes is { } b ? (long)b : null,
                gen0));
        }

        return results;
    }

    public BenchmarkHistory History()
    {
        try
        {
            return File.Exists(HistoryPath) && JsonSerializer.Deserialize(File.ReadAllText(HistoryPath), BenchmarkJsonContext.Default.BenchmarkHistory) is { } history
                ? history
                : new BenchmarkHistory([]);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new BenchmarkHistory([]);
        }
    }

    /// <summary>The results of a benchmark across runs, the oldest first.</summary>
    public IReadOnlyList<(DateTimeOffset At, string Job, BenchmarkResult Result)> HistoryOf(string fullName) =>
        [.. History().Runs.SelectMany(r => r.Results.Where(x => x.FullName == fullName).Select(x => (r.At, r.Job, x)))];

    private void AddToHistory(BenchmarkRun run)
    {
        lock (_gate)
        {
            var runs = History().Runs.Append(run).TakeLast(MaxRuns).ToList();
            Directory.CreateDirectory(Root);
            var temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new BenchmarkHistory(runs), BenchmarkJsonContext.Default.BenchmarkHistory));
            File.Move(temp, HistoryPath, overwrite: true);
        }
    }

    /// <summary>A time in nanoseconds as BenchmarkDotNet shows it (ns, μs, ms, s).</summary>
    public static string FormatTime(double nanoseconds) => nanoseconds switch
    {
        < 1_000 => nanoseconds.ToString("0.00", CultureInfo.CurrentCulture) + " ns",
        < 1_000_000 => (nanoseconds / 1_000).ToString("0.00", CultureInfo.CurrentCulture) + " μs",
        < 1_000_000_000 => (nanoseconds / 1_000_000).ToString("0.00", CultureInfo.CurrentCulture) + " ms",
        _ => (nanoseconds / 1_000_000_000).ToString("0.00", CultureInfo.CurrentCulture) + " s",
    };

    public static string FormatBytes(long? bytes) => bytes switch
    {
        null => "-",
        < 1024 => bytes.Value.ToString(CultureInfo.CurrentCulture) + " B",
        < 1024 * 1024 => (bytes.Value / 1024.0).ToString("0.##", CultureInfo.CurrentCulture) + " KB",
        _ => (bytes.Value / 1024.0 / 1024).ToString("0.##", CultureInfo.CurrentCulture) + " MB",
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(BenchmarkHistory))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
