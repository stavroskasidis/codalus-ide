using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Testing;

/// <summary>Outcomes, ordered so that the "worst" state of a group is the maximum of its children's.</summary>
public enum TestOutcome
{
    /// <summary>Never run (or no result kept).</summary>
    None,

    Passed,

    Skipped,

    /// <summary>Waiting in a running test run.</summary>
    Queued,

    Running,

    Failed,

    /// <summary>Could not run, or threw something other than an assertion.</summary>
    Errored,
}

public static class TestOutcomes
{
    public static TestOutcome Parse(string state) => state switch
    {
        TestStates.Passed => TestOutcome.Passed,
        TestStates.Failed => TestOutcome.Failed,
        TestStates.Errored => TestOutcome.Errored,
        TestStates.Skipped => TestOutcome.Skipped,
        TestStates.Started => TestOutcome.Running,
        TestStates.Enqueued => TestOutcome.Queued,
        _ => TestOutcome.None,
    };

    public static bool IsFinal(this TestOutcome outcome) => outcome is TestOutcome.Passed or TestOutcome.Failed or TestOutcome.Errored or TestOutcome.Skipped;

    public static bool IsFailure(this TestOutcome outcome) => outcome is TestOutcome.Failed or TestOutcome.Errored;

    /// <summary>The name used by <c>outcome:</c> filters and TRX files.</summary>
    public static string Name(this TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => "passed",
        TestOutcome.Failed => "failed",
        TestOutcome.Errored => "error",
        TestOutcome.Skipped => "skipped",
        TestOutcome.Running => "running",
        TestOutcome.Queued => "queued",
        _ => "notrun",
    };
}

/// <summary>
/// A finished result of one test: outcome, duration, messages and the test's own output. <see cref="RunId"/> is the
/// run that produced it (or the TRX file it was imported from).
/// </summary>
public sealed record TestResult(
    TestOutcome Outcome,
    TimeSpan? Duration,
    IReadOnlyList<TestMessageDto> Messages,
    string Output,
    DateTimeOffset Finished,
    string RunId);

/// <summary>
/// A test item mirrored from a controller (ADR 0010): a test, or a group (project, namespace, class, theory).
/// Mutated only by <see cref="TestService"/> under its lock; read freely (fields are replaced, not edited).
/// </summary>
public sealed class TestNode
{
    private readonly List<TestNode> _children = [];

    internal TestNode(string controllerId, string id)
    {
        ControllerId = controllerId;
        Id = id;
        Key = KeyOf(controllerId, id);
    }

    public string ControllerId { get; }

    public string Id { get; }

    /// <summary>Unique across controllers.</summary>
    public string Key { get; }

    public TestNode? Parent { get; internal set; }

    public IReadOnlyList<TestNode> Children => _children;

    internal List<TestNode> MutableChildren => _children;

    public string Label { get; internal set; } = string.Empty;

    public string? Description { get; internal set; }

    public string? Uri { get; internal set; }

    public RangeDto? Range { get; internal set; }

    public IReadOnlyList<TestTraitDto> Traits { get; internal set; } = [];

    public string? SortText { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>The latest finished result.</summary>
    public TestResult? Result { get; internal set; }

    /// <summary>Queued or Running while a run reports on it; None otherwise.</summary>
    public TestOutcome Pending { get; internal set; }

    public bool IsLeaf => _children.Count == 0;

    /// <summary>What to show: a pending state, else the last result's outcome.</summary>
    public TestOutcome Outcome => Pending != TestOutcome.None ? Pending : Result?.Outcome ?? TestOutcome.None;

    /// <summary>The root of this node (a project, for the .NET controller).</summary>
    public TestNode Root
    {
        get
        {
            var node = this;
            while (node.Parent is not null)
            {
                node = node.Parent;
            }

            return node;
        }
    }

    public IEnumerable<TestNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in _children.ToList())
        {
            foreach (var descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }

    public IEnumerable<TestNode> Leaves() => SelfAndDescendants().Where(n => n.IsLeaf);

    /// <summary>The worst outcome among the node's leaves.</summary>
    public TestOutcome AggregateOutcome()
    {
        if (IsLeaf)
        {
            return Outcome;
        }

        var worst = TestOutcome.None;
        var allPassedOrSkipped = true;
        var any = false;
        foreach (var leaf in Leaves())
        {
            any = true;
            var outcome = leaf.Outcome;
            if (outcome > worst)
            {
                worst = outcome;
            }

            allPassedOrSkipped &= outcome is TestOutcome.Passed or TestOutcome.Skipped;
        }

        // A group where everything ran shows Passed even if some leaves were skipped (Skipped > Passed in the order).
        return any && allPassedOrSkipped && worst == TestOutcome.Skipped && Leaves().Any(l => l.Outcome == TestOutcome.Passed) ? TestOutcome.Passed : worst;
    }

    /// <summary>The leaf-or-group path of labels from the root, for display and sorting.</summary>
    public string FullLabel
    {
        get
        {
            var parts = new List<string>();
            for (var node = this; node is not null; node = node.Parent)
            {
                parts.Insert(0, node.Label);
            }

            return string.Join(" › ", parts);
        }
    }

    public static string KeyOf(string controllerId, string id) => controllerId + "\u001f" + id;

    public override string ToString() => Key;
}

/// <summary>A run profile of a controller.</summary>
public sealed record TestProfile(string ControllerId, string Id, string Label, TestRunKind Kind, bool IsDefault);

public enum TestRunKind
{
    Run,
    Debug,
    Coverage,
}

/// <summary>One test run, as the controller reported it.</summary>
public sealed class TestRunRecord
{
    private readonly System.Text.StringBuilder _output = new();
    private readonly List<TestAttachmentDto> _attachments = [];

    internal TestRunRecord(string runId, string controllerId, string name, string? requestId, TestRunKind kind)
    {
        RunId = runId;
        ControllerId = controllerId;
        Name = name;
        RequestId = requestId;
        Kind = kind;
        Started = DateTimeOffset.Now;
    }

    public string RunId { get; }

    public string ControllerId { get; }

    public string Name { get; }

    public string? RequestId { get; }

    public TestRunKind Kind { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset? Ended { get; internal set; }

    /// <summary>Final results by node key.</summary>
    public Dictionary<string, TestResult> Results { get; } = new(StringComparer.Ordinal);

    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    public IReadOnlyList<TestAttachmentDto> Attachments
    {
        get
        {
            lock (_attachments)
            {
                return [.. _attachments];
            }
        }
    }

    internal void AppendOutput(string text)
    {
        lock (_output)
        {
            _output.Append(text);
        }
    }

    internal void AddAttachment(TestAttachmentDto attachment)
    {
        lock (_attachments)
        {
            _attachments.Add(attachment);
        }
    }

    public int Count(TestOutcome outcome) => Results.Values.Count(r => r.Outcome == outcome);
}
