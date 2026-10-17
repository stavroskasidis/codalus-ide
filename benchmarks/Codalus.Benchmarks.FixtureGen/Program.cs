using Codalus.Benchmarks.FixtureGen;

// Usage:
//   codalus-fixturegen large-file <out-dir> [lines=100000]
//   codalus-fixturegen markup     <out-dir> [lines=100000]  (Large.json and Large.xml, M11)
//   codalus-fixturegen solution   <out-dir> [projects=50]
//   codalus-fixturegen tests      <out-dir> [projects=20]   (250 xUnit tests per project)
//   codalus-fixturegen packages   <feed-dir> [package-id] (samples/Packages/feed: committed, M9; one package with its id)
//   codalus-fixturegen all        <samples-dir>
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: codalus-fixturegen (large-file|markup|solution|tests|packages|all) <out-dir> [count]");
    return 2;
}

var outDir = Path.GetFullPath(args[1]);
var count = args.Length > 2 && args[0] != "packages" ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
switch (args[0])
{
    case "large-file":
        LargeFileGenerator.WriteProject(outDir, count > 0 ? count : LargeFileGenerator.DefaultLines);
        break;
    case "markup":
        MarkupGenerator.Write(outDir, count > 0 ? count : LargeFileGenerator.DefaultLines);
        break;
    case "solution":
        SolutionGenerator.Write(outDir, count > 0 ? count : SolutionGenerator.DefaultProjects);
        break;
    case "tests":
        TestsGenerator.Write(outDir, count > 0 ? count : TestsGenerator.DefaultProjects);
        break;
    case "packages":
        PackageFeedGenerator.Write(outDir, args.Length > 2 ? args[2] : null);
        break;
    case "all":
        LargeFileGenerator.WriteProject(Path.Combine(outDir, "Large100k"), LargeFileGenerator.DefaultLines);
        MarkupGenerator.Write(Path.Combine(outDir, "LargeMarkup100k"), LargeFileGenerator.DefaultLines);
        SolutionGenerator.Write(Path.Combine(outDir, "Solution50"), SolutionGenerator.DefaultProjects);
        TestsGenerator.Write(Path.Combine(outDir, "Tests5k"), TestsGenerator.DefaultProjects);
        break;
    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}

Console.WriteLine($"fixtures written to {outDir}");
return 0;
