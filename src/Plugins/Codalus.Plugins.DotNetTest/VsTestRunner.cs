using System.Xml.Linq;
using Codalus.Plugins.Proposed;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using VsTestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;

namespace Codalus.Plugins.DotNetTest;

/// <summary>
/// VSTest through <c>Microsoft.TestPlatform.TranslationLayer</c> (ADR 0010): one <c>vstest.console</c> session from the
/// SDK, started on first use; one operation at a time. Debugging launches the test host under the shell's debugger
/// through a custom test host launcher.
/// </summary>
internal sealed class VsTestRunner(IDebug debug, Action<string> log) : IDisposable
{
    private static readonly TimeSpan s_abortAfter = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private VsTestConsoleWrapper? _wrapper;
    private string? _wrapperPath;
    private bool _connected;

    /// <summary>
    /// Discovers the tests of <paramref name="targets"/> (one source per target). Sources of different frameworks go in
    /// separate requests: vstest.console aborts a design-mode request that mixes them. <c>Complete</c> is false when a
    /// request was aborted: the tests are then only those found before it stopped, often none.
    /// </summary>
    public async Task<(IReadOnlyList<RunnerTest> Tests, bool Complete)> DiscoverAsync(string sdkPath, IReadOnlyList<TestTarget> targets, RunOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wrapper = Wrapper(sdkPath);
            var results = new List<RunnerTest>();
            var complete = true;
            foreach (var group in targets.GroupBy(t => t.Framework ?? string.Empty))
            {
                var handler = new DiscoveryHandler(log);
                using var registration = cancellationToken.Register(wrapper.CancelDiscovery);
                await wrapper.DiscoverTestsAsync(
                    [.. group.Select(t => t.AssemblyPath)],
                    RunSettings(OlderFrameworks.Adjust(options, group.Key), [.. group], coverage: null),
                    new TestPlatformOptions { CollectMetrics = false },
                    handler).ConfigureAwait(false);

                // The call returns once the request is sent; the results come through the handler.
                await handler.Completed.WaitAsync(cancellationToken).ConfigureAwait(false);
                complete &= !handler.Aborted;
                var bySource = group.ToDictionary(t => t.AssemblyPath, StringComparer.OrdinalIgnoreCase);
                results.AddRange(handler.Tests.Select(c => bySource.TryGetValue(c.Source, out var target) ? ToRunnerTest(c, target) : null).OfType<RunnerTest>());
            }

            return (results, complete);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="tests"/> (null: every test of <paramref name="targets"/>), reporting to the reporter of each
    /// target's project. Debug mode launches each test host under the debugger.
    /// </summary>
    public async Task RunAsync(string sdkPath, IReadOnlyList<TestTarget> targets, IReadOnlyList<RunnerTest>? tests, RunOptions options, Func<TestTarget, IRunReporter> reporters, CancellationToken cancellationToken)
    {
        // One request per framework, as for discovery.
        foreach (var group in targets.GroupBy(t => t.Framework ?? string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sources = group.Select(t => t.AssemblyPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groupTests = tests?.Where(t => sources.Contains(((TestCase)t.Payload).Source)).ToList();
            if (groupTests is { Count: 0 })
            {
                continue;
            }

            // Older frameworks' test hosts may run as x64 or need an ICU override (M33).
            await RunGroupAsync(sdkPath, [.. group], groupTests, OlderFrameworks.Adjust(options, group.Key), reporters, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunGroupAsync(string sdkPath, IReadOnlyList<TestTarget> targets, IReadOnlyList<RunnerTest>? tests, RunOptions options, Func<TestTarget, IRunReporter> reporters, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wrapper = Wrapper(sdkPath);
            var bySource = targets.ToDictionary(t => t.AssemblyPath, StringComparer.OrdinalIgnoreCase);
            var handler = new RunHandler(bySource, reporters, tests);
            var settings = RunSettings(options, targets, options.Coverage);
            var platformOptions = new TestPlatformOptions { CollectMetrics = false };
            using var registration = cancellationToken.Register(() => Cancel(wrapper, handler));
            var testCases = tests?.Select(t => (TestCase)t.Payload).ToList();
            if (options.Mode == RunMode.Debug)
            {
                // The async custom-host request never completes with vstest.console 18.x; the synchronous one works
                // (vstest starts the test host and asks the launcher to attach), so it runs on a worker thread.
                var launcher = new DebugLauncher(debug, log, cancellationToken);
                await Task.Run(
                    () =>
                    {
                        if (testCases is null)
                        {
                            wrapper.RunTestsWithCustomTestHost([.. bySource.Keys], settings, platformOptions, handler, launcher);
                        }
                        else
                        {
                            wrapper.RunTestsWithCustomTestHost(testCases, settings, platformOptions, handler, launcher);
                        }
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            else if (testCases is null)
            {
                await wrapper.RunTestsAsync([.. bySource.Keys], settings, platformOptions, handler).ConfigureAwait(false);
            }
            else
            {
                await wrapper.RunTestsAsync(testCases, settings, platformOptions, handler).ConfigureAwait(false);
            }

            await handler.Completed.WaitAsync(cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            log($"VSTest run finished: {handler.Reported} results.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Cancel(VsTestConsoleWrapper wrapper, RunHandler handler)
    {
        wrapper.CancelTestRun();
        _ = Task.Delay(s_abortAfter).ContinueWith(_ =>
        {
            if (!handler.Completed.IsCompleted)
            {
                wrapper.AbortTestRun();
            }
        }, TaskScheduler.Default);
    }

    private VsTestConsoleWrapper Wrapper(string sdkPath)
    {
        var path = Path.Combine(sdkPath, "vstest.console.dll");
        if (_wrapper is null || !string.Equals(_wrapperPath, path, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"vstest.console was not found in the SDK ({path}).");
            }

            _wrapper?.EndSession();
            _wrapper = new VsTestConsoleWrapper(path, new ConsoleParameters { InheritEnvironmentVariables = true });
            _wrapperPath = path;
            _connected = false;
            _wrapper.StartSession();
        }

        // StartSession only starts vstest.console; the async requests don't wait for it to connect and abort ("socket
        // is null") when sent too early. The synchronous calls do wait, but only VSTEST_CONNECTION_TIMEOUT: when that
        // ran out on a loaded machine the wrapper was kept anyway, and every request sent to it before vstest.console
        // connected aborted (run 39979261891). So until one wait has succeeded, each use waits again, for the same
        // vstest.console (or a new one if it exited).
        if (!_connected)
        {
            _wrapper.InitializeExtensions([]);
            _connected = true;
            log($"VSTest: {path}");
        }

        return _wrapper;
    }

    /// <summary>
    /// The runsettings for a run: the user's file (if any), with <c>RunConfiguration</c> values for parallelism,
    /// architecture, environment variables and the IDE's design mode, plus the Coverlet collector for coverage runs.
    /// </summary>
    internal static string RunSettings(RunOptions options, IReadOnlyList<TestTarget> targets, CoverageOptions? coverage)
    {
        XDocument document;
        try
        {
            document = options.RunSettingsFile is { } file && File.Exists(file) ? XDocument.Load(file) : new XDocument(new XElement("RunSettings"));
        }
        catch (System.Xml.XmlException)
        {
            document = new XDocument(new XElement("RunSettings"));
        }

        var root = document.Root!;
        var configuration = Child(root, "RunConfiguration");
        Child(configuration, "DesignMode").Value = "true";
        Child(configuration, "MaxCpuCount").Value = options.Parallel ? "0" : "1";
        if (options.Architecture is { Length: > 0 } architecture)
        {
            Child(configuration, "TargetPlatform").Value = architecture;
        }

        if (options.Environment.Count > 0)
        {
            var environment = Child(configuration, "EnvironmentVariables");
            foreach (var (name, value) in options.Environment)
            {
                Child(environment, name).Value = value;
            }
        }

        if (coverage is not null)
        {
            var collectorFolder = CoverageTools.CollectorFolder(targets.Count > 0 ? targets[0].Framework : null);
            Child(configuration, "TestAdaptersPaths").Value = collectorFolder;
            Child(configuration, "ResultsDirectory").Value = coverage.OutputDirectory;
            var collectors = Child(Child(root, "DataCollectionRunSettings"), "DataCollectors");
            var collector = new XElement("DataCollector", new XAttribute("friendlyName", "XPlat code coverage"));
            var settings = new XElement("Configuration", new XElement("Format", "cobertura"));
            var include = coverage.Include.Count > 0 ? coverage.Include : CoverageTools.DefaultIncludes(targets);
            if (include.Count > 0)
            {
                settings.Add(new XElement("Include", string.Join(',', include)));
            }

            settings.Add(new XElement("Exclude", string.Join(',', coverage.Exclude.Concat(CoverageTools.DefaultExcludes(targets)))));
            settings.Add(new XElement("ExcludeByAttribute", "GeneratedCodeAttribute,CompilerGeneratedAttribute,ExcludeFromCodeCoverageAttribute"));
            collector.Add(settings);
            collectors.Add(collector);

            // Coverlet's in-process collector flushes the hits in the test host when the session ends. Without it the
            // instrumented assemblies write them on the host's exit, which VSTest cuts short (it kills a host that hasn't
            // exited 100 ms after the session): a loaded machine then reports every line uncovered. `dotnet test
            // --collect` adds it; run settings given to vstest.console don't get it, so we add it as that does
            // (the test host wants its codebase absolute).
            var inProc = Child(Child(root, "InProcDataCollectionRunSettings"), "InProcDataCollectors");
            if (!inProc.Elements("InProcDataCollector").Any(e => ((string?)e.Attribute("assemblyQualifiedName"))?.StartsWith(CoverageTools.InProcCollectorType, StringComparison.Ordinal) == true))
            {
                inProc.Add(new XElement(
                    "InProcDataCollector",
                    new XAttribute("friendlyName", "XPlat Code Coverage"),
                    new XAttribute("assemblyQualifiedName", CoverageTools.InProcCollectorType + ", coverlet.collector, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"),
                    new XAttribute("codebase", Path.Combine(collectorFolder, "coverlet.collector.dll"))));
            }
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Child(XElement parent, string name)
    {
        if (parent.Element(name) is { } existing)
        {
            return existing;
        }

        var child = new XElement(name);
        parent.Add(child);
        return child;
    }

    internal static RunnerTest ToRunnerTest(TestCase testCase, TestTarget target)
    {
        string? type = null;
        string? method = null;
        foreach (var property in testCase.Properties)
        {
            if (property.Id == "TestCase.ManagedType")
            {
                type = testCase.GetPropertyValue(property) as string;
            }
            else if (property.Id == "TestCase.ManagedMethod" && testCase.GetPropertyValue(property) is string managed)
            {
                method = ManagedNames.MethodName(managed);
            }
        }

        if (type is null || method is null)
        {
            (type, method) = ManagedNames.FromFullyQualifiedName(testCase.FullyQualifiedName);
        }

        return new RunnerTest(
            testCase.Id.ToString("N"),
            target.Project,
            target.TargetFramework,
            testCase.FullyQualifiedName,
            testCase.DisplayName,
            type,
            method,
            string.IsNullOrEmpty(testCase.CodeFilePath) ? null : testCase.CodeFilePath,
            testCase.LineNumber > 0 ? testCase.LineNumber : null,
            Traits(testCase),
            testCase);
    }

    private static List<TestTrait> Traits(TestCase testCase)
    {
        var traits = testCase.Traits.Select(t => new TestTrait(t.Name, string.IsNullOrEmpty(t.Value) ? null : t.Value)).ToList();

        // MSTest and NUnit report categories as a property rather than traits.
        foreach (var property in testCase.Properties.Where(p => p.Id is "MSTestDiscoverer.TestCategory" or "NUnit.TestCategory" or "TestObject.Traits"))
        {
            if (testCase.GetPropertyValue(property) is string[] categories)
            {
                traits.AddRange(categories.Select(c => new TestTrait("Category", c)));
            }
        }

        return [.. traits.Distinct()];
    }

    public void Dispose()
    {
        try
        {
            _wrapper?.EndSession();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        _gate.Dispose();
    }

    private sealed class DiscoveryHandler(Action<string> log) : ITestDiscoveryEventsHandler2
    {
        private readonly List<TestCase> _tests = [];
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public bool Aborted { get; private set; }

        public IReadOnlyList<TestCase> Tests
        {
            get
            {
                lock (_tests)
                {
                    return [.. _tests];
                }
            }
        }

        public void HandleDiscoveredTests(IEnumerable<TestCase>? discoveredTestCases)
        {
            lock (_tests)
            {
                _tests.AddRange(discoveredTestCases ?? []);
            }
        }

        public void HandleDiscoveryComplete(DiscoveryCompleteEventArgs discoveryCompleteEventArgs, IEnumerable<TestCase>? lastChunk)
        {
            HandleDiscoveredTests(lastChunk);
            if (discoveryCompleteEventArgs.IsAborted)
            {
                Aborted = true;
                log("VSTest discovery was aborted.");
            }

            _completed.TrySetResult();
        }

        public void HandleLogMessage(TestMessageLevel level, string? message)
        {
            if (level != TestMessageLevel.Informational && message is not null)
            {
                log($"VSTest {level}: {message}");
            }
        }

        public void HandleRawMessage(string rawMessage)
        {
        }
    }

    /// <summary>Streams results to the reporter of each test's project.</summary>
    private sealed class RunHandler(Dictionary<string, TestTarget> targets, Func<TestTarget, IRunReporter> reporters, IReadOnlyList<RunnerTest>? selected) : ITestRunEventsHandler
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<Guid, RunnerTest> _known = selected?.ToDictionary(t => ((TestCase)t.Payload).Id) ?? [];
        private readonly HashSet<Guid> _started = [];
        private int _reported;

        public Task Completed => _completed.Task;

        public int Reported => Volatile.Read(ref _reported);

        public void HandleTestRunStatsChange(TestRunChangedEventArgs? testRunChangedArgs)
        {
            foreach (var active in testRunChangedArgs?.ActiveTests ?? [])
            {
                if (Resolve(active) is { } test && _started.Add(active.Id))
                {
                    Reporter(test)?.Started(test);
                }
            }

            foreach (var result in testRunChangedArgs?.NewTestResults ?? [])
            {
                Report(result);
            }
        }

        public void HandleTestRunComplete(TestRunCompleteEventArgs testRunCompleteArgs, TestRunChangedEventArgs? lastChunkArgs, ICollection<AttachmentSet>? runContextAttachments, ICollection<string>? executorUris)
        {
            foreach (var result in lastChunkArgs?.NewTestResults ?? [])
            {
                Report(result);
            }

            if (testRunCompleteArgs.Error is { } error)
            {
                foreach (var target in targets.Values)
                {
                    reporters(target).Output($"The test run failed: {error.Message}\n");
                }
            }

            foreach (var attachment in (runContextAttachments ?? []).Concat(testRunCompleteArgs.AttachmentSets ?? []).SelectMany(s => s.Attachments))
            {
                if (attachment.Uri.IsFile && attachment.Uri.LocalPath.EndsWith(".cobertura.xml", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var target in targets.Values)
                    {
                        reporters(target).CoverageFile(attachment.Uri.LocalPath);
                        break;
                    }
                }
            }

            _completed.TrySetResult();
        }

        public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo) => -1;

        public void HandleLogMessage(TestMessageLevel level, string? message)
        {
            if (message is null)
            {
                return;
            }

            foreach (var target in targets.Values)
            {
                reporters(target).Output($"[{level}] {message}\n");
                break;
            }
        }

        public void HandleRawMessage(string rawMessage)
        {
        }

        private RunnerTest? Resolve(TestCase testCase)
        {
            if (_known.TryGetValue(testCase.Id, out var test))
            {
                return test;
            }

            if (!targets.TryGetValue(testCase.Source, out var target))
            {
                return null;
            }

            test = ToRunnerTest(testCase, target);
            _known[testCase.Id] = test;
            reporters(target).Discovered(test);
            return test;
        }

        private IRunReporter? Reporter(RunnerTest test) =>
            targets.Values.FirstOrDefault(t => t.Project == test.Project && t.TargetFramework == test.TargetFramework) is { } target ? reporters(target) : null;

        private void Report(VsTestResult result)
        {
            Interlocked.Increment(ref _reported);
            if (Resolve(result.TestCase) is not { } test || Reporter(test) is not { } reporter)
            {
                return;
            }

            var outcome = result.Outcome switch
            {
                TestOutcome.Passed => RunnerOutcome.Passed,
                TestOutcome.Failed => RunnerOutcome.Failed,
                TestOutcome.Skipped or TestOutcome.NotFound or TestOutcome.None => RunnerOutcome.Skipped,
                _ => RunnerOutcome.Errored,
            };
            var messages = new List<TestMessage>();
            if (outcome is RunnerOutcome.Failed or RunnerOutcome.Errored || !string.IsNullOrEmpty(result.ErrorMessage))
            {
                messages.Add(new TestMessage(result.ErrorMessage ?? "The test failed.") { StackTrace = result.ErrorStackTrace });
            }

            var output = string.Concat(result.Messages
                .Where(m => m.Category is not null && !string.IsNullOrEmpty(m.Text))
                .Select(m => m.Category == TestResultMessage.StandardErrorCategory ? "[stderr] " + m.Text : m.Text));

            // Theory cases and data rows that share a test case report their own display names.
            reporter.Finished(test, outcome, result.Duration, messages, output.Length == 0 ? null : output);
        }
    }

    /// <summary>Starts test hosts under the shell's debugger (netcoredbg, ADR 0007).</summary>
    private sealed class DebugLauncher(IDebug debug, Action<string> log, CancellationToken cancellationToken) : ITestHostLauncher3
    {
        public bool IsDebug => true;

        public int LaunchTestHost(TestProcessStartInfo defaultTestHostStartInfo) => LaunchTestHost(defaultTestHostStartInfo, cancellationToken);

        public int LaunchTestHost(TestProcessStartInfo defaultTestHostStartInfo, CancellationToken cancellationToken)
        {
            var environment = defaultTestHostStartInfo.EnvironmentVariables?.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!) ?? [];
            var options = new DebugLaunchOptions(
                defaultTestHostStartInfo.FileName!,
                CommandLine.Split(defaultTestHostStartInfo.Arguments),
                defaultTestHostStartInfo.WorkingDirectory ?? Environment.CurrentDirectory)
            {
                Environment = environment,
            };
            log($"Debugging test host: {options.Program} {defaultTestHostStartInfo.Arguments}");
            var session = debug.LaunchAsync(options, cancellationToken).GetAwaiter().GetResult();
            return session.ProcessId;
        }

        public bool AttachDebuggerToProcess(int pid) => AttachDebuggerToProcess(pid, cancellationToken);

        public bool AttachDebuggerToProcess(int pid, CancellationToken cancellationToken)
        {
            log($"Attaching the debugger to test host {pid}");
            debug.AttachAsync(pid, cancellationToken).GetAwaiter().GetResult();
            return true;
        }

        public bool AttachDebuggerToProcess(AttachDebuggerInfo attachDebuggerInfo, CancellationToken cancellationToken) =>
            AttachDebuggerToProcess(attachDebuggerInfo.ProcessId, cancellationToken);
    }
}
