namespace Codalus.Shell.ViewModels;

/// <summary>
/// Access keys for a page of options whose labels come from templates (ADR 0082). The page's own letters are taken first
/// (its buttons' and labels': <c>_Back</c>, <c>_Framework</c>, …). A label that brings its own key (<c>Include _sample
/// pages</c>) keeps it when the letter is free, else loses it; a label without one (the IDE's own options: Configure for
/// HTTPS, Enable container support) gets the first free letter of its text when <c>assign</c> is set.
/// </summary>
public sealed class AccessKeyAllocator
{
    private readonly HashSet<char> _taken = [];

    public AccessKeyAllocator(IEnumerable<char> reserved)
    {
        foreach (var letter in reserved)
        {
            _taken.Add(char.ToUpperInvariant(letter));
        }
    }

    /// <summary>The letter a label marks with <c>_</c> (a doubled <c>__</c> is a literal underscore), or null.</summary>
    public static char? KeyOf(string label)
    {
        for (var i = 0; i < label.Length - 1; i++)
        {
            if (label[i] != '_')
            {
                continue;
            }

            if (label[i + 1] == '_')
            {
                i++;
                continue;
            }

            return char.IsLetterOrDigit(label[i + 1]) ? char.ToUpperInvariant(label[i + 1]) : null;
        }

        return null;
    }

    /// <summary>The label without its access key marker (doubled underscores stay literal ones).</summary>
    public static string Strip(string label) => label.Replace("__", "\u0001", StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).Replace("\u0001", "__", StringComparison.Ordinal);

    /// <summary>The label to show: its own key when free, else none, or (with <paramref name="assign"/>) the first free letter.</summary>
    public string Allocate(string label, bool assign)
    {
        if (KeyOf(label) is { } own && _taken.Add(own))
        {
            return label;
        }

        var plain = Strip(label);
        if (!assign)
        {
            return plain;
        }

        for (var i = 0; i < plain.Length; i++)
        {
            if (plain[i] == '_' && i + 1 < plain.Length && plain[i + 1] == '_')
            {
                i++;
                continue;
            }

            if (char.IsLetter(plain[i]) && _taken.Add(char.ToUpperInvariant(plain[i])))
            {
                return plain[..i] + "_" + plain[i..];
            }
        }

        return plain;
    }
}
