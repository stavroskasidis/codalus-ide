using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The repository's current files never name the classic IDE: its product name and its two-letter abbreviation are
/// "the classic IDE" everywhere (M50), outside the exceptions below. Git history isn't rewritten; this scans what
/// <c>git ls-files</c> lists. This file spells the name in parts, so it passes its own scan.
/// </summary>
public sealed class ProductNameTests
{
    private static string Metadata(string key) =>
        typeof(ProductNameTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>The name, and the standalone uppercase abbreviation; "VS Code" and its full name are another product.</summary>
    private static readonly Regex s_mention = new(@"Visual\x20Studio(?!\x20Code)|\bVS\b(?!\x20Code)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The <c>.sln</c> format's own text, wherever it is written, parsed or quoted: its first line and the version comment
    /// on its second (<c># ... Version 17</c>, <c># ... 15</c>).
    /// </summary>
    private static readonly Regex s_slnFormat = new(@"Microsoft\x20Visual\x20Studio\x20Solution\x20File|#\x20Visual\x20Studio\x20(Version\x20)?\d+", RegexOptions.CultureInvariant);

    /// <summary>The name wrapped over two lines of a paragraph or a comment: the first line's end.</summary>
    private static readonly Regex s_wrappedStart = new(@"\bVisual\s*$", RegexOptions.CultureInvariant);

    /// <summary>The second line's start, after a comment's prefix.</summary>
    private static readonly Regex s_wrappedEnd = new(@"^\s*(?:///?|\*|#+|<!--)?\s*Studio\b(?!\x20Code)", RegexOptions.CultureInvariant);

    private const string Name = "Visual" + " Studio";

    /// <summary>Files left out entirely.</summary>
    private static readonly HashSet<string> s_excludedFiles = new(StringComparer.Ordinal)
    {
        "THIRD-PARTY-NOTICES.md", // the legal notices name the reference IDEs measured, as their licences require
    };

    /// <summary>Functional strings, which the code needs as they are: (file, substring, reason).</summary>
    private static readonly (string File, string Text, string Reason)[] s_allowed =
    [
        (".github/workflows/pipeline.yml", $"Microsoft {Name}/Installer", "vswhere.exe's install folder, put on PATH for the NativeAOT publish"),
        ("benchmarks/Codalus.Benchmarks.Harness/Targets.cs", $"\"Microsoft {Name}\", \"Installer\", \"vswhere.exe\"", "vswhere.exe's install folder, which finds the reference IDE to measure"),
        ("src/Codalus.Workbench/Settings/VsCodeImport.cs", $"[\"Dark ({Name})\"]", "a VS Code theme's name, read from the user's VS Code settings"),
        ("src/Codalus.Workbench/Settings/VsCodeImport.cs", $"[\"{Name} Dark\"]", "a VS Code theme's name, read from the user's VS Code settings"),
        ("src/Codalus.Workbench/Settings/VsCodeImport.cs", $"[\"Light ({Name})\"]", "a VS Code theme's name, read from the user's VS Code settings"),
        ("src/Codalus.Workbench/Settings/VsCodeImport.cs", $"[\"{Name} Light\"]", "a VS Code theme's name, read from the user's VS Code settings"),
        ("tests/Codalus.Shell.Tests/SweepFindingsTests.cs", $"Contains(\"{Name}\", StringComparison.OrdinalIgnoreCase)", "the check that no UI string names it"),
        ("tests/Codalus.Shell.Tests/UxPassTests.cs", $"Contains(\"{Name}\", StringComparison.OrdinalIgnoreCase)", "the check that no menu title names it"),
    ];

    [Fact]
    public void No_current_file_names_the_classic_IDE()
    {
        var root = Metadata("CodalusRepoRoot");
        var mentions = new List<string>();
        var used = new HashSet<int>();
        foreach (var file in TrackedFiles(root))
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (s_excludedFiles.Contains(file) || !File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0)
            {
                continue; // binary, as git decides it
            }

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = s_slnFormat.Replace(lines[i], "");
                for (var a = 0; a < s_allowed.Length; a++)
                {
                    if (s_allowed[a].File == file && line.Contains(s_allowed[a].Text, StringComparison.Ordinal))
                    {
                        line = line.Replace(s_allowed[a].Text, "", StringComparison.Ordinal);
                        used.Add(a);
                    }
                }

                if (s_mention.IsMatch(line) || (i + 1 < lines.Length && s_wrappedStart.IsMatch(line) && s_wrappedEnd.IsMatch(lines[i + 1])))
                {
                    mentions.Add($"{file}:{i + 1}: {lines[i].Trim()[..Math.Min(lines[i].Trim().Length, 160)]}");
                }
            }
        }

        var stale = s_allowed.Where((_, a) => !used.Contains(a)).Select(a => $"{a.File}: {a.Text}").ToList();
        Assert.True(stale.Count == 0, "Exceptions that match nothing any more (remove them): " + string.Join("; ", stale));
        Assert.True(mentions.Count == 0, $"{mentions.Count} lines name the classic IDE (write \"the classic IDE\"; a functional string goes in {nameof(s_allowed)} with its reason):\n" + string.Join("\n", mentions));
    }

    [Theory]
    [InlineData("as the " + "Visual" + " Studio" + " palette", true)]
    [InlineData("a " + "V" + "S-written form", true)]
    [InlineData("V" + "S's initial properties", true)]
    [InlineData("V" + "S behavior", true)]
    [InlineData("V" + "S Code's keymap", false)]
    [InlineData("Visual" + " Studio" + " Code.app", false)]
    [InlineData("A vs. B, vswhere, a .vsix, VsCodeImport, VS16, VS_Code_snippets", false)]
    [InlineData("Microsoft " + "Visual" + " Studio" + " Solution File, Format Version 12.00", false)]
    [InlineData("# " + "Visual" + " Studio" + " Version 17", false)]
    [InlineData("# " + "Visual" + " Studio" + " 15", false)]
    [InlineData("## " + "Visual" + " Studio" + " parity matrix", true)]
    public void The_scan_tells_the_name_from_what_only_looks_like_it(string line, bool named) =>
        Assert.Equal(named, s_mention.IsMatch(s_slnFormat.Replace(line, "")));

    [Theory]
    [InlineData("/// a project that imports Visual", "/// Studio's own targets", true)]
    [InlineData("the tokens in `Tokens.axaml`: Visual", "Studio's image colors", true)]
    [InlineData("// as Visual", "// Studio Code does", false)]
    [InlineData("Visual", "Basic", false)]
    public void The_scan_finds_the_name_wrapped_over_two_lines(string first, string second, bool named) =>
        Assert.Equal(named, s_wrappedStart.IsMatch(first) && s_wrappedEnd.IsMatch(second));

    private static string[] TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-c", "core.quotepath=off", "ls-files", "-z" })
        {
            start.ArgumentList.Add(argument);
        }

        using var git = Process.Start(start)!;
        var error = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git ls-files failed in {root}: {error.Result}");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
