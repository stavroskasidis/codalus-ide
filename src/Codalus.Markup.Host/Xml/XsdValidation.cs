using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;
using Codalus.LanguageServices.Protocol;
using Codalus.Markup.Host.Json;

namespace Codalus.Markup.Host.Xml;

/// <summary>
/// XSD validation of XML documents (deferred from M11). A document's schemas are the local files its root names with
/// <c>xsi:schemaLocation</c> (pairs of namespace and location) or <c>xsi:noNamespaceSchemaLocation</c>, relative to the
/// document, and those the <c>xml.schemas</c> setting associates with its path (a glob, as JSON schema associations are
/// written). Nothing is downloaded: <c>http</c> locations are skipped, and a schema's own <c>include</c> and
/// <c>import</c> resolve only to local files. Schema sets are cached until a schema file changes.
/// </summary>
internal sealed class XsdValidation
{
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    private readonly ConcurrentDictionary<string, (string Stamp, XmlSchemaSet? Set, string? Error)> _cache = new(StringComparer.Ordinal);

    /// <summary>The <c>xml.schemas</c> associations: a glob over the file path, and the schema's absolute path.</summary>
    public IReadOnlyList<(string Pattern, string Schema)> Associations { get; set; } = [];

    public IEnumerable<Diagnostic> Validate(TextDocument document)
    {
        var schemas = SchemasOf(document);
        if (schemas.Count == 0)
        {
            return [];
        }

        var key = string.Join('|', schemas.Select(s => s.Namespace + "=" + s.Path));
        var stamp = string.Join('|', schemas.Select(s => File.Exists(s.Path) ? File.GetLastWriteTimeUtc(s.Path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing"));
        if (!_cache.TryGetValue(key, out var cached) || cached.Stamp != stamp)
        {
            cached = (stamp, null, null);
            try
            {
                cached = (stamp, Load(schemas), null);
            }
            catch (Exception ex) when (ex is XmlException or XmlSchemaException or IOException or UnauthorizedAccessException)
            {
                cached = (stamp, null, ex.Message);
            }

            _cache[key] = cached;
        }

        if (cached.Error is { } error)
        {
            return [new Diagnostic(document.RangeOf(0, Math.Min(1, document.Text.Length)), DiagnosticSeverity.Warning, "xml.schemaLoad", "xml", $"The schema couldn't be read: {error}")];
        }

        var diagnostics = new List<Diagnostic>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = cached.Set!,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings | XmlSchemaValidationFlags.ProcessIdentityConstraints,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        settings.ValidationEventHandler += (sender, e) =>
        {
            var line = e.Exception?.LineNumber ?? 0;
            var column = e.Exception?.LinePosition ?? 0;
            if (line == 0 && sender is IXmlLineInfo info && info.HasLineInfo())
            {
                (line, column) = (info.LineNumber, info.LinePosition);
            }

            var start = document.OffsetAt(new Position(Math.Max(0, line - 1), Math.Max(0, column - 1)));
            var end = start;
            while (end < document.Text.Length && (char.IsLetterOrDigit(document.Text[end]) || document.Text[end] is ':' or '-' or '_' or '.'))
            {
                end++;
            }

            diagnostics.Add(new Diagnostic(document.RangeOf(start, Math.Max(end, Math.Min(document.Text.Length, start + 1))),
                e.Severity == XmlSeverityType.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "xml.schema", "xml", e.Message));
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(document.Text), settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException)
        {
            // Not well-formed: the syntax check reports it.
        }

        return diagnostics;
    }

    /// <summary>The schemas that apply to a document: its root's <c>xsi</c> locations, then the settings' associations.</summary>
    internal List<(string? Namespace, string Path)> SchemasOf(TextDocument document)
    {
        var schemas = new List<(string? Namespace, string Path)>();
        var folder = document.Path is { } path ? System.IO.Path.GetDirectoryName(path) : null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(document.Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            if (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.GetAttribute("noNamespaceSchemaLocation", XsiNamespace) is { } plain && Local(plain, folder) is { } plainPath)
                {
                    schemas.Add((null, plainPath));
                }

                if (reader.GetAttribute("schemaLocation", XsiNamespace) is { } pairs)
                {
                    var parts = pairs.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i + 1 < parts.Length; i += 2)
                    {
                        if (Local(parts[i + 1], folder) is { } located)
                        {
                            schemas.Add((parts[i], located));
                        }
                    }
                }
            }
        }
        catch (XmlException)
        {
        }

        if (document.Path is { } file)
        {
            var normalized = file.Replace('\\', '/');
            var name = System.IO.Path.GetFileName(file);
            foreach (var (pattern, schema) in Associations)
            {
                if (SchemaStore.Glob(pattern, normalized, name) && File.Exists(schema) && !schemas.Any(s => s.Path == schema))
                {
                    schemas.Add((TargetNamespace(schema), schema));
                }
            }
        }

        return schemas;
    }

    /// <summary>A location as a local file: relative to the document, absolute, or a <c>file:</c> URI; null for others.</summary>
    private static string? Local(string location, string? folder)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return null; // http and the like: never downloaded
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : folder is null ? location : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, location));
        return File.Exists(path) ? path : null;
    }

    private static string? TargetNamespace(string schema)
    {
        try
        {
            using var reader = XmlReader.Create(schema, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return reader.MoveToContent() == XmlNodeType.Element ? reader.GetAttribute("targetNamespace") : null;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static XmlSchemaSet Load(IEnumerable<(string? Namespace, string Path)> schemas)
    {
        var set = new XmlSchemaSet { XmlResolver = new LocalResolver() };
        foreach (var (ns, path) in schemas)
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            set.Add(string.IsNullOrEmpty(ns) ? null : ns, reader);
        }

        set.Compile();
        return set;
    }

    /// <summary>Resolves a schema's <c>include</c> and <c>import</c> to local files only.</summary>
    private sealed class LocalResolver : XmlResolver
    {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) =>
            absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath)
                ? File.OpenRead(absoluteUri.LocalPath)
                : throw new XmlException($"{absoluteUri} isn't a local file; schemas are never downloaded.");
    }
}
