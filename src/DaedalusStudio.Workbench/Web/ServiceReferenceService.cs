using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Web;

/// <summary>
/// A new service reference: <see cref="Source"/> is a file (copied into the project's <c>OpenAPIs</c> or <c>Protos</c>
/// folder unless it is in the project already) or an <c>http(s)</c> URL (downloaded there, and kept as the source that
/// Refresh downloads again). <see cref="Versions"/> pins package versions (otherwise the build host picks them).
/// </summary>
public sealed record ServiceReferenceRequest(
    string Project,
    string Kind,
    string Source,
    string? Namespace = null,
    string? ClassName = null,
    string? CodeGenerator = null,
    string? Options = null,
    string? GrpcServices = null,
    string? Access = null,
    bool Restore = true,
    ServiceReferencePackage[]? Versions = null);

/// <summary>
/// Service references (ADR 0041), Visual Studio's Connected Services for OpenAPI and gRPC: the document lives in the
/// project (copied or downloaded here), the build host writes the <c>OpenApiReference</c> or <c>Protobuf</c> item and
/// adds the packages, and the client (or server base) is generated at build time. UI-agnostic.
/// </summary>
public sealed class ServiceReferenceService(SolutionService solutions)
{
    public const string OpenApiFolder = "OpenAPIs";
    public const string ProtosFolder = "Protos";
    private const long MaxDocumentBytes = 16 * 1024 * 1024;

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static WebToolsJsonContext Json => WebToolsJsonContext.Default;

    /// <summary>A reference was added, changed, removed or refreshed in the project.</summary>
    public event EventHandler<string>? Changed;

    public Task<ServiceReferencesResult> ListAsync(string project, CancellationToken cancellationToken) =>
        solutions.RequestAsync(ServiceReferenceMethods.List, new ServiceReferencesParams(project), Json.ServiceReferencesParams, Json.ServiceReferencesResult, cancellationToken);

    public static bool IsUrl(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>Puts the document in the project and adds the reference; returns the document's path and the edit.</summary>
    public async Task<(string File, ServiceReferenceEditResult Edit)> AddAsync(ServiceReferenceRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind is not (ServiceReferenceKinds.OpenApi or ServiceReferenceKinds.Grpc))
        {
            throw new ArgumentException($"Unknown service reference kind '{request.Kind}'.");
        }

        var directory = Path.GetDirectoryName(request.Project)!;
        var folder = Path.Combine(directory, request.Kind == ServiceReferenceKinds.OpenApi ? OpenApiFolder : ProtosFolder);
        var source = request.Source.Trim();
        string file;
        string? sourceUrl = null;
        if (IsUrl(source))
        {
            sourceUrl = source;
            var name = Path.GetFileName(new Uri(source).AbsolutePath);
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                name = request.Kind == ServiceReferenceKinds.OpenApi ? "openapi.json" : "service.proto";
            }
            else if (request.Kind == ServiceReferenceKinds.Grpc && !name.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
            {
                name += ".proto";
            }

            Directory.CreateDirectory(folder);
            file = Unique(Path.Combine(folder, name));
            await DownloadAsync(source, file, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var path = Path.GetFullPath(source);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"{path} doesn't exist.", path);
            }

            if (IsUnder(path, directory))
            {
                file = path;
            }
            else
            {
                Directory.CreateDirectory(folder);
                file = Unique(Path.Combine(folder, Path.GetFileName(path)));
                File.Copy(path, file);
            }
        }

        var edit = await solutions.RequestAsync(
            ServiceReferenceMethods.Add,
            new ServiceReferenceParams(request.Project, request.Kind, file, sourceUrl, Empty(request.Namespace), Empty(request.ClassName), Empty(request.CodeGenerator), Empty(request.Options), Empty(request.GrpcServices), Empty(request.Access), request.Restore, request.Versions),
            Json.ServiceReferenceParams,
            Json.ServiceReferenceEditResult,
            cancellationToken,
            TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        Changed?.Invoke(this, request.Project);
        return (file, edit);
    }

    /// <summary>Downloads the reference's document again from its <see cref="ServiceReferenceInfo.SourceUrl"/>.</summary>
    public async Task RefreshAsync(string project, ServiceReferenceInfo reference, CancellationToken cancellationToken)
    {
        if (reference.SourceUrl is not { Length: > 0 } url)
        {
            throw new InvalidOperationException($"{Path.GetFileName(reference.Path)} has no source URL: it was added from a file, so it changes when you edit it.");
        }

        await DownloadAsync(url, reference.Path, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, project);
    }

    public async Task<ServiceReferenceEditResult> UpdateAsync(ServiceReferenceParams parameters, CancellationToken cancellationToken)
    {
        var edit = await solutions.RequestAsync(ServiceReferenceMethods.Update, parameters, Json.ServiceReferenceParams, Json.ServiceReferenceEditResult, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, parameters.Project);
        return edit;
    }

    public async Task<ServiceReferenceEditResult> RemoveAsync(string project, ServiceReferenceInfo reference, bool deleteFile, CancellationToken cancellationToken)
    {
        var edit = await solutions.RequestAsync(
            ServiceReferenceMethods.Remove,
            new ServiceReferenceRemoveParams(project, reference.Kind, reference.Path, deleteFile),
            Json.ServiceReferenceRemoveParams,
            Json.ServiceReferenceEditResult,
            cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, project);
        return edit;
    }

    /// <summary>Downloads to a temporary file first, so a failed download leaves the document as it was.</summary>
    private static async Task DownloadAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await s_http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        if (response.Content.Headers.ContentLength > MaxDocumentBytes)
        {
            throw new InvalidOperationException($"{url} is larger than {MaxDocumentBytes / 1024 / 1024} MB.");
        }

        var temporary = path + ".download";
        await using (var target = File.Create(temporary))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxDocumentBytes)
                {
                    throw new InvalidOperationException($"{url} is larger than {MaxDocumentBytes / 1024 / 1024} MB.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name}{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
