using System.Globalization;
using System.Xml.Linq;
using DaedalusStudio.Plugins.Protocol;

namespace DaedalusStudio.Workbench.Testing;

/// <summary>
/// Visual Studio test results files (<c>.trx</c>, the TeamTest 2010 schema that <c>dotnet test --logger trx</c> and CI
/// systems write): export the results the Test Explorer shows, and import results to show them on the matching tests.
/// </summary>
public static class Trx
{
    private static readonly XNamespace s_ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private const string UnitTestType = "13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b";
    private const string ResultsNotInAList = "8c84fa94-04c1-424b-9868-57a2d4851a1d";
    private const string AllLoadedResults = "19431567-8539-422a-85d7-44ee4e166bda";

    /// <summary>A TRX document for the results of <paramref name="leaves"/> (tests without a result are left out).</summary>
    public static XDocument Export(IEnumerable<TestNode> leaves, string runName, DateTimeOffset now)
    {
        var results = new XElement(s_ns + "Results");
        var definitions = new XElement(s_ns + "TestDefinitions");
        var entries = new XElement(s_ns + "TestEntries");
        int total = 0, passed = 0, failed = 0, skipped = 0;
        foreach (var leaf in leaves)
        {
            if (leaf.Result is not { } result || TestNames.FullyQualifiedName(leaf) is not { } fqn)
            {
                continue;
            }

            var testId = Guid.NewGuid().ToString();
            var executionId = Guid.NewGuid().ToString();
            var className = fqn[..fqn.LastIndexOf('.')];
            var methodName = fqn[(fqn.LastIndexOf('.') + 1)..];
            var displayName = leaf.Label.StartsWith(methodName, StringComparison.Ordinal) ? className + "." + leaf.Label : fqn;
            var outcome = result.Outcome switch
            {
                TestOutcome.Passed => "Passed",
                TestOutcome.Failed or TestOutcome.Errored => "Failed",
                _ => "NotExecuted",
            };
            total++;
            passed += outcome == "Passed" ? 1 : 0;
            failed += outcome == "Failed" ? 1 : 0;
            skipped += outcome == "NotExecuted" ? 1 : 0;

            var output = new XElement(s_ns + "Output");
            if (result.Output.Length > 0)
            {
                output.Add(new XElement(s_ns + "StdOut", result.Output));
            }

            if (result.Messages.Count > 0)
            {
                var message = result.Messages[0];
                output.Add(new XElement(
                    s_ns + "ErrorInfo",
                    new XElement(s_ns + "Message", message.Message),
                    message.StackTrace is { } stack ? new XElement(s_ns + "StackTrace", stack) : null));
            }

            results.Add(new XElement(
                s_ns + "UnitTestResult",
                new XAttribute("executionId", executionId),
                new XAttribute("testId", testId),
                new XAttribute("testName", displayName),
                new XAttribute("computerName", Environment.MachineName),
                new XAttribute("duration", (result.Duration ?? TimeSpan.Zero).ToString("c", CultureInfo.InvariantCulture)),
                new XAttribute("startTime", result.Finished.ToString("o", CultureInfo.InvariantCulture)),
                new XAttribute("endTime", result.Finished.ToString("o", CultureInfo.InvariantCulture)),
                new XAttribute("testType", UnitTestType),
                new XAttribute("outcome", outcome),
                new XAttribute("testListId", ResultsNotInAList),
                output.HasElements ? output : null));
            definitions.Add(new XElement(
                s_ns + "UnitTest",
                new XAttribute("name", displayName),
                new XAttribute("storage", leaf.Root.Label),
                new XAttribute("id", testId),
                new XElement(s_ns + "Execution", new XAttribute("id", executionId)),
                new XElement(
                    s_ns + "TestMethod",
                    new XAttribute("codeBase", leaf.Root.Label),
                    new XAttribute("adapterTypeName", "executor://daedalus"),
                    new XAttribute("className", className),
                    new XAttribute("name", methodName))));
            entries.Add(new XElement(s_ns + "TestEntry", new XAttribute("testId", testId), new XAttribute("executionId", executionId), new XAttribute("testListId", ResultsNotInAList)));
        }

        var stamp = now.ToString("o", CultureInfo.InvariantCulture);
        return new XDocument(new XElement(
            s_ns + "TestRun",
            new XAttribute("id", Guid.NewGuid().ToString()),
            new XAttribute("name", runName),
            new XElement(s_ns + "Times", new XAttribute("creation", stamp), new XAttribute("start", stamp), new XAttribute("finish", stamp)),
            results,
            definitions,
            entries,
            new XElement(
                s_ns + "TestLists",
                new XElement(s_ns + "TestList", new XAttribute("name", "Results Not in a List"), new XAttribute("id", ResultsNotInAList)),
                new XElement(s_ns + "TestList", new XAttribute("name", "All Loaded Results"), new XAttribute("id", AllLoadedResults))),
            new XElement(
                s_ns + "ResultSummary",
                new XAttribute("outcome", failed > 0 ? "Failed" : "Completed"),
                new XElement(
                    s_ns + "Counters",
                    new XAttribute("total", total),
                    new XAttribute("executed", total - skipped),
                    new XAttribute("passed", passed),
                    new XAttribute("failed", failed),
                    new XAttribute("notExecuted", skipped)))));
    }

    /// <summary>The results of a TRX document, by test name.</summary>
    public static IReadOnlyList<ImportedResult> Import(XDocument document, string runId)
    {
        var root = document.Root ?? throw new FormatException("The file has no TestRun element.");
        var ns = root.Name.Namespace;
        var definitions = root.Element(ns + "TestDefinitions")?.Elements(ns + "UnitTest")
            .Where(d => d.Attribute("id") is not null)
            .GroupBy(d => (string)d.Attribute("id")!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        var results = new List<ImportedResult>();
        foreach (var result in root.Element(ns + "Results")?.Descendants(ns + "UnitTestResult") ?? [])
        {
            var testName = (string?)result.Attribute("testName") ?? string.Empty;
            var fqn = testName;
            if ((string?)result.Attribute("testId") is { } testId && definitions.TryGetValue(testId, out var definition)
                && definition.Element(ns + "TestMethod") is { } method)
            {
                var className = ((string?)method.Attribute("className") ?? string.Empty).Split(',')[0].Trim();
                var methodName = (string?)method.Attribute("name") ?? testName;
                fqn = className.Length > 0 ? className + "." + methodName : methodName;
            }

            var outcome = ((string?)result.Attribute("outcome"))?.ToLowerInvariant() switch
            {
                "passed" => TestOutcome.Passed,
                "failed" or "error" or "timeout" or "aborted" => TestOutcome.Failed,
                _ => TestOutcome.Skipped,
            };
            TimeSpan? duration = TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d : null;
            var finished = DateTimeOffset.TryParse((string?)result.Attribute("endTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ? end : DateTimeOffset.Now;
            var output = result.Element(ns + "Output");
            var error = output?.Element(ns + "ErrorInfo");
            var messages = error is null
                ? []
                : new[] { new TestMessageDto((string?)error.Element(ns + "Message") ?? string.Empty, null, null, (string?)error.Element(ns + "StackTrace"), null, null) };
            var stdout = (string?)output?.Element(ns + "StdOut") ?? string.Empty;
            results.Add(new ImportedResult(fqn, testName, new TestResult(outcome, duration, messages, stdout, finished, runId)));
        }

        return results;
    }
}
