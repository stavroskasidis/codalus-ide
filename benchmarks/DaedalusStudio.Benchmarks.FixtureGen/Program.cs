using DaedalusStudio.Benchmarks.FixtureGen;

// Usage:
//   daedalus-fixturegen large-file <out-dir> [lines=100000]
//   daedalus-fixturegen solution   <out-dir> [projects=50]
//   daedalus-fixturegen all        <samples-dir>
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: daedalus-fixturegen (large-file|solution|all) <out-dir> [count]");
    return 2;
}

var outDir = Path.GetFullPath(args[1]);
var count = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
switch (args[0])
{
    case "large-file":
        LargeFileGenerator.WriteProject(outDir, count > 0 ? count : LargeFileGenerator.DefaultLines);
        break;
    case "solution":
        SolutionGenerator.Write(outDir, count > 0 ? count : SolutionGenerator.DefaultProjects);
        break;
    case "all":
        LargeFileGenerator.WriteProject(Path.Combine(outDir, "Large100k"), LargeFileGenerator.DefaultLines);
        SolutionGenerator.Write(Path.Combine(outDir, "Solution50"), SolutionGenerator.DefaultProjects);
        break;
    default:
        Console.Error.WriteLine($"unknown command '{args[0]}'");
        return 2;
}

Console.WriteLine($"fixtures written to {outDir}");
return 0;
