using System.Globalization;

namespace DaedalusStudio.Data.Host;

/// <summary>Values on the wire are invariant strings (null is NULL); binary is <c>0x</c> hex (ADR 0037).</summary>
internal static class Values
{
    /// <summary>Longest binary value shown in full; longer ones are cut with "…".</summary>
    private const int MaxBinary = 4096;

    public static string? Format(object? value) => value switch
    {
        null or DBNull => null,
        string s => s,
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > MaxBinary ? bytes.AsSpan(0, MaxBinary) : bytes) + (bytes.Length > MaxBinary ? "…" : string.Empty),
        bool b => b ? "true" : "false",
        DateTime d => d.TimeOfDay == TimeSpan.Zero && d.Kind != DateTimeKind.Utc ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>A string from the wire as a value of <paramref name="type"/> (a column's .NET type); null is <see cref="DBNull"/>.</summary>
    public static object Parse(string? text, Type type)
    {
        if (text is null)
        {
            return DBNull.Value;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            return type switch
            {
                _ when type == typeof(string) => text,
                _ when type == typeof(bool) => text.Trim().ToLowerInvariant() switch
                {
                    "1" or "true" or "yes" or "t" or "y" => true,
                    "0" or "false" or "no" or "f" or "n" => false,
                    _ => throw new FormatException($"'{text}' isn't true or false."),
                },
                _ when type == typeof(byte[]) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(text[2..]) : Convert.FromHexString(text),
                _ when type == typeof(Guid) => Guid.Parse(text),
                _ when type == typeof(DateTime) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces),
                _ when type == typeof(DateTimeOffset) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
                _ when type == typeof(DateOnly) => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                _ when type == typeof(TimeOnly) => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                _ when type == typeof(TimeSpan) => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
                _ when type.IsEnum => Enum.Parse(type, text, ignoreCase: true),
                _ when typeof(IConvertible).IsAssignableFrom(type) => Convert.ChangeType(text.Trim(), type, CultureInfo.InvariantCulture),
                _ => text,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new FormatException($"'{text}' isn't a valid {Friendly(type)}.", ex);
        }
    }

    private static string Friendly(Type type) => type.Name switch
    {
        "Int16" or "Int32" or "Int64" or "Byte" or "SByte" or "UInt16" or "UInt32" or "UInt64" => "whole number",
        "Decimal" or "Double" or "Single" => "number",
        "Boolean" => "true or false value",
        "DateTime" or "DateTimeOffset" or "DateOnly" => "date",
        _ => type.Name,
    };
}
