using System.Globalization;
using Codalus.Release;

// The release workflows' helper (M31, D21). Run from anywhere inside the repository:
//   codalus-release version stable|nightly|dev [--run N] [--date YYYYMMDD]   the version to stamp a build with
//   codalus-release check-tag <version>                                     fails when v<version> already exists
//   codalus-release bump [--after <released version>]                       VERSION := the next minor version
//                                                                            (after a release: past it, unless already further)
//   codalus-release notes stable|nightly <version> [--repo owner/name] [--to REF] [--since TAG] [--changelog FILE]
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

try
{
    var git = new Git(Environment.CurrentDirectory);
    var root = git.Run("rev-parse", "--show-toplevel").Trim();
    git = new Git(root);
    var versionFile = Path.Combine(root, ReleaseVersion.FileName);
    switch (args.FirstOrDefault())
    {
        case "version":
        {
            var kind = ParseKind(args.ElementAtOrDefault(1));
            var date = Option("--date") is { } d ? DateOnly.ParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.UtcNow);
            var run = Option("--run") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : 0;
            Console.WriteLine(ReleaseVersion.Read(versionFile).For(kind, date, run));
            return 0;
        }

        case "check-tag":
        {
            var tag = ReleaseVersion.Tag(args.ElementAtOrDefault(1) ?? throw new ArgumentException("check-tag needs a version."));
            if (git.TagExists(tag))
            {
                Console.Error.WriteLine($"The tag {tag} already exists: this version was released. Bump VERSION first.");
                return 1;
            }

            Console.WriteLine($"{tag} is free.");
            return 0;
        }

        case "bump":
        {
            var current = ReleaseVersion.Read(versionFile);
            var next = Option("--after") is { } released ? current.After(ReleaseVersion.Parse(released)) : current.NextMinor();
            File.WriteAllText(versionFile, next + "\n");
            Console.WriteLine(next);
            return 0;
        }

        case "notes":
        {
            var kind = ParseKind(args.ElementAtOrDefault(1));
            var version = args.ElementAtOrDefault(2) ?? throw new ArgumentException("notes needs the version being released.");
            var repository = Option("--repo") ?? "stavroskasidis/codalus-ide";
            var input = ReleaseNotesSource.Read(git, kind, version, repository, Option("--to") ?? "HEAD", Option("--since"), Option("--changelog") ?? "CHANGELOG.md");
            Console.Write(ReleaseNotes.Build(input));
            return 0;
        }

        default:
            Console.Error.WriteLine("usage: codalus-release version|check-tag|bump|notes … (see Program.cs)");
            return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static ReleaseKind ParseKind(string? text) => text switch
{
    "stable" => ReleaseKind.Stable,
    "nightly" => ReleaseKind.Nightly,
    "dev" => ReleaseKind.Dev,
    _ => throw new ArgumentException("The kind is stable, nightly or dev."),
};
