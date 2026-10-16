using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A stand-in for dotnet-svcutil 8.0.0 (CODALUS_SVCUTIL) for the WCF reference tests: no download, no network. It
// prints its arguments as a JSON line ("Arguments: [...]"), then does what the real tool does with them, from recorded
// output: the code it generated for samples/Wcf's client (with the namespace asked for), its options file
// (dotnet-svcutil.params.json, as the tool writes it), and the System.ServiceModel packages added to the project. An
// input containing "fail" fails as the tool does; --update reads the folder's ConnectedService.json or params file.
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var noLogo = args.Contains("--noLogo") || args.Contains("-nl");
if (!noLogo || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Microsoft (R) WCF Service Model Proxy Generation Tool for .Net Core platform");
    Console.WriteLine("[Microsoft.Tools.ServiceModel.Svcutil, Version 8.0.0]");
    Console.WriteLine("Copyright (c) Microsoft Corporation.  All rights reserved.");
    Console.WriteLine();
}

Console.WriteLine("Arguments: " + JsonSerializer.Serialize(args));
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(" --outputDir <directory>  - Directory to create files in. (Short Form: -d)");
    return 0;
}

string? input = null, project = null, outputDir = null, outputFile = null, update = null;
var namespaces = new List<string>();
var collections = new List<string>();
bool sync = false, noTypeReuse = false, isInternal = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
    switch (args[i])
    {
        case "--projectFile" or "-pf": project = Next(); break;
        case "--outputDir" or "-d": outputDir = Next(); break;
        case "--outputFile" or "-o": outputFile = Next(); break;
        case "--namespace" or "-n": namespaces.Add(Next()); break;
        case "--collectionType" or "-ct": collections.Add(Next()); break;
        case "--update" or "-u": update = Next(); break;
        case "--sync" or "-syn": sync = true; break;
        case "--noTypeReuse" or "-ntr": noTypeReuse = true; break;
        case "--internal" or "-i": isInternal = true; break;
        case "--noLogo" or "-nl": break;
        default:
            if (args[i].StartsWith('-'))
            {
                Console.WriteLine($"Error: Unrecognized option '{args[i]}' specified.");
                return 2;
            }

            input = args[i];
            break;
    }
}

project ??= Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.csproj").FirstOrDefault();
if (project is null)
{
    Console.WriteLine("Error: No project file found.");
    return 1;
}

project = Path.GetFullPath(project);
var projectDirectory = Path.GetDirectoryName(project)!;
JsonObject options;
string folder;
if (update is not null)
{
    folder = Path.GetFullPath(Path.Combine(projectDirectory, update));
    var file = File.Exists(Path.Combine(folder, "dotnet-svcutil.params.json")) ? Path.Combine(folder, "dotnet-svcutil.params.json")
        : File.Exists(Path.Combine(folder, "ConnectedService.json")) ? Path.Combine(folder, "ConnectedService.json")
        : null;
    if (file is null)
    {
        Console.WriteLine($"Error: The service reference '{update}' was not found under the project '{project}'.");
        return 1;
    }

    var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    options = (root["ExtendedData"] ?? root["options"])!.AsObject();
    Console.WriteLine("Update option read from \"" + file + "\" ...");
    input = options["inputs"]![0]!.GetValue<string>();
}
else
{
    if (input is null)
    {
        Console.WriteLine("Error: No input specified.");
        return 1;
    }

    folder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), outputDir ?? "ServiceReference"));
    var framework = Regex.Match(File.ReadAllText(project), "<TargetFramework>([^<]+)</TargetFramework>").Groups[1].Value;
    options = new JsonObject { ["inputs"] = new JsonArray(input) };
    if (collections.Count > 0)
    {
        options["collectionTypes"] = new JsonArray([.. collections.Select(c => (JsonNode)JsonValue.Create(c)!)]);
    }

    if (isInternal)
    {
        options["internalTypeAccess"] = true;
    }

    options["namespaceMappings"] = new JsonArray([.. namespaces.Select(n => (JsonNode)JsonValue.Create(string.Join(", ", n.Split(',', 2).Select(p => p.Trim())))!)]);
    options["outputFile"] = outputFile ?? "Reference.cs";
    if (sync)
    {
        options["sync"] = true;
    }

    options["targetFramework"] = framework;
    options["typeReuseMode"] = noTypeReuse ? "None" : "All";
}

if (input.Contains("fail", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Attempting to download metadata from '" + input + "' using WS-Metadata Exchange and HttpGet.");
    Console.WriteLine("Error: Cannot obtain Metadata from " + input);
    Console.WriteLine();
    Console.WriteLine("If you would like more help, type \"dotnet-svcutil -h\"");
    return 1;
}

if (!input.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.GetFullPath(Path.Combine(folder, input))))
{
    Console.WriteLine($"Error: The input file '{input}' doesn't exist.");
    return 1;
}

// The generated code, in the namespace "*" maps to.
var mapping = options["namespaceMappings"]?.AsArray().Select(n => n!.GetValue<string>()).FirstOrDefault(m => m.StartsWith('*'));
var ns = mapping?.Split(',', 2)[1].Trim() ?? "ServiceReference";
using var resource = typeof(Program).Assembly.GetManifestResourceStream("Reference.cs")!;
var code = new StreamReader(resource).ReadToEnd().Replace("CalculatorClient.Calculator", ns, StringComparison.Ordinal);
Directory.CreateDirectory(folder);
var codeFile = Path.Combine(folder, options["outputFile"]?.GetValue<string>() ?? "Reference.cs");
Console.WriteLine("Generating files...");
File.WriteAllText(codeFile, code);
Console.WriteLine("\"" + codeFile + "\"");
if (update is null)
{
    var parameters = new JsonObject { ["providerId"] = "Microsoft.Tools.ServiceModel.Svcutil", ["version"] = "8.0.0", ["options"] = options };
    File.WriteAllText(Path.Combine(folder, "dotnet-svcutil.params.json"), parameters.ToJsonString(json));
}

// The packages the client needs, as the tool adds them (pinned here to the versions samples/Wcf restores).
var text = File.ReadAllText(project);
if (!text.Contains("System.ServiceModel.Http", StringComparison.Ordinal))
{
    var group = """
          <ItemGroup>
            <PackageReference Include="System.ServiceModel.Http" Version="10.0.652802" />
            <PackageReference Include="System.ServiceModel.Primitives" Version="10.0.652802" />
          </ItemGroup>
        </Project>
        """;
    File.WriteAllText(project, text.Replace("</Project>", group.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal));
}

return 0;
