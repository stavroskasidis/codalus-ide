using System.Text;
using System.Text.RegularExpressions;

namespace Spike.Utilities;

public static partial class Text
{
    /* Indentation helpers.
       Multi-line block comment used by the classifier tests. */
    public static string Indent(string value, int depth = 1)
    {
        var builder = new StringBuilder();
        var prefix = new string(' ', depth * 4);
        foreach (var line in value.Split('\n'))
        {
            builder.Append(prefix).Append(line).Append("\r\n");
        }

        return builder.ToString();
    }

    public static bool IsIdentifier(string value) => IdentifierPattern().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}

public static class Money
{
    public const int Decimals = 2;

    public static decimal Round(decimal value) => Math.Round(value, Decimals, MidpointRounding.ToEven);

    public static T Max<T>(T left, T right)
        where T : IComparable<T>
        => left.CompareTo(right) >= 0 ? left : right;
}
