using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DaedalusStudio.Platform.Credentials;

/// <summary>
/// Windows Credential Manager: generic credentials named <c>Daedalus Studio:&lt;service&gt;:&lt;account&gt;</c>, kept for the
/// user on this computer (<c>CRED_PERSIST_LOCAL_MACHINE</c>). Credential Manager holds at most 2,560 bytes per credential,
/// so a longer secret (an Entra ID refresh token with its access token) is split over numbered parts.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlob = 5 * 512;

    // A blob that starts with 0xFF isn't UTF-8, so it can't be a secret: it says how many parts follow.
    private const byte PartsMarker = 0xFF;

    public string Name => "Windows Credential Manager";

    public bool IsPersistent => true;

    private static string Target(string service, string account) => $"Daedalus Studio:{service}:{account}";

    private static string PartTarget(string target, int index) => target + ":part" + index.ToString(CultureInfo.InvariantCulture);

    public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var target = Target(service, account);
            if (Read(target) is not { } blob)
            {
                return null;
            }

            if (blob.Length < 2 || blob[0] != PartsMarker)
            {
                return Encoding.UTF8.GetString(blob);
            }

            var count = int.Parse(Encoding.ASCII.GetString(blob, 1, blob.Length - 1), CultureInfo.InvariantCulture);
            var whole = new List<byte>();
            for (var i = 0; i < count; i++)
            {
                whole.AddRange(Read(PartTarget(target, i)) ?? throw new CredentialStoreUnavailableException($"A part of the credential '{target}' is missing."));
            }

            return Encoding.UTF8.GetString([.. whole]);
        }, cancellationToken);

    public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var target = Target(service, account);
            var bytes = Encoding.UTF8.GetBytes(secret);
            var previousParts = PartCount(target);
            if (bytes.Length <= MaxBlob)
            {
                Write(target, account, bytes);
                DeleteParts(target, 0, previousParts);
                return;
            }

            var count = (bytes.Length + MaxBlob - 1) / MaxBlob;
            for (var i = 0; i < count; i++)
            {
                Write(PartTarget(target, i), account, bytes.AsSpan(i * MaxBlob, Math.Min(MaxBlob, bytes.Length - (i * MaxBlob))).ToArray());
            }

            Write(target, account, [PartsMarker, .. Encoding.ASCII.GetBytes(count.ToString(CultureInfo.InvariantCulture))]);
            DeleteParts(target, count, previousParts);
        }, cancellationToken);

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var target = Target(service, account);
            DeleteParts(target, 0, PartCount(target));
            return Delete(target);
        }, cancellationToken);

    private static int PartCount(string target) =>
        Read(target) is { Length: >= 2 } blob && blob[0] == PartsMarker
            ? int.Parse(Encoding.ASCII.GetString(blob, 1, blob.Length - 1), CultureInfo.InvariantCulture)
            : 0;

    private static void DeleteParts(string target, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            _ = Delete(PartTarget(target, i));
        }
    }

    private static byte[]? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credential))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound ? null : throw Unavailable("read", error);
        }

        try
        {
            return new ReadOnlySpan<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize).ToArray();
        }
        finally
        {
            CredFree(credential);
        }
    }

    private static void Write(string target, string account, byte[] blob)
    {
        fixed (char* targetName = target)
        fixed (char* userName = account)
        fixed (char* comment = "Stored by Daedalus Studio")
        fixed (byte* data = blob)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };
            if (!CredWrite(&credential, 0))
            {
                throw Unavailable("write", Marshal.GetLastPInvokeError());
            }
        }
    }

    private static bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNotFound ? false : throw Unavailable("delete", error);
    }

    private static CredentialStoreUnavailableException Unavailable(string operation, int error) =>
        new($"Windows Credential Manager could not {operation} the credential: {Marshal.GetPInvokeErrorMessage(error)} (error {error.ToString(CultureInfo.InvariantCulture)}).");

    /// <summary><c>CREDENTIALW</c>; <c>FILETIME</c> is two 32-bit halves.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(void* buffer);
}
