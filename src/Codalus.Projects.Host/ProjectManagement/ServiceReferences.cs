using Codalus.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Codalus.Projects.Host.ProjectManagement;

/// <summary>
/// Service references (ADR 0041) as the items <c>dotnet-openapi</c> and Visual Studio's Connected Services write:
/// <c>&lt;OpenApiReference Include="OpenAPIs\weather.json" CodeGenerator="NSwagCSharp" Namespace="…" ClassName="…" /&gt;</c>
/// and <c>&lt;Protobuf Include="Protos\greet.proto" GrpcServices="Client" /&gt;</c>, with <c>SourceUrl</c> when the
/// document was downloaded. The code is generated at build time by the packages <see cref="PackagesFor"/> lists.
/// </summary>
internal static class ServiceReferences
{
    public const string OpenApiItem = "OpenApiReference";
    public const string ProtobufItem = "Protobuf";

    private static readonly StringComparison s_pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The packages a reference needs, with a version known to work when no other can be found.</summary>
    public static IReadOnlyList<(string Id, string Fallback, bool Private)> PackagesFor(string kind, string? grpcServices) => kind switch
    {
        ServiceReferenceKinds.OpenApi =>
        [
            ("Microsoft.Extensions.ApiDescription.Client", "10.0.12", true),
            ("NSwag.ApiDescription.Client", "14.7.1", true),
            ("Newtonsoft.Json", "13.0.4", false),
        ],
        ServiceReferenceKinds.Grpc when grpcServices is GrpcServiceKinds.Server or GrpcServiceKinds.Both =>
        [
            ("Grpc.AspNetCore", "2.84.0", false),
        ],
        ServiceReferenceKinds.Grpc =>
        [
            ("Grpc.Net.ClientFactory", "2.84.0", false),
            ("Google.Protobuf", "3.36.2", false),
            ("Grpc.Tools", "2.84.0", true),
        ],
        _ => throw new ArgumentException($"Unknown service reference kind '{kind}'."),
    };

    /// <summary>Every package that generates service references, to report the ones a project has.</summary>
    public static readonly string[] AllPackages =
    [
        "Microsoft.Extensions.ApiDescription.Client", "NSwag.ApiDescription.Client", "Newtonsoft.Json",
        "Grpc.AspNetCore", "Grpc.Net.ClientFactory", "Grpc.Net.Client", "Google.Protobuf", "Grpc.Tools",
    ];

    public static string ItemType(string kind) => kind switch
    {
        ServiceReferenceKinds.OpenApi => OpenApiItem,
        ServiceReferenceKinds.Grpc => ProtobufItem,
        _ => throw new ArgumentException($"Unknown service reference kind '{kind}'."),
    };

    /// <summary>The references the project declares (not items that packages or the SDK add).</summary>
    public static ServiceReferenceInfo[] Read(Project project)
    {
        var directory = Path.GetDirectoryName(project.FullPath)!;
        var references = new List<ServiceReferenceInfo>();
        foreach (var (kind, type) in new[] { (ServiceReferenceKinds.OpenApi, OpenApiItem), (ServiceReferenceKinds.Grpc, ProtobufItem) })
        {
            foreach (var item in project.GetItems(type))
            {
                if (item.Xml?.ContainingProject?.FullPath is not { } file || !PackageReferencesUserFile(project, file))
                {
                    continue;
                }

                string? Meta(string name) => item.GetMetadataValue(name) is { Length: > 0 } value ? value : null;
                references.Add(new ServiceReferenceInfo(
                    kind,
                    Path.GetFullPath(Path.Combine(directory, item.EvaluatedInclude.Replace('\\', Path.DirectorySeparatorChar))),
                    item.UnevaluatedInclude,
                    Meta("SourceUrl"),
                    Meta("Namespace"),
                    kind == ServiceReferenceKinds.OpenApi ? Meta("ClassName") : null,
                    kind == ServiceReferenceKinds.OpenApi ? Meta("CodeGenerator") ?? "NSwagCSharp" : null,
                    kind == ServiceReferenceKinds.OpenApi ? Meta("Options") : null,
                    kind == ServiceReferenceKinds.Grpc ? Meta("GrpcServices") ?? GrpcServiceKinds.Both : null,
                    kind == ServiceReferenceKinds.Grpc ? Meta("Access") : null));
            }
        }

        return [.. references];
    }

    private static bool PackageReferencesUserFile(Project project, string file) => Packages.PackageReferences.IsUserFile(project, file);

    /// <summary>Adds the item (or changes the existing one for the same file); returns whether the project file changed.</summary>
    public static bool Set(string projectPath, ServiceReferenceParams parameters, bool create)
    {
        var type = ItemType(parameters.Kind);
        using var gate = StringCacheGate.Enter();
        using var collection = new ProjectCollection();
        var xml = ProjectRootElement.Open(projectPath, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
        var before = xml.RawXml;
        var item = Find(xml, type, parameters.File);
        if (item is null)
        {
            if (!create)
            {
                throw new InvalidOperationException($"{Path.GetFileName(projectPath)} has no {type} for {Path.GetFileName(parameters.File)}.");
            }

            var group = xml.ItemGroups.FirstOrDefault(g => g.Condition.Length == 0 && g.Items.Any(i => i.ItemType == type)) ?? xml.AddItemGroup();
            item = group.AddItem(type, Relative(projectPath, parameters.File));
        }

        if (parameters.Kind == ServiceReferenceKinds.OpenApi)
        {
            Write(item, "CodeGenerator", parameters.CodeGenerator is { Length: > 0 } generator ? generator : "NSwagCSharp");
            Write(item, "Namespace", parameters.Namespace);
            Write(item, "ClassName", parameters.ClassName);
            Write(item, "Options", parameters.Options);
        }
        else
        {
            Write(item, "GrpcServices", parameters.GrpcServices is { Length: > 0 } services ? services : GrpcServiceKinds.Client);
            Write(item, "Access", parameters.Access is "Internal" ? "Internal" : null);
        }

        Write(item, "SourceUrl", parameters.SourceUrl);
        if (xml.RawXml == before)
        {
            return false;
        }

        xml.SaveFile();
        return true;
    }

    /// <summary>Removes the item; returns whether the project file changed.</summary>
    public static bool Remove(string projectPath, string kind, string file)
    {
        using var gate = StringCacheGate.Enter();
        using var collection = new ProjectCollection();
        var xml = ProjectRootElement.Open(projectPath, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
        if (Find(xml, ItemType(kind), file) is not { } item)
        {
            return false;
        }

        var group = item.Parent;
        group.RemoveChild(item);
        if (group.Count == 0)
        {
            group.Parent.RemoveChild(group);
        }

        xml.SaveFile();
        return true;
    }

    /// <summary>
    /// Code generators are build-time dependencies: <c>PrivateAssets="all"</c>, as the templates and Visual Studio write
    /// them, so they don't flow to the projects that reference this one.
    /// </summary>
    public static bool MarkPrivate(string projectPath, string id)
    {
        using var gate = StringCacheGate.Enter();
        using var collection = new ProjectCollection();
        var xml = ProjectRootElement.Open(projectPath, collection, preserveFormatting: true);
        xml.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
        var changed = false;
        foreach (var item in xml.Items.Where(i => i.ItemType == "PackageReference" && i.Include.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            if (item.Metadata.All(m => m.Name != "PrivateAssets"))
            {
                item.AddMetadata("PrivateAssets", "all", expressAsAttribute: false);
                item.AddMetadata("IncludeAssets", "runtime; build; native; contentfiles; analyzers; buildtransitive", expressAsAttribute: false);
                changed = true;
            }
        }

        if (changed)
        {
            xml.SaveFile();
        }

        return changed;
    }

    private static ProjectItemElement? Find(ProjectRootElement xml, string type, string file)
    {
        var directory = Path.GetDirectoryName(xml.FullPath)!;
        var full = Path.GetFullPath(file);
        return xml.Items.FirstOrDefault(i => i.ItemType == type
            && string.Equals(Path.GetFullPath(Path.Combine(directory, i.Include.Replace('\\', Path.DirectorySeparatorChar))), full, s_pathComparison));
    }

    private static string Relative(string projectPath, string file) =>
        Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, Path.GetFullPath(file)).Replace('/', '\\');

    private static void Write(ProjectItemElement item, string name, string? value)
    {
        var existing = item.Metadata.FirstOrDefault(m => m.Name == name);
        if (value is null)
        {
            if (existing is not null)
            {
                item.RemoveChild(existing);
            }
        }
        else if (existing is null)
        {
            item.AddMetadata(name, value, expressAsAttribute: true);
        }
        else if (existing.Value != value)
        {
            existing.Value = value;
        }
    }
}
