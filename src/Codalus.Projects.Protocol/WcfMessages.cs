using System.Text.Json.Serialization;

namespace Codalus.Projects.Protocol;

/// <summary>
/// WCF service references (M42, ADR 0069) on the build host connection. The host runs the user's own
/// <c>dotnet-svcutil</c> (a local or global .NET tool) as a child process in the project's folder, trusted solutions only,
/// one at a time, its process tree killed on cancel. A reference lives where the classic IDE puts it:
/// <c>Connected Services/&lt;name&gt;/Reference.cs</c> next to a <c>ConnectedService.json</c> in the classic IDE's format,
/// which <c>dotnet-svcutil --update</c> also reads.
/// </summary>
public static class WcfReferenceMethods
{
    /// <summary>Whether <c>dotnet-svcutil</c> runs in the project's folder, local or global, and its version.</summary>
    public const string Status = "wcfReferences/status";

    /// <summary>Installs <c>dotnet-svcutil</c> as a local tool (a manifest first), or restores the manifest's tools.</summary>
    public const string Install = "wcfReferences/install";

    /// <summary>The project's references: each <c>ConnectedService.json</c> (or <c>dotnet-svcutil.params.json</c>) below it.</summary>
    public const string List = "wcfReferences/list";

    /// <summary>Generates a new reference from a URL or a WSDL file.</summary>
    public const string Add = "wcfReferences/add";

    /// <summary>Writes changed options into the reference's <c>ConnectedService.json</c>, then generates it again.</summary>
    public const string Update = "wcfReferences/update";

    /// <summary>Sends the reference's folder to the trash and answers the references left. The packages stay.</summary>
    public const string Remove = "wcfReferences/remove";

    /// <summary>Host → shell notification: a line of the running <c>dotnet-svcutil</c> (<see cref="ScaffoldOutputParams"/>).</summary>
    public const string Output = "wcfReferences/output";
}

public sealed record WcfToolStatusParams(string Project);

/// <summary>
/// <see cref="ToolSource"/>: <c>local</c> (the tool manifest), <c>global</c>, or <c>CODALUS_SVCUTIL</c> (a stand-in).
/// <see cref="Message"/> says what's missing when the tool can't run.
/// </summary>
public sealed record WcfToolStatusResult(
    bool ToolAvailable,
    string? ToolVersion,
    string? ToolSource,
    string? Manifest,
    bool ManifestListsTool,
    string? Message);

/// <summary><see cref="Version"/>: null picks the newest stable release.</summary>
public sealed record WcfToolInstallParams(string Project, string? Version = null);

public sealed record WcfReferencesParams(string Project);

/// <summary>
/// A reference's options, as <c>dotnet-svcutil</c> takes them. <see cref="Name"/> is the folder under
/// <c>Connected Services</c>; <see cref="Input"/> a URL (<c>…?wsdl</c>, a MEX address) or a WSDL file. A null
/// <see cref="Namespace"/> maps every target namespace to <see cref="Name"/> (the classic IDE's default).
/// <see cref="CollectionType"/> and <see cref="DictionaryCollectionType"/>: full type names (<c>System.Array</c>,
/// <c>System.Collections.Generic.List`1</c>, <c>System.Collections.Generic.Dictionary`2</c>). <see cref="Sync"/>: synchronous
/// methods as well as task-based ones. <see cref="ReuseTypes"/>: reuse types from the project's references.
/// <see cref="Internal"/>: internal classes. For <c>wcfReferences/update</c>, a null <see cref="Input"/> keeps the reference's.
/// </summary>
public sealed record WcfReferenceParams(
    string Project,
    string Name,
    string? Input,
    string? Namespace = null,
    string? CollectionType = null,
    string? DictionaryCollectionType = null,
    bool Sync = false,
    bool ReuseTypes = true,
    bool Internal = false);

/// <summary>
/// A reference read from its options file. <see cref="Folder"/>: its folder; <see cref="OptionsFile"/>: the
/// <c>ConnectedService.json</c> or <c>dotnet-svcutil.params.json</c> in it; <see cref="Code"/>: the generated file (null
/// when missing). <see cref="Inputs"/> as written (a relative file is relative to <see cref="Folder"/>).
/// </summary>
public sealed record WcfReferenceInfo(
    string Name,
    string Folder,
    string OptionsFile,
    string? Code,
    string[] Inputs,
    string? Namespace,
    string? CollectionType,
    string? DictionaryCollectionType,
    bool Sync,
    bool ReuseTypes,
    bool Internal,
    string? TargetFramework);

public sealed record WcfReferencesResult(WcfReferenceInfo[] References);

/// <summary>The reference after the run, the files <c>dotnet-svcutil</c> created and changed, and its output.</summary>
public sealed record WcfReferenceRunResult(WcfReferenceInfo Reference, string[] Created, string[] Modified, string Output);

public sealed record WcfReferenceRemoveParams(string Project, string Name);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WcfToolStatusParams))]
[JsonSerializable(typeof(WcfToolStatusResult))]
[JsonSerializable(typeof(WcfToolInstallParams))]
[JsonSerializable(typeof(WcfReferencesParams))]
[JsonSerializable(typeof(WcfReferencesResult))]
[JsonSerializable(typeof(WcfReferenceParams))]
[JsonSerializable(typeof(WcfReferenceRunResult))]
[JsonSerializable(typeof(WcfReferenceRemoveParams))]
[JsonSerializable(typeof(ScaffoldOutputParams))]
public sealed partial class WcfJsonContext : JsonSerializerContext;
