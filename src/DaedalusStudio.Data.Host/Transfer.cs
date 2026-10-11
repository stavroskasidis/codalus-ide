using System.Text;
using System.Text.Json;
using DaedalusStudio.Data.Protocol;

namespace DaedalusStudio.Data.Host;

/// <summary>Export and import of table data as CSV (RFC 4180, a header row) or JSON (an array of objects), streamed.</summary>
internal static class Transfer
{
    public static async Task<TransferResult> ExportAsync(DataServer server, ExportParams parameters, CancellationToken cancellationToken)
    {
        var engine = server.Registered(parameters.Id).Engine;
        await using var connection = await server.OpenAsync(parameters.Id, parameters.Database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = parameters.Table is { } table ? "SELECT * FROM " + engine.Qualified(table)
            : parameters.Query is { Length: > 0 } query ? query
            : throw new ArgumentException("Export needs a table or a query.");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        long rows = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parameters.Path))!);
        await using var file = File.Create(parameters.Path);
        if (IsJson(parameters.Format))
        {
            await using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
            json.WriteStartArray();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                json.WriteStartObject();
                for (var i = 0; i < names.Length; i++)
                {
                    json.WritePropertyName(names[i]);
                    WriteJsonValue(json, reader.IsDBNull(i) ? null : reader.GetValue(i));
                }

                json.WriteEndObject();
                rows++;
            }

            json.WriteEndArray();
        }
        else
        {
            await using var writer = new StreamWriter(file, new UTF8Encoding(false));
            await writer.WriteLineAsync(string.Join(',', names.Select(Csv))).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = DataServer.Row(reader, names.Length);
                await writer.WriteLineAsync(string.Join(',', row.Select(v => v is null ? string.Empty : Csv(v)))).ConfigureAwait(false);
                rows++;
            }
        }

        return new TransferResult(rows);
    }

    public static async Task<TransferResult> ImportAsync(DataServer server, ImportParams parameters, CancellationToken cancellationToken)
    {
        var engine = server.Registered(parameters.Id).Engine;
        var records = IsJson(parameters.Format) ? ReadJson(parameters.Path) : ReadCsv(await File.ReadAllTextAsync(parameters.Path, cancellationToken).ConfigureAwait(false));
        await using var connection = await server.OpenAsync(parameters.Id, parameters.Database, cancellationToken).ConfigureAwait(false);
        var (columns, _, types) = await DataServer.ShapeAsync(engine, connection, parameters.Table, cancellationToken).ConfigureAwait(false);
        var known = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long rows = 0;
        foreach (var record in records)
        {
            var values = record.Where(v => known.Contains(v.Key)).ToList();
            if (values.Count == 0)
            {
                throw new FormatException($"Row {rows + 1} has no column of {parameters.Table.Name}.");
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = new List<string>();
            var placeholders = new List<string>();
            for (var i = 0; i < values.Count; i++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = engine.Parameter("p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                parameter.Value = Values.Parse(values[i].Value, types.GetValueOrDefault(values[i].Key) ?? typeof(string));
                command.Parameters.Add(parameter);
                names.Add(engine.Quote(columns.First(c => c.Name.Equals(values[i].Key, StringComparison.OrdinalIgnoreCase)).Name));
                placeholders.Add(parameter.ParameterName);
            }

            command.CommandText = $"INSERT INTO {engine.Qualified(parameters.Table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", placeholders)})";
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException ex)
            {
                throw new InvalidOperationException($"Row {rows + 1}: {ex.Message} Nothing was imported.", ex);
            }

            rows++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TransferResult(rows);
    }

    private static bool IsJson(string format) => format.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 || value.Length == 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    private static void WriteJsonValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                json.WriteRawValue(Values.Format(value)!);
                break;
            default:
                json.WriteStringValue(Values.Format(value));
                break;
        }
    }

    /// <summary>
    /// CSV with a header row. An empty unquoted field is NULL; a quoted empty field (<c>""</c>) is an empty string.
    /// </summary>
    public static List<Dictionary<string, string?>> ReadCsv(string text)
    {
        var records = new List<List<(string Value, bool Quoted)>>();
        var row = new List<(string, bool)>();
        var field = new StringBuilder();
        var quoted = false;
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    quoted = true;
                    break;
                case ',':
                    row.Add((field.ToString(), quoted));
                    field.Clear();
                    quoted = false;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add((field.ToString(), quoted));
                    field.Clear();
                    quoted = false;
                    records.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || quoted || row.Count > 0)
        {
            row.Add((field.ToString(), quoted));
            records.Add(row);
        }

        records.RemoveAll(r => r is [("", false)]);
        if (records.Count == 0)
        {
            return [];
        }

        var header = records[0].Select(h => h.Value.Trim()).ToArray();
        return [.. records.Skip(1).Select(r => header.Select((name, i) => (name, value: i < r.Count ? (r[i].Value.Length == 0 && !r[i].Quoted ? null : r[i].Value) : null))
            .ToDictionary(p => p.name, p => p.value, StringComparer.OrdinalIgnoreCase))];
    }

    private static List<Dictionary<string, string?>> ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The JSON file must hold an array of objects.");
        }

        return [.. document.RootElement.EnumerateArray().Select(o => o.ValueKind != JsonValueKind.Object
            ? throw new FormatException("The JSON file must hold an array of objects.")
            : o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch
            {
                JsonValueKind.Null => (string?)null,
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.Value.GetRawText(),
            }, StringComparer.OrdinalIgnoreCase))];
    }
}
