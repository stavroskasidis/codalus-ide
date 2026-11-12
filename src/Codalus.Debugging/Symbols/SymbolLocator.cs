using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection.Metadata;

namespace Codalus.Debugging.Symbols;

/// <summary>What a lookup found: the PDB's local path (null: none), and one line for the Modules window saying where from or why not.</summary>
public sealed record SymbolResult(string? PdbPath, string Status)
{
    public bool Found => PdbPath is not null;
}

/// <summary>
/// Finds the PDB of a module that has none next to it (M58), as <see cref="SymbolOptions"/> says: in the cache, then in
/// each search path, then on each symbol server, whose file is kept in the cache for the next time. A PDB is only
/// returned when it belongs to the module's build. One locator serves one debugging session: it remembers what the
/// servers didn't have, so each is asked once per PDB, and <see cref="Dispose"/> ends the downloads still running.
/// </summary>
public sealed class SymbolLocator : IDisposable
{
    private const int MaxRedirects = 5;
    private static readonly byte[] s_windowsPdbHeader = "Microsoft C/C++ MSF 7.00"u8.ToArray();

    private readonly HttpClient _http;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _disposed = new();
    private readonly CancellationToken _stop;

    /// <summary>Downloads in progress by request URL, so lookups of one PDB at the same time share one request.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<SymbolResult>>> _downloads = new(StringComparer.Ordinal);

    /// <summary>Request URLs that gave nothing usable, with the status that said so.</summary>
    private readonly ConcurrentDictionary<string, string> _failed = new(StringComparer.Ordinal);

    /// <summary>Servers that didn't answer: the session's other modules don't each wait for the timeout again.</summary>
    private readonly ConcurrentDictionary<string, bool> _unreachable = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="options">Where to look.</param>
    /// <param name="http">The client the servers are asked with; it stays the caller's, and isn't disposed here.</param>
    /// <param name="log">Receives a line for each place that was skipped or asked (the Debug output).</param>
    public SymbolLocator(SymbolOptions options, HttpClient http, Action<string>? log = null)
    {
        Options = options;
        _http = http;
        _log = log;
        _stop = _disposed.Token;
    }

    public SymbolOptions Options { get; }

    /// <summary>How long a server may take to answer, and to send the next part of a file, before it is given up on.</summary>
    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Looks for <paramref name="modulePath"/>'s PDB. <paramref name="ignoreFilter"/> is the user's "Load Symbols": the
    /// module is looked up even when the include and exclude lists leave it out. Network and file failures are part of
    /// the status, never exceptions; only a cancelled <paramref name="cancellationToken"/> throws.
    /// </summary>
    public Task<SymbolResult> FindAsync(string modulePath, bool ignoreFilter, CancellationToken cancellationToken) =>
        Task.Run(() => FindCoreAsync(modulePath, ignoreFilter, cancellationToken), cancellationToken); // Off the caller's thread: a search path may be a slow share.

    /// <summary>Forgets which servers failed and what they didn't have, so the next lookup asks them again.</summary>
    public void ForgetFailures()
    {
        _failed.Clear();
        _unreachable.Clear();
    }

    /// <summary>Ends the downloads still running; their temporary files are removed.</summary>
    public void Dispose()
    {
        if (!_stop.IsCancellationRequested)
        {
            _disposed.Cancel();
            _disposed.Dispose();
        }
    }

    private async Task<SymbolResult> FindCoreAsync(string modulePath, bool ignoreFilter, CancellationToken cancellationToken)
    {
        var module = SymbolIdentity.FileName(modulePath);
        if (!ignoreFilter && !Options.Includes(module))
        {
            return new SymbolResult(null, Options.ModuleFilter == SymbolModuleFilter.OnlySpecified
                ? "Skipped: not among the modules the symbol settings include."
                : "Skipped: excluded by the symbol settings.");
        }

        SymbolIdentity? identity;
        try
        {
            identity = SymbolIdentity.Read(modulePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new SymbolResult(null, $"No symbols: {module} couldn't be read ({FirstLine(ex.Message)}).");
        }

        if (identity is null)
        {
            return new SymbolResult(null, "No symbols: the module doesn't name a PDB.");
        }

        // What was skipped on the way; the status when nothing is found.
        List<string> notes = [];
        var keyPath = identity.Key.Replace('/', Path.DirectorySeparatorChar);
        if (Options.CacheFolder is { Length: > 0 } cache && Accept(Path.Combine(cache, keyPath), identity, inStore: true, notes) is { } cached)
        {
            return Loaded(module, cached, "Symbols loaded from the cache.");
        }

        foreach (var folder in Options.SearchPaths.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Accept(Path.Combine(folder, identity.PdbFileName), identity, inStore: false, notes) is { } flat)
            {
                return Loaded(module, flat, $"Symbols loaded from {folder}.");
            }

            if (Accept(Path.Combine(folder, keyPath), identity, inStore: true, notes) is { } stored)
            {
                return Loaded(module, stored, $"Symbols loaded from {folder}.");
            }
        }

        foreach (var server in Options.Servers.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await AskServerAsync(server.Trim(), identity, cancellationToken).ConfigureAwait(false);
            if (result.PdbPath is { } downloaded)
            {
                return Loaded(module, downloaded, result.Status);
            }

            notes.Add(result.Status);
        }

        var status = notes.Count > 0 ? string.Join(' ', notes) : $"No symbols: {identity.PdbFileName} isn't in the cache or the symbol paths.";
        _log?.Invoke($"{module}: {status}");
        return new SymbolResult(null, status);
    }

    private SymbolResult Loaded(string module, string pdbPath, string status)
    {
        _log?.Invoke($"{module}: {status} ({pdbPath})");
        return new SymbolResult(pdbPath, status);
    }

    /// <summary>The file at <paramref name="path"/> when it is the module's PDB; a file that is there but isn't adds a note.</summary>
    private static string? Accept(string path, SymbolIdentity identity, bool inStore, List<string> notes)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (Mismatch(path, identity, inStore) is not { } problem)
            {
                return path;
            }

            notes.Add($"Skipped {path}: {problem}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"Skipped {path}: {FirstLine(ex.Message)}");
        }

        return null;
    }

    /// <summary>
    /// Why the file isn't the module's PDB, or null when it is. A portable PDB carries the id the module names (its
    /// first 16 bytes are the GUID). A Windows PDB isn't read here, so it is taken on the word of the place it is in:
    /// the folder of a symbol store, the cache or a server's answer is named after the id; a loose file's name says
    /// nothing about its build.
    /// </summary>
    private static string? Mismatch(string path, SymbolIdentity identity, bool inStore)
    {
        if (!identity.IsPortable && !inStore)
        {
            return "a Windows PDB outside a symbol store can't be checked against the module";
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!identity.IsPortable)
        {
            // Not a check of the build, only that it is a PDB and not, say, a proxy's error page.
            var header = new byte[s_windowsPdbHeader.Length];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return header.AsSpan(0, read).SequenceEqual(s_windowsPdbHeader) ? null : "it isn't a Windows PDB";
        }

        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var id = provider.GetMetadataReader().DebugMetadataHeader?.Id ?? [];
            return id.Length >= 16 && new Guid(id.AsSpan(0, 16)) == identity.Guid ? null : "it belongs to another build of the module";
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return "it isn't a portable PDB";
        }
    }

    /// <summary>Asks one server for the PDB, sharing the request with the lookups that ask for the same one right now.</summary>
    private async Task<SymbolResult> AskServerAsync(string server, SymbolIdentity identity, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return new SymbolResult(null, $"Skipped '{server}': it isn't a symbol server's URL.");
        }

        if (!IsAllowed(address))
        {
            return new SymbolResult(null, $"Skipped {address.Authority}: a symbol server must be https://.");
        }

        if (Options.CacheFolder is not { Length: > 0 } cache)
        {
            return new SymbolResult(null, $"Skipped {address.Authority}: there's no cache folder to download to.");
        }

        // The key under the server's path; a query (an access token) stays where it is.
        var path = address.AbsolutePath.TrimEnd('/') + "/" + string.Join('/', identity.Key.Split('/').Select(Uri.EscapeDataString));
        var request = new Uri(address.GetLeftPart(UriPartial.Authority) + path + address.Query);

        // The download doesn't end with the lookup that started it (others may be waiting for it, and the file is good
        // for the next session): each lookup only stops waiting. Dispose ends it.
        var download = _downloads.GetOrAdd(request.AbsoluteUri, _ => new Lazy<Task<SymbolResult>>(() => Task.Run(() => DownloadAsync(request, identity, cache))));
        return await download.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAllowed(Uri address) => address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback);

    private async Task<SymbolResult> DownloadAsync(Uri request, SymbolIdentity identity, string cache)
    {
        var host = request.Authority;
        var target = Path.Combine(cache, identity.Key.Replace('/', Path.DirectorySeparatorChar));
        string? temp = null;
        try
        {
            // Checked here, where one lookup at a time gets: the one before may have just finished with this answer.
            if (_failed.TryGetValue(request.AbsoluteUri, out var before))
            {
                return new SymbolResult(null, before);
            }

            if (_unreachable.ContainsKey(host))
            {
                return new SymbolResult(null, $"Skipped {host}: it didn't answer earlier in this session.");
            }

            if (File.Exists(target) && Mismatch(target, identity, inStore: true) is null)
            {
                return new SymbolResult(target, "Symbols loaded from the cache.");
            }

            _log?.Invoke($"Asking {host} for {identity.Key}.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop);
            timeout.CancelAfter(ServerTimeout);
            try
            {
                using var response = await SendAsync(request, identity, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                    return Failed(request, response.StatusCode == HttpStatusCode.NotFound ? $"Not found on {host} ({code})."
                        : response.Headers.Location is not null ? $"{host} redirects to an address that isn't followed ({code})."
                        : $"{host} answered with an error ({code}).");
                }

                Directory.CreateDirectory(cache);
                temp = Path.Combine(cache, "download-" + Guid.NewGuid().ToString("N") + ".tmp");
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                        timeout.CancelAfter(ServerTimeout); // A large PDB may take longer than the timeout, as long as it keeps coming.
                    }
                }
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
                _unreachable[host] = true;
                return new SymbolResult(null, $"{host} didn't answer within {ServerTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");
            }
            catch (HttpRequestException ex)
            {
                _unreachable[host] = true;
                return new SymbolResult(null, $"{host} couldn't be reached: {FirstLine(ex.Message)}");
            }

            // Only a file that belongs to the module gets the key's place in the cache: a wrong one there would be
            // trusted by every later session (and, for a Windows PDB, by other debuggers that share the folder).
            if (Mismatch(temp, identity, inStore: true) is { } problem)
            {
                return Failed(request, $"Skipped the file {host} sent: {problem}.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target, overwrite: true);
            return new SymbolResult(target, $"Symbols loaded from the cache (downloaded from {host}).");
        }
        catch (OperationCanceledException)
        {
            return new SymbolResult(null, $"The download from {host} was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(request, $"The download from {host} failed: {FirstLine(ex.Message)}");
        }
        finally
        {
            Delete(temp);
            _downloads.TryRemove(request.AbsoluteUri, out _);
        }
    }

    /// <summary>
    /// Sends the request and follows redirects (Microsoft's server answers with one to its storage), whether or not the
    /// client's handler does. One to a plain <c>http://</c> address isn't followed (unless the server itself is one on
    /// this machine): the redirect itself is the answer.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri address, SymbolIdentity identity, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (identity.ChecksumHeader is { } checksum)
            {
                request.Headers.TryAddWithoutValidation("SymbolChecksum", checksum);
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location
                || redirects == MaxRedirects || new Uri(address, location) is not { } next || !IsAllowed(next) || (next.Scheme != address.Scheme && next.Scheme != Uri.UriSchemeHttps))
            {
                return response;
            }

            response.Dispose();
            address = next;
        }
    }

    private SymbolResult Failed(Uri request, string status)
    {
        _failed[request.AbsoluteUri] = status;
        return new SymbolResult(null, status);
    }

    private static void Delete(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FirstLine(string message) => message.Trim().Split('\r', '\n')[0];
}
