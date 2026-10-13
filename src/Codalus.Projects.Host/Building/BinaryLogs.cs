using System.Collections.Concurrent;
using Codalus.Projects.Protocol;
using Microsoft.Build.Logging.StructuredLogger;
using LogProject = Microsoft.Build.Logging.StructuredLogger.Project;
using LogTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace Codalus.Projects.Host.Building;

/// <summary>
/// Open binary logs (ADR 0034), read with MSBuild Structured Log Viewer's <c>StructuredLogger</c> library. The tree goes to
/// the shell a level at a time (<c>binlog/children</c>), so a large log shows its root at once; nodes get ids as they are
/// sent. Search matches node text (and, for diagnostics, their file) case-insensitively.
/// </summary>
internal sealed class BinaryLogs
{
    private readonly ConcurrentDictionary<string, OpenLog> _logs = new(StringComparer.Ordinal);

    private sealed class OpenLog(Build build, string path)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<BaseNode, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly List<BaseNode> _nodes = [];

        public Build Build { get; } = build;

        public string Path { get; } = path;

        public int IdOf(BaseNode node)
        {
            lock (_gate)
            {
                if (!_ids.TryGetValue(node, out var id))
                {
                    id = _nodes.Count;
                    _nodes.Add(node);
                    _ids[node] = id;
                }

                return id;
            }
        }

        public BaseNode? Node(int id)
        {
            lock (_gate)
            {
                return id >= 0 && id < _nodes.Count ? _nodes[id] : null;
            }
        }
    }

    public BinaryLogInfo Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new IOException($"{full} does not exist.");
        }

        var build = BinaryLog.ReadBuild(full);
        build.WaitForBackgroundTasks();
        var id = Guid.NewGuid().ToString("N");
        var log = new OpenLog(build, full);
        _logs[id] = log;
        var errors = 0;
        var warnings = 0;
        var count = 0;
        build.VisitAllChildren<BaseNode>(node =>
        {
            count++;
            if (node is Error)
            {
                errors++;
            }
            else if (node is Warning)
            {
                warnings++;
            }
        });
        return new BinaryLogInfo(id, full, Describe(log, build), build.Succeeded, (long)build.Duration.TotalMilliseconds, errors, warnings, count);
    }

    public BinaryLogChildrenResult Children(BinaryLogChildrenParams parameters)
    {
        var log = Log(parameters.Id);
        return log.Node(parameters.Node) is TreeNode { HasChildren: true } node
            ? new BinaryLogChildrenResult([.. node.Children.Select(c => Describe(log, c))])
            : new BinaryLogChildrenResult([]);
    }

    public SearchBinaryLogResult Search(SearchBinaryLogParams parameters, CancellationToken cancellationToken)
    {
        var log = Log(parameters.Id);
        var query = parameters.Query.Trim();
        var results = new List<BinaryLogSearchResult>();
        var truncated = false;
        if (query.Length == 0)
        {
            return new SearchBinaryLogResult([], false);
        }

        void Visit(BaseNode node)
        {
            if (truncated)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Matches(node, query))
            {
                if (results.Count >= parameters.MaxResults)
                {
                    truncated = true;
                    return;
                }

                // From the root (the build) down to the node.
                var chain = node.GetParentChainIncludingThis().ToList();
                if (chain.Count > 0 && chain[^1] == node && !ReferenceEquals(chain[0], log.Build))
                {
                    chain.Insert(0, log.Build);
                }

                var path = chain.Select(log.IdOf).ToArray();
                results.Add(new BinaryLogSearchResult(Describe(log, node), path));
            }

            if (node is TreeNode { HasChildren: true } tree)
            {
                foreach (var child in tree.Children)
                {
                    Visit(child);
                }
            }
        }

        Visit(log.Build);
        return new SearchBinaryLogResult([.. results], truncated);
    }

    public void Close(string id) => _logs.TryRemove(id, out _);

    private OpenLog Log(string id) =>
        _logs.TryGetValue(id, out var log) ? log : throw new InvalidOperationException("That binary log isn't open.");

    private static bool Matches(BaseNode node, string query) =>
        Text(node).Contains(query, StringComparison.OrdinalIgnoreCase)
        || (node is AbstractDiagnostic { File: { } file } && file.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static BinaryLogNode Describe(OpenLog log, BaseNode node)
    {
        var (file, line) = node switch
        {
            AbstractDiagnostic d => (d.File, d.LineNumber),
            LogProject p => (p.ProjectFile, 0),
            LogTask t => (t.SourceFilePath, t.LineNumber),
            Target t => (t.SourceFilePath, 0),
            _ => ((string?)null, (int?)0),
        };
        return new BinaryLogNode(
            log.IdOf(node),
            node.TypeName,
            Text(node),
            node is TreeNode { HasChildren: true },
            node is TimedNode timed ? (long)timed.Duration.TotalMilliseconds : null,
            string.IsNullOrEmpty(file) ? null : file,
            line ?? 0,
            node is Error,
            node is Warning);
    }

    /// <summary>The node's text as the viewer shows it: a project's name and targets, a property's name and value…</summary>
    private static string Text(BaseNode node)
    {
        var text = node switch
        {
            LogProject p => $"{p.Name} {p.TargetsDisplayText}".Trim(),
            Property p => $"{p.Name} = {p.Value}",
            Metadata m => $"{m.Name} = {m.Value}",
            AbstractDiagnostic d => (d.Code is { Length: > 0 } code ? code + ": " : string.Empty) + d.Text,
            TextNode t => t.Text,
            NamedNode n => n.Name,
            _ => node.Title,
        } ?? node.ToString() ?? string.Empty;
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
