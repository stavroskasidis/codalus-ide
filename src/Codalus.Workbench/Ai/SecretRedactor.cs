using System.Text.RegularExpressions;

namespace Codalus.Workbench.Ai;

/// <summary>
/// Replaces secrets in text that goes to a language model with <see cref="Placeholder"/> (ADR 0062): private key blocks,
/// well-known token shapes, and the values of password, secret, token and key settings and connection-string parts.
/// Pattern-based, so it is a second line behind the file exclusions, not a guarantee.
/// </summary>
public static partial class SecretRedactor
{
    public const string Placeholder = "[redacted]";

    /// <summary>The text with its secrets replaced, and how many were.</summary>
    public static (string Text, int Count) Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, 0);
        }

        var count = 0;
        var result = PrivateKey().Replace(text, _ =>
        {
            count++;
            return Placeholder;
        });
        result = Tokens().Replace(result, _ =>
        {
            count++;
            return Placeholder;
        });
        result = QuotedValue().Replace(result, Value);
        result = PlainValue().Replace(result, Value);
        return (result, count);

        string Value(Match m)
        {
            var value = m.Groups["value"];
            if (value.Length == 0 || value.Value == Placeholder || IsPlaceholderValue(value.Value))
            {
                return m.Value;
            }

            count++;
            return m.Value[..(value.Index - m.Index)] + Placeholder + m.Value[(value.Index - m.Index + value.Length)..];
        }
    }

    /// <summary>
    /// The text with every occurrence of <paramref name="values"/> replaced (an external agent's keys from
    /// <c>secretEnv</c>, which the patterns may not know; ADR 0083), then the patterns'. Values under 4 characters are
    /// left alone (they would redact ordinary words).
    /// </summary>
    public static (string Text, int Count) Redact(string text, IEnumerable<string> values)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, 0);
        }

        var count = 0;
        foreach (var value in values.Where(v => v.Length >= 4).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length))
        {
            var at = text.IndexOf(value, StringComparison.Ordinal);
            while (at >= 0)
            {
                count++;
                text = string.Concat(text.AsSpan(0, at), Placeholder, text.AsSpan(at + value.Length));
                at = text.IndexOf(value, at + Placeholder.Length, StringComparison.Ordinal);
            }
        }

        var (redacted, more) = Redact(text);
        return (redacted, count + more);
    }

    /// <summary>Values that are clearly not secrets (empty, a reference to a variable, a template marker).</summary>
    private static bool IsPlaceholderValue(string value) =>
        value.StartsWith('$') || value.StartsWith('%') || value.StartsWith("{{", StringComparison.Ordinal) ||
        value.StartsWith('<') || value is "null" or "true" or "false" or "\"\"";

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();

    // GitHub (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_), GitLab (glpat-), OpenAI and Anthropic (sk-…), AWS access keys,
    // Slack (xox?-), Azure storage account keys in connection strings, Google API keys, JWTs.
    [GeneratedRegex(
        @"\b(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_\-]{20,}|sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}|A[KS]IA[0-9A-Z]{16}|xox[abposr]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z_\-]{35}|eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})")]
    private static partial Regex Tokens();

    // A key that names a secret, then = or :, then a quoted literal: "password": "x", password = "x", Password='x'.
    [GeneratedRegex(
        @"[""']?\b[\w.\-]*(?:password|passwd|pwd|secret|token|apikey|api_key|api-key|accountkey|access_key|client_secret|sharedaccesskey)\b[""']?\s*(?:=|:)\s*(?<quote>[""'])(?<value>[^""'\r\n]+)\k<quote>",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuotedValue();

    // Unquoted values only where code doesn't put them: KEY=value with nothing around the = (.env, ini, connection-string
    // parts like Password=x;), and "key: value" at the start of a line (YAML). Code's "token = GetToken()" stays.
    [GeneratedRegex(
        @"(?:(?<=^|[;\s""'])[\w.\-]*(?:password|passwd|pwd|secret|token|apikey|api_key|api-key|accountkey|access_key|client_secret|sharedaccesskey)=(?<value>[^""'\s;,]+)|^[ \t]*[\w.\-]*(?:password|passwd|pwd|secret|token|apikey|api_key|api-key|accountkey|access_key|client_secret)[ \t]*:[ \t]+(?<value>[^""'\s#]+))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PlainValue();
}
