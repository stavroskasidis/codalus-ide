using System.Text.RegularExpressions;

namespace Codalus.Workbench.Projects;

/// <summary>
/// A template's options from <c>dotnet new &lt;template&gt; --help</c> (in English): New Project's fallback when the
/// template cache can't be read (ADR 0082). Each option block is <c>--name &lt;placeholder&gt;  description</c>, then
/// <c>Type: bool|choice|text|string|int|float</c>, the choices indented under it, and <c>Default: value</c>.
/// </summary>
public static partial class TemplateHelp
{
    /// <summary>Options that are plumbing, not choices for the page.</summary>
    private static readonly HashSet<string> s_plumbing = new(StringComparer.OrdinalIgnoreCase)
    {
        "--name", "--output", "--language", "--type", "--framework", "--target-framework-override", "--langVersion", "--no-restore",
        "--dry-run", "--force", "--no-update-check", "--project", "--verbosity", "--diagnostics", "--help", "--exclude-launch-settings",
    };

    /// <summary>
    /// The choices of <c>-f, --framework</c>: the default first, then the newest .NET first, then .NET Standard. Empty
    /// when the template has no framework option. The choices are listed under the option ("Type: choice"); a wide
    /// console also puts them in the option's placeholder (<c>&lt;net10.0|net11.0&gt;</c>), a narrow one says
    /// <c>&lt;choice&gt;</c>.
    /// </summary>
    public static IReadOnlyList<(string Framework, bool IsDefault)> Frameworks(string help)
    {
        var lines = help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var option = Array.FindIndex(lines, l => FrameworkOption().IsMatch(l));
        if (option < 0)
        {
            return [];
        }

        var block = lines.Skip(option + 1).TakeWhile(l => !l.TrimStart().StartsWith('-')).ToList();
        var listed = block
            .SkipWhile(l => !l.Trim().Equals("Type: choice", StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(l => !DefaultValue().IsMatch(l))
            .Select(l => l.Trim().Split(' ', 2)[0])
            .Where(c => c.Length > 0)
            .ToList();
        var choices = FrameworkOption().Match(lines[option]).Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c != "choice")
            .Union(listed, StringComparer.Ordinal);
        var @default = block.Select(l => DefaultValue().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return [.. choices
            .OrderByDescending(c => c == @default)
            .ThenBy(c => c.StartsWith("netstandard", StringComparison.Ordinal))
            .ThenByDescending(c => Version.TryParse(VersionOf(c), out var v) ? v : new Version())
            .Select(c => (c, c == @default))];
    }

    /// <summary>
    /// The template's own options (not the CLI's, not the framework): bool, choice, text, string, integer and float
    /// options with their defaults and choice descriptions, labeled by their descriptions.
    /// </summary>
    public static IReadOnlyList<TemplateParameter> Parameters(string help)
    {
        var lines = help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd().EndsWith("options:", StringComparison.OrdinalIgnoreCase) && l.Contains("Template", StringComparison.OrdinalIgnoreCase));
        var parameters = new List<TemplateParameter>();
        for (var i = start < 0 ? 0 : start + 1; i < lines.Length; i++)
        {
            var match = OptionLine().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var option = match.Groups["long"].Value;
            if (s_plumbing.Contains(option))
            {
                continue;
            }

            var block = lines.Skip(i + 1).TakeWhile(l => !OptionLine().IsMatch(l) && l.Trim().Length > 0).ToList();
            var description = string.Join(' ', new[] { match.Groups["description"].Value.Trim() }
                .Concat(block.TakeWhile(l => !TypeLine().IsMatch(l) && !EnabledLine().IsMatch(l)).Select(l => l.Trim()))
                .Where(t => t.Length > 0));
            var enabled = block.Select(l => EnabledLine().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();
            var typeText = block.Select(l => TypeLine().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim().ToLowerInvariant() ?? "text";
            var type = typeText switch
            {
                "bool" => TemplateDataType.Bool,
                "choice" => TemplateDataType.Choice,
                "int" or "integer" => TemplateDataType.Integer,
                "float" => TemplateDataType.Float,
                _ => TemplateDataType.Text,
            };
            var choices = type == TemplateDataType.Choice
                ? block.SkipWhile(l => !TypeLine().IsMatch(l)).Skip(1).TakeWhile(l => !DefaultValue().IsMatch(l))
                    .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length > 0)
                    .Select(p => new TemplateChoice(p[0], p[0], p.Length > 1 ? p[1].Trim() : null))
                    .ToList()
                : [];
            var @default = block.Select(l => DefaultValue().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            var name = SymbolName(option);
            parameters.Add(new TemplateParameter
            {
                Name = name,
                CliOption = option,
                DataType = type,
                DefaultValue = @default ?? (type == TemplateDataType.Bool ? "false" : null),
                DisplayDefault = @default ?? (type == TemplateDataType.Bool ? "false" : null),
                Label = description.Length > 0 ? FirstSentence(description) : name,
                Description = description.Length > 0 ? description : null,
                Choices = choices,
                EnabledCondition = enabled is { Length: > 0 } ? enabled : null,
                Order = parameters.Count,
            });
        }

        return parameters;
    }

    /// <summary><c>net11.0</c> → ".NET 11.0", <c>netstandard2.1</c> → ".NET Standard 2.1" (without a support label).</summary>
    public static string Display(string framework) =>
        framework.StartsWith("netstandard", StringComparison.Ordinal) ? ".NET Standard " + VersionOf(framework)
        : framework.StartsWith("netcoreapp", StringComparison.Ordinal) ? ".NET Core " + VersionOf(framework)
        : framework.StartsWith("net", StringComparison.Ordinal) && Version.TryParse(VersionOf(framework), out _) ? ".NET " + VersionOf(framework)
        : framework;

    /// <summary>The first sentence of a description, without its full stop: the label of an option the help names no better.</summary>
    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return (end > 0 ? text[..end] : text).TrimEnd('.');
    }

    /// <summary><c>--use-program-main</c> → <c>use-program-main</c>: the option is the only name the help gives.</summary>
    private static string SymbolName(string option) => option.TrimStart('-');

    private static string VersionOf(string framework) => framework.TrimStart("netstandardcorep".ToCharArray());

    [GeneratedRegex(@"--framework <([^>]+)>")]
    private static partial Regex FrameworkOption();

    [GeneratedRegex(@"^\s*Default:\s*(\S+)")]
    private static partial Regex DefaultValue();

    [GeneratedRegex(@"^\s*Enabled if:\s*(.+)$")]
    private static partial Regex EnabledLine();

    [GeneratedRegex(@"^\s*Type:\s*(\S+)")]
    private static partial Regex TypeLine();

    [GeneratedRegex(@"^\s{1,6}(?:-[A-Za-z0-9]+,\s*)?(?<long>--[A-Za-z0-9][A-Za-z0-9_-]*)(?:\s+<[^>]*>)?(?:\s{2,}(?<description>.*))?$")]
    private static partial Regex OptionLine();
}
