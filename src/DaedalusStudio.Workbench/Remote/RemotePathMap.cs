using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DaedalusStudio.Workbench.Remote;

/// <summary>
/// Paths between this machine and a remote one (M21, ADR 0049). The shell names a remote file by a path of this
/// machine's form under <see cref="LocalRoot"/>, so every <c>Path</c> API, comparer and tree of the shell works
/// unchanged: on Windows, a WSL distribution's files are under <c>\\wsl.localhost\Ubuntu</c> (a real path), another
/// machine's under <c>\\daedalus-remote.invalid\&lt;id&gt;</c>; on Linux and macOS under <c>/daedalus-remote/&lt;id&gt;</c>.
/// Neither of the last two exists here, so a feature that still reads files directly fails at once instead of
/// reading the wrong file. The remote side is a Unix file system. An identity map (no mapping) is for a server on
/// this machine of the same OS.
/// </summary>
public sealed class RemotePathMap
{
    private readonly StringComparison _comparison;

    private RemotePathMap(string? localRoot)
    {
        LocalRoot = localRoot is null ? null : Path.TrimEndingDirectorySeparator(localRoot);
        _comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>No mapping: the remote paths are this machine's (a stand-in server here).</summary>
    public static RemotePathMap Identity { get; } = new(null);

    /// <summary>Remote Unix paths under <paramref name="localRoot"/> here.</summary>
    public static RemotePathMap Under(string localRoot) => new(localRoot);

    /// <summary>The local root for a target: the WSL share for a distribution on Windows, else a folder that doesn't exist.</summary>
    public static string LocalRootFor(string id, string? wslDistribution) =>
        OperatingSystem.IsWindows()
            ? wslDistribution is { Length: > 0 } distribution ? @"\\wsl.localhost\" + distribution : @"\\daedalus-remote.invalid\" + id
            : "/daedalus-remote/" + id;

    /// <summary>Null for <see cref="Identity"/>.</summary>
    public string? LocalRoot { get; }

    public bool IsIdentity => LocalRoot is null;

    /// <summary>Whether a local path is a remote file's (under <see cref="LocalRoot"/>).</summary>
    public bool Covers(string localPath) =>
        LocalRoot is { } root && localPath.Length >= root.Length && localPath.StartsWith(root, _comparison)
        && (localPath.Length == root.Length || localPath[root.Length] is '\\' or '/');

    /// <summary>The remote path of a local one under the root; anything else comes back unchanged.</summary>
    public string ToRemote(string localPath)
    {
        if (!Covers(localPath))
        {
            return localPath;
        }

        var rest = localPath[LocalRoot!.Length..].Replace('\\', '/');
        return rest.Length == 0 ? "/" : rest;
    }

    /// <summary>The local path of an absolute remote one.</summary>
    public string ToLocal(string remotePath)
    {
        if (LocalRoot is not { } root || !remotePath.StartsWith('/'))
        {
            return remotePath;
        }

        return root + (OperatingSystem.IsWindows() ? remotePath.Replace('/', '\\') : remotePath);
    }

    // ------------------------------------------------------------------ JSON messages

    /// <summary>
    /// Rewrites a JSON message for the remote side: every string that is a local path under the root, or a
    /// <c>file:</c> URI of one, becomes the remote path or URI. Other strings (and property names) are kept.
    /// </summary>
    public byte[] MessageToRemote(ReadOnlySpan<byte> json) => IsIdentity ? json.ToArray() : Rewrite(json, StringToRemote);

    /// <summary>The reverse of <see cref="MessageToRemote"/>: remote absolute paths and their URIs become local ones.</summary>
    public byte[] MessageToLocal(ReadOnlySpan<byte> json) => IsIdentity ? json.ToArray() : Rewrite(json, StringToLocal);

    internal string? StringToRemote(string value)
    {
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile && Covers(LocalPathOf(uri)) ? RemoteUri(ToRemote(LocalPathOf(uri))) : null;
        }

        return Covers(value) ? ToRemote(value) : null;
    }

    internal string? StringToLocal(string value)
    {
        if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            // A URI is a path whatever it holds (spaces included, escaped).
            var path = Uri.UnescapeDataString(value["file://".Length..].Split('?', '#')[0]);
            return path.Length > 1 && path[1] != '/' ? new Uri(ToLocal(path)).AbsoluteUri : null;
        }

        return IsRemotePath(value) ? ToLocal(value) : null;
    }

    /// <summary>
    /// An absolute Unix path as a whole string: <c>/</c> and a name, no whitespace or control characters (so
    /// sentences, command lines and <c>//</c> comments stay as they are).
    /// </summary>
    internal static bool IsRemotePath(string value)
    {
        if (value.Length < 2 || value[0] != '/' || value[1] == '/')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string LocalPathOf(Uri uri)
    {
        var path = uri.LocalPath;
        return OperatingSystem.IsWindows() && !uri.IsUnc && path.Length >= 3 && path[0] == '/' && path[2] == ':' ? path[1..] : path;
    }

    /// <summary>A <c>file:</c> URI of a remote path, escaped as .NET escapes one on Unix.</summary>
    internal static string RemoteUri(string remotePath)
    {
        var builder = new StringBuilder("file://", remotePath.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(remotePath))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~/!$&'()*+,;=:@".Contains(c, StringComparison.Ordinal)))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static byte[] Rewrite(ReadOnlySpan<byte> json, Func<string, string?> map)
    {
        var output = new ArrayBufferWriter<byte>(json.Length + 64);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray:
                        writer.WriteStartArray();
                        break;
                    case JsonTokenType.EndArray:
                        writer.WriteEndArray();
                        break;
                    case JsonTokenType.PropertyName:
                        writer.WritePropertyName(reader.ValueSpan.Length == 0 && !reader.ValueIsEscaped ? string.Empty : reader.GetString()!);
                        break;
                    case JsonTokenType.String:
                        var text = reader.GetString()!;
                        writer.WriteStringValue(text.Length > 1 && (text[0] is '/' or '\\' or 'f' or 'F' || char.IsAsciiLetter(text[0])) ? map(text) ?? text : text);
                        break;
                    case JsonTokenType.Number:
                        writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                        break;
                    case JsonTokenType.True:
                        writer.WriteBooleanValue(true);
                        break;
                    case JsonTokenType.False:
                        writer.WriteBooleanValue(false);
                        break;
                    case JsonTokenType.Null:
                        writer.WriteNullValue();
                        break;
                }
            }
        }

        return output.WrittenSpan.ToArray();
    }
}
