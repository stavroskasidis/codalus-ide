using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Workbench.Http;

/// <summary>A request in a <c>.http</c> file, as written (variables not yet replaced). Lines are 1-based.</summary>
public sealed record HttpRequestBlock(
    string? Name,
    int StartLine,
    int EndLine,
    int RequestLine,
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body)
{
    /// <summary>The file variables defined above the request, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Variables { get; init; } = [];
}

/// <summary>A parsed <c>.http</c> file: the format of Visual Studio's and VS Code's REST clients (M15a).</summary>
public sealed record HttpFileDocument(IReadOnlyList<HttpRequestBlock> Requests, IReadOnlyList<KeyValuePair<string, string>> Variables)
{
    /// <summary>The request whose block contains <paramref name="line"/>, or null.</summary>
    public HttpRequestBlock? At(int line) => Requests.FirstOrDefault(r => line >= r.StartLine && line <= r.EndLine);
}

/// <summary>
/// Parses <c>.http</c> files: requests separated by <c>###</c>; <c>#</c> and <c>//</c> comments, with
/// <c># @name x</c> naming a request; <c>@variable = value</c> lines; a request line (<c>METHOD URL [HTTP/1.1]</c>,
/// or a URL for GET) with query lines starting with <c>?</c> or <c>&amp;</c>; headers until an empty line; then the body.
/// </summary>
public static partial class HttpFileParser
{
    public static readonly IReadOnlyList<string> Methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    [GeneratedRegex(@"^@([A-Za-z_][\w.\-]*)\s*=\s*(.*)$")]
    private static partial Regex VariableLine();

    [GeneratedRegex(@"^(?:#|//)\s*@name\s+(\S+)")]
    private static partial Regex NameLine();

    [GeneratedRegex(@"^(?:(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS|TRACE|CONNECT)\s+)?(\S.*?)(?:\s+HTTP/[\d.]+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestLineRegex();

    public static HttpFileDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var requests = new List<HttpRequestBlock>();
        var variables = new List<KeyValuePair<string, string>>();
        var start = 0;
        for (var i = 0; i <= lines.Length; i++)
        {
            if (i == lines.Length || lines[i].TrimStart().StartsWith("###", StringComparison.Ordinal))
            {
                if (ParseBlock(lines, start, i, variables) is { } request)
                {
                    requests.Add(request);
                }

                start = i + 1;
            }
        }

        return new HttpFileDocument(requests, variables);
    }

    /// <summary>Whether <paramref name="line"/> is a request line (for the editor's send glyphs).</summary>
    public static bool IsRequestLine(string line)
    {
        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var first = space < 0 ? trimmed : trimmed[..space];
        return Methods.Contains(first.ToUpperInvariant()) && space > 0
               || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("{{", StringComparison.Ordinal) && !trimmed.StartsWith("{{$", StringComparison.Ordinal) && space < 0;
    }

    private static HttpRequestBlock? ParseBlock(string[] lines, int start, int end, List<KeyValuePair<string, string>> variables)
    {
        string? name = null;
        var i = start;
        for (; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (NameLine().Match(line) is { Success: true } named)
            {
                name = named.Groups[1].Value;
                continue;
            }

            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (VariableLine().Match(line) is { Success: true } variable)
            {
                variables.Add(new(variable.Groups[1].Value, variable.Groups[2].Value.Trim()));
                continue;
            }

            break;
        }

        if (i >= end || RequestLineRegex().Match(lines[i].Trim()) is not { Success: true } request)
        {
            return null;
        }

        var requestLine = i;
        var method = request.Groups[1].Success ? request.Groups[1].Value.ToUpperInvariant() : "GET";
        var url = new StringBuilder(request.Groups[2].Value.Trim());
        i++;
        for (; i < end && lines[i].TrimStart() is ['?' or '&', ..] continuation; i++)
        {
            url.Append(continuation.Trim());
        }

        var headers = new List<KeyValuePair<string, string>>();
        for (; i < end && lines[i].Trim().Length > 0; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }

        var bodyLines = new List<string>();
        for (i++; i < end; i++)
        {
            bodyLines.Add(lines[i].TrimEnd('\r'));
        }

        while (bodyLines.Count > 0 && bodyLines[^1].Trim().Length == 0)
        {
            bodyLines.RemoveAt(bodyLines.Count - 1);
        }

        var last = end - 1;
        while (last > requestLine && lines[last].Trim().Length == 0)
        {
            last--;
        }

        return new HttpRequestBlock(name, start + 1, last + 1, requestLine + 1, method, url.ToString(), headers, bodyLines.Count == 0 ? null : string.Join("\n", bodyLines))
        {
            Variables = [.. variables],
        };
    }
}

/// <summary>
/// Environments for <c>.http</c> files: <c>http-client.env.json</c> (and <c>http-client.env.json.user</c>, which
/// overrides it), found in the file's folder or a parent. <c>$shared</c> applies to every environment.
/// </summary>
public sealed record HttpEnvironments(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Values, string? File)
{
    public const string Shared = "$shared";

    public static readonly HttpEnvironments None = new(new Dictionary<string, IReadOnlyDictionary<string, string>>(), null);

    /// <summary>The environment names (without <c>$shared</c>).</summary>
    public IReadOnlyList<string> Names => [.. Values.Keys.Where(k => k != Shared).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The variables of <paramref name="environment"/>, over the shared ones.</summary>
    public IReadOnlyDictionary<string, string> For(string? environment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { Shared, environment })
        {
            if (name is not null && Values.TryGetValue(name, out var values))
            {
                foreach (var (key, value) in values)
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>Reads the environment files that apply to <paramref name="httpFile"/> (stopping at <paramref name="root"/>).</summary>
    public static HttpEnvironments Load(string httpFile, string? root = null)
    {
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(httpFile)); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var file = Path.Combine(folder, "http-client.env.json");
            if (System.IO.File.Exists(file))
            {
                var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                Merge(values, file);
                Merge(values, file + ".user");
                return new HttpEnvironments(values.ToDictionary(v => v.Key, v => (IReadOnlyDictionary<string, string>)v.Value, StringComparer.Ordinal), file);
            }

            if (root is not null && string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return None;
    }

    private static void Merge(Dictionary<string, Dictionary<string, string>> values, string file)
    {
        if (!System.IO.File.Exists(file))
        {
            return;
        }

        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var environment in document.RootElement.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
        {
            if (!values.TryGetValue(environment.Name, out var target))
            {
                values[environment.Name] = target = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            foreach (var variable in environment.Value.EnumerateObject())
            {
                target[variable.Name] = variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString()! : variable.Value.GetRawText();
            }
        }
    }
}

/// <summary>
/// Replaces <c>{{name}}</c> in a request: file variables (which may use each other), the environment, the results of
/// earlier named requests (<c>{{name.response.body.$.path}}</c>, <c>{{name.response.headers.Header}}</c>), and the
/// dynamic variables <c>$guid</c>, <c>$randomInt min max</c>, <c>$timestamp</c>, <c>$datetime</c>,
/// <c>$localDatetime</c> and <c>$processEnv NAME</c>.
/// </summary>
public static partial class HttpVariables
{
    [GeneratedRegex(@"\{\{\s*(.+?)\s*\}\}")]
    private static partial Regex Reference();

    public static string Resolve(
        string text,
        IReadOnlyList<KeyValuePair<string, string>> fileVariables,
        IReadOnlyDictionary<string, string> environment,
        Func<string, HttpExchange?> namedResult,
        ICollection<string>? unresolved = null)
    {
        var values = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        foreach (var (name, value) in fileVariables)
        {
            values[name] = Replace(value, values, namedResult, unresolved, depth: 0);
        }

        return Replace(text, values, namedResult, unresolved, depth: 0);
    }

    private static string Replace(string text, Dictionary<string, string> values, Func<string, HttpExchange?> namedResult, ICollection<string>? unresolved, int depth) =>
        depth > 8 ? text : Reference().Replace(text, match =>
        {
            var expression = match.Groups[1].Value;
            if (Dynamic(expression) is { } dynamic)
            {
                return dynamic;
            }

            if (values.TryGetValue(expression, out var value))
            {
                return Replace(value, values, namedResult, unresolved, depth + 1);
            }

            if (FromResponse(expression, namedResult) is { } fromResponse)
            {
                return fromResponse;
            }

            unresolved?.Add(expression);
            return match.Value;
        });

    private static string? Dynamic(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "$guid":
                return Guid.NewGuid().ToString();
            case "$randomInt":
                var min = parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var a) ? a : 0;
                var max = parts.Length > 2 && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var b) ? b : 1000;
                return Random.Shared.Next(min, Math.Max(min + 1, max)).ToString(CultureInfo.InvariantCulture);
            case "$timestamp":
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            case "$datetime":
                return Format(DateTimeOffset.UtcNow, parts.Length > 1 ? parts[1] : "iso8601");
            case "$localDatetime":
                return Format(DateTimeOffset.Now, parts.Length > 1 ? parts[1] : "iso8601");
            case "$processEnv" when parts.Length > 1:
                return Environment.GetEnvironmentVariable(parts[1]) ?? string.Empty;
            default:
                return null;
        }
    }

    private static string Format(DateTimeOffset time, string format) => format.Trim('"', '\'') switch
    {
        "iso8601" => time.ToString("o", CultureInfo.InvariantCulture),
        "rfc1123" => time.ToString("r", CultureInfo.InvariantCulture),
        var custom => time.ToString(custom, CultureInfo.InvariantCulture),
    };

    /// <summary><c>name.response.body.$.a.b[0]</c>, <c>name.response.body.*</c> or <c>name.response.headers.Name</c>.</summary>
    private static string? FromResponse(string expression, Func<string, HttpExchange?> namedResult)
    {
        var parts = expression.Split('.', 4);
        if (parts.Length < 4 || parts[1] != "response" || namedResult(parts[0]) is not { Response: { } response })
        {
            return null;
        }

        if (parts[2] == "headers")
        {
            return response.Headers.FirstOrDefault(h => string.Equals(h.Key, parts[3], StringComparison.OrdinalIgnoreCase)).Value;
        }

        if (parts[2] != "body")
        {
            return null;
        }

        if (parts[3] == "*")
        {
            return response.Body;
        }

        return JsonPath(response.Body, parts[3]);
    }

    /// <summary>A small JSONPath subset: <c>$.a.b[0].c</c>.</summary>
    public static string? JsonPath(string json, string path)
    {
        if (!path.StartsWith('$'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (Match segment in PathSegment().Matches(path[1..]))
            {
                if (segment.Groups["name"].Success)
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment.Groups["name"].Value, out current))
                    {
                        return null;
                    }
                }
                else if (int.TryParse(segment.Groups["index"].Value, CultureInfo.InvariantCulture, out var index))
                {
                    if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                    {
                        return null;
                    }

                    current = current[index];
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\.(?<name>[^.\[]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathSegment();
}
