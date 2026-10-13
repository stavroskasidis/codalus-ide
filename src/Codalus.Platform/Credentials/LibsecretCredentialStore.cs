using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Codalus.Platform.Credentials;

/// <summary>
/// The Secret Service (GNOME Keyring, KWallet's Secret Service, KeePassXC…) through libsecret: passwords in the default
/// collection with the schema <c>org.codalus.Credential</c> (attributes <c>service</c> and <c>account</c>). Uses
/// the non-variadic <c>secret_password_*v_sync</c> functions, whose attributes are a <c>GHashTable</c>. A missing
/// libsecret, or no Secret Service on the session bus, is <see cref="CredentialStoreUnavailableException"/>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe partial class LibsecretCredentialStore : ICredentialStore
{
    private const string Libsecret = "libsecret-1.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const string Gobject = "libgobject-2.0.so.0";

    public string Name => "Secret Service (libsecret)";

    public bool IsPersistent => true;

    public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Call(service, account, (attributes, cancellable) =>
        {
            GError* error = null;
            var password = secret_password_lookupv_sync(Schema.Pointer, attributes, cancellable, &error);
            Check(error, "read");
            if (password == null)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8((nint)password);
            }
            finally
            {
                secret_password_free(password);
            }
        }, cancellationToken);

    public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default) =>
        Call<object?>(service, account, (attributes, cancellable) =>
        {
            var label = Marshal.StringToCoTaskMemUTF8($"Codalus: {account}");
            var password = Marshal.StringToCoTaskMemUTF8(secret);
            try
            {
                GError* error = null;
                _ = secret_password_storev_sync(Schema.Pointer, attributes, 0, label, password, cancellable, &error);
                Check(error, "store");
                return null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(label);
                Wipe(password, System.Text.Encoding.UTF8.GetByteCount(secret));
            }
        }, cancellationToken);

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Call(service, account, (attributes, cancellable) =>
        {
            GError* error = null;
            var removed = secret_password_clearv_sync(Schema.Pointer, attributes, cancellable, &error);
            Check(error, "delete");
            return removed != 0;
        }, cancellationToken);

    /// <summary>Runs a libsecret call off the caller's thread with the item's attributes, cancellable through a <c>GCancellable</c>.</summary>
    private static Task<T> Call<T>(string service, string account, Func<nint, nint, T> body, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            nint table, cancellable;
            try
            {
                table = g_hash_table_new(Schema.StrHash, Schema.StrEqual);
                cancellable = g_cancellable_new();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
            {
                throw NotInstalled(ex);
            }

            var serviceKey = Marshal.StringToCoTaskMemUTF8("service");
            var serviceValue = Marshal.StringToCoTaskMemUTF8(service);
            var accountKey = Marshal.StringToCoTaskMemUTF8("account");
            var accountValue = Marshal.StringToCoTaskMemUTF8(account);
            var registration = cancellationToken.Register(() => g_cancellable_cancel(cancellable));
            try
            {
                _ = g_hash_table_insert(table, serviceKey, serviceValue);
                _ = g_hash_table_insert(table, accountKey, accountValue);
                return body(table, cancellable);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw NotInstalled(ex);
            }
            finally
            {
                registration.Dispose();
                g_hash_table_unref(table);
                g_object_unref(cancellable);
                Marshal.FreeCoTaskMem(serviceKey);
                Marshal.FreeCoTaskMem(serviceValue);
                Marshal.FreeCoTaskMem(accountKey);
                Marshal.FreeCoTaskMem(accountValue);
            }
        }, cancellationToken);

    private static CredentialStoreUnavailableException NotInstalled(Exception inner) =>
        new("libsecret is not installed (libsecret-1.so.0), so secrets can't be kept in the Secret Service.", inner);

    private static void Check(GError* error, string operation)
    {
        if (error == null)
        {
            return;
        }

        var message = Marshal.PtrToStringUTF8(error->Message) ?? "unknown error";
        g_error_free(error);
        throw new CredentialStoreUnavailableException($"The Secret Service could not {operation} the secret: {message}");
    }

    private static void Wipe(nint text, int length)
    {
        new Span<byte>((void*)text, length).Clear();
        Marshal.FreeCoTaskMem(text);
    }

    /// <summary>The schema (a <c>SecretSchema</c> kept for the process's lifetime) and glib's string hash functions.</summary>
    private static class Schema
    {
        // SecretSchema on 64-bit: name (8), flags (4 + 4 padding), 32 attributes of { name, type } (16 each),
        // then reserved fields; 1 KiB covers it, zeroed so the attribute list ends after ours.
        public static readonly nint Pointer = Create();

        private static readonly nint s_glib = NativeLibrary.Load(Glib);

        public static readonly nint StrHash = NativeLibrary.GetExport(s_glib, "g_str_hash");

        public static readonly nint StrEqual = NativeLibrary.GetExport(s_glib, "g_str_equal");

        private static nint Create()
        {
            var schema = (byte*)NativeMemory.AllocZeroed(1024);
            *(nint*)schema = Marshal.StringToCoTaskMemUTF8("org.codalus.Credential");
            *(int*)(schema + 8) = 0; // SECRET_SCHEMA_NONE
            *(nint*)(schema + 16) = Marshal.StringToCoTaskMemUTF8("service");
            *(int*)(schema + 24) = 0; // SECRET_SCHEMA_ATTRIBUTE_STRING
            *(nint*)(schema + 32) = Marshal.StringToCoTaskMemUTF8("account");
            *(int*)(schema + 40) = 0;
            return (nint)schema;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public nint Message;
    }

    [LibraryImport(Libsecret)]
    private static partial byte* secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, GError** error);

    [LibraryImport(Libsecret)]
    private static partial int secret_password_storev_sync(nint schema, nint attributes, nint collection, nint label, nint password, nint cancellable, GError** error);

    [LibraryImport(Libsecret)]
    private static partial int secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, GError** error);

    [LibraryImport(Libsecret)]
    private static partial void secret_password_free(byte* password);

    [LibraryImport(Glib)]
    private static partial nint g_hash_table_new(nint hash, nint equal);

    [LibraryImport(Glib)]
    private static partial int g_hash_table_insert(nint table, nint key, nint value);

    [LibraryImport(Glib)]
    private static partial void g_hash_table_unref(nint table);

    [LibraryImport(Glib)]
    private static partial void g_error_free(GError* error);

    [LibraryImport(Gio)]
    private static partial nint g_cancellable_new();

    [LibraryImport(Gio)]
    private static partial void g_cancellable_cancel(nint cancellable);

    [LibraryImport(Gobject)]
    private static partial void g_object_unref(nint instance);
}
