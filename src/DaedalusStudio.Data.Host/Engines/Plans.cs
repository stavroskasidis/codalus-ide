using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using DaedalusStudio.Data.Protocol;

namespace DaedalusStudio.Data.Host.Engines;

/// <summary>Turns each engine's plan output into <see cref="PlanNode"/> trees (ADR 0037).</summary>
internal static class Plans
{
    private static readonly XNamespace s_showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // ------------------------------------------------------------------ SQL Server showplan XML

    /// <summary>One root per statement: its <c>RelOp</c> tree with estimated (and actual) rows and each operator's cost share.</summary>
    public static PlanNode[] SqlServer(IEnumerable<string> xmls)
    {
        var roots = new List<PlanNode>();
        foreach (var xml in xmls)
        {
            var document = XDocument.Parse(xml);
            foreach (var statement in document.Descendants(s_showplan + "StmtSimple"))
            {
                var total = Double(statement.Attribute("StatementSubTreeCost")?.Value);
                var children = statement.Element(s_showplan + "QueryPlan")?.Elements(s_showplan + "RelOp").Select(r => RelOp(r, total)).ToArray() ?? [];
                roots.Add(new PlanNode(
                    statement.Attribute("StatementType")?.Value ?? "Statement",
                    Shorten(statement.Attribute("StatementText")?.Value),
                    Double(statement.Attribute("StatementEstRows")?.Value),
                    null,
                    total,
                    null,
                    children));
            }
        }

        return [.. roots];
    }

    private static PlanNode RelOp(XElement relOp, double? total)
    {
        var children = ChildRelOps(relOp).Select(r => RelOp(r, total)).ToArray();
        var cost = Double(relOp.Attribute("EstimatedTotalSubtreeCost")?.Value);
        var own = cost is { } c ? Math.Max(0, c - children.Sum(ch => ch.EstimatedCost ?? 0)) : (double?)null;
        var runtime = relOp.Element(s_showplan + "RunTimeInformation")?.Elements(s_showplan + "RunTimeCountersPerThread").ToList();
        double? actual = runtime is { Count: > 0 } ? runtime.Sum(r => Double(r.Attribute("ActualRows")?.Value) ?? 0) : null;
        var objectName = relOp.Descendants(s_showplan + "Object").FirstOrDefault() is { } o
            ? string.Join('.', new[] { o.Attribute("Schema")?.Value, o.Attribute("Table")?.Value, o.Attribute("Index")?.Value }.Where(v => v is { Length: > 0 })).Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal)
            : null;
        var logical = relOp.Attribute("LogicalOp")?.Value;
        var physical = relOp.Attribute("PhysicalOp")?.Value ?? "Operator";
        var detail = string.Join(" · ", new[] { logical != physical ? logical : null, objectName }.Where(v => v is { Length: > 0 }));
        return new PlanNode(physical, detail.Length == 0 ? null : detail, Double(relOp.Attribute("EstimateRows")?.Value), actual, cost, Percent(own, total), children);
    }

    /// <summary>The RelOps directly under this one (nested in its operator element, not in other RelOps).</summary>
    private static IEnumerable<XElement> ChildRelOps(XElement relOp)
    {
        foreach (var element in relOp.Elements())
        {
            foreach (var child in Nearest(element))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<XElement> Nearest(XElement element)
    {
        if (element.Name == s_showplan + "RelOp")
        {
            yield return element;
            yield break;
        }

        foreach (var child in element.Elements())
        {
            foreach (var found in Nearest(child))
            {
                yield return found;
            }
        }
    }

    // ------------------------------------------------------------------ PostgreSQL EXPLAIN (FORMAT JSON)

    public static PlanNode[] Postgres(string json)
    {
        using var document = JsonDocument.Parse(json);
        var roots = new List<PlanNode>();
        foreach (var statement in document.RootElement.EnumerateArray())
        {
            if (statement.TryGetProperty("Plan", out var plan))
            {
                var total = Number(plan, "Total Cost");
                roots.Add(PostgresNode(plan, total));
            }
        }

        return [.. roots];
    }

    private static PlanNode PostgresNode(JsonElement plan, double? total)
    {
        var children = plan.TryGetProperty("Plans", out var plans) ? plans.EnumerateArray().Select(p => PostgresNode(p, total)).ToArray() : [];
        var cost = Number(plan, "Total Cost");
        var own = cost is { } c ? Math.Max(0, c - children.Sum(ch => ch.EstimatedCost ?? 0)) : (double?)null;
        var detail = string.Join(" · ", new[]
        {
            String(plan, "Relation Name") is { } relation ? (String(plan, "Schema") is { } schema ? schema + "." + relation : relation) : null,
            String(plan, "Index Name") is { } index ? "index " + index : null,
            String(plan, "Join Type"),
            String(plan, "Filter") is { } filter ? "filter " + filter : null,
            String(plan, "Index Cond") is { } cond ? cond : null,
        }.Where(v => v is { Length: > 0 }));
        var loops = Number(plan, "Actual Loops") ?? 1;
        var actual = Number(plan, "Actual Rows") is { } rows ? rows * loops : (double?)null;
        return new PlanNode(String(plan, "Node Type") ?? "Node", detail.Length == 0 ? null : detail, Number(plan, "Plan Rows"), actual, cost, Percent(own, total), children);
    }

    // ------------------------------------------------------------------ MySQL and MariaDB EXPLAIN FORMAT=JSON

    /// <summary>MySQL's JSON plan is a tree of named blocks (<c>query_block</c>, <c>nested_loop</c>, <c>table</c>…): each object becomes a node.</summary>
    public static PlanNode[] MySqlJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [MySqlNode("query", document.RootElement)];
    }

    private static PlanNode MySqlNode(string name, JsonElement element)
    {
        var children = new List<PlanNode>();
        var details = new List<string>();
        double? rows = null;
        double? actualRows = null;
        double? cost = null;
        if (element.ValueKind == JsonValueKind.Array)
        {
            children.AddRange(element.EnumerateArray().Select(item => MySqlNode(name, item)));
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Object when property.Name is "cost_info":
                        cost = Number(property.Value, "query_cost") ?? Number(property.Value, "prefix_cost");
                        break;
                    case JsonValueKind.Object or JsonValueKind.Array when property.Name is not ("used_columns" or "possible_keys" or "used_key_parts" or "key_length"):
                        children.Add(MySqlNode(property.Name, property.Value));
                        break;
                    case JsonValueKind.Number when property.Name is "rows_examined_per_scan" or "rows":
                        rows = property.Value.GetDouble();
                        break;
                    case JsonValueKind.Number when property.Name is "r_rows":
                        actualRows = property.Value.GetDouble();
                        break;
                    case JsonValueKind.String when property.Name is "table_name" or "access_type" or "key" or "attached_condition" or "message":
                        details.Add(property.Name == "access_type" ? "access " + property.Value.GetString() : property.Value.GetString()!);
                        break;
                }
            }
        }

        // An array's single child stands for it.
        if (element.ValueKind == JsonValueKind.Array && children.Count == 1)
        {
            return children[0];
        }

        return new PlanNode(name.Replace('_', ' '), details.Count == 0 ? null : string.Join(" · ", details), rows, actualRows, cost, null, [.. children]);
    }

    /// <summary>MySQL 8's <c>EXPLAIN ANALYZE</c> tree: lines starting with <c>-&gt; </c>, nested by indentation.</summary>
    public static PlanNode[] MySqlTree(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Where(l => l.TrimStart().StartsWith("->", StringComparison.Ordinal))
            .Select(l => (Depth: l.Length - l.TrimStart().Length, Text: l.TrimStart()[2..].Trim()))
            .ToList();
        var index = 0;
        List<PlanNode> Level(int depth)
        {
            var nodes = new List<PlanNode>();
            while (index < lines.Count && lines[index].Depth >= depth)
            {
                var (lineDepth, line) = lines[index++];
                var children = index < lines.Count && lines[index].Depth > lineDepth ? Level(lines[index].Depth) : [];
                var paren = line.IndexOf("  (", StringComparison.Ordinal);
                var operation = paren > 0 ? line[..paren] : line;
                double? estimated = Match(line, @"rows=([\d.e+]+)", 0);
                double? actualRows = Match(line, @"\(actual time=[^)]*rows=([\d.e+]+)", 0);
                nodes.Add(new PlanNode(operation, paren > 0 ? line[(paren + 2)..] : null, estimated, actualRows, Match(line, @"cost=([\d.e+]+)", 0), null, [.. children]));
            }

            return nodes;
        }

        return [.. Level(0)];
    }

    // ------------------------------------------------------------------ SQLite EXPLAIN QUERY PLAN

    /// <summary>Rows of (id, parent, notused, detail).</summary>
    public static PlanNode[] Sqlite(IReadOnlyList<(long Id, long Parent, string Detail)> rows)
    {
        PlanNode[] Children(long parent) => [.. rows.Where(r => r.Parent == parent).Select(r => new PlanNode(Operation(r.Detail), r.Detail, null, null, null, null, Children(r.Id)))];
        static string Operation(string detail) => detail.Split(' ', 2)[0] switch
        {
            "SCAN" => "Scan",
            "SEARCH" => "Search",
            "USE" => "Use",
            "USING" => "Using",
            "COMPOUND" => "Compound",
            "CORRELATED" or "SCALAR" => "Subquery",
            "MULTI-INDEX" => "Multi-index",
            var word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant(),
        };
        return Children(0);
    }

    // ------------------------------------------------------------------ helpers

    private static double? Percent(double? own, double? total) => own is { } o && total is > 0 ? Math.Round(100 * o / total.Value, 1) : null;

    private static double? Double(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number ? value.GetDouble() : value.ValueKind == JsonValueKind.String ? Double(value.GetString()) : null
            : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Match(string text, string pattern, int _)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success ? Double(match.Groups[1].Value) : null;
    }

    private static string? Shorten(string? text) => text is null ? null : text.Length > 200 ? text[..200] + "…" : text;
}
