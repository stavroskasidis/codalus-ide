using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DaedalusStudio.Platform.Credentials;

/// <summary>
/// The macOS Keychain: generic passwords (<c>kSecClassGenericPassword</c>) in the user's default keychain, found by
/// their service and account attributes, through the <c>SecItem*</c> functions with Core Foundation dictionaries. The
/// constants (<c>kSecClass</c>…) are read from the frameworks' exports.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacKeychainCredentialStore : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const uint CFStringEncodingUtf8 = 0x08000100;

    public string Name => "macOS Keychain";

    public bool IsPersistent => true;

    public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var query = new Query(service, account);
            query.Add(Constants.ReturnData, Constants.True);
            query.Add(Constants.MatchLimit, Constants.MatchLimitOne);
            var status = SecItemCopyMatching(query.Build(), out var data);
            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            Check(status, "read");
            try
            {
                var length = (int)CFDataGetLength(data);
                return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(CFDataGetBytePtr(data), length));
            }
            finally
            {
                CFRelease(data);
            }
        }, cancellationToken);

    public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var bytes = Encoding.UTF8.GetBytes(secret);
            nint data;
            fixed (byte* pointer = bytes)
            {
                data = CFDataCreate(0, pointer, bytes.Length);
            }

            try
            {
                using (var query = new Query(service, account))
                using (var update = new Query())
                {
                    update.Add(Constants.ValueData, data);
                    var status = SecItemUpdate(query.Build(), update.Build());
                    if (status != ErrSecItemNotFound)
                    {
                        Check(status, "update");
                        return;
                    }
                }

                using var add = new Query(service, account);
                add.Add(Constants.Label, add.Own(CFString($"Daedalus Studio: {account}")));
                add.Add(Constants.ValueData, data);
                Check(SecItemAdd(add.Build(), 0), "add");
            }
            finally
            {
                CFRelease(data);
            }
        }, cancellationToken);

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var query = new Query(service, account);
            var status = SecItemDelete(query.Build());
            if (status == ErrSecItemNotFound)
            {
                return false;
            }

            Check(status, "delete");
            return true;
        }, cancellationToken);

    private static void Check(int status, string operation)
    {
        if (status == ErrSecSuccess)
        {
            return;
        }

        var message = SecCopyErrorMessageString(status, 0) is var text and not 0 ? FromCFString(text, release: true) : null;
        throw new CredentialStoreUnavailableException(
            $"The macOS Keychain could not {operation} the item: {message ?? "unknown error"} (OSStatus {status.ToString(CultureInfo.InvariantCulture)}).");
    }

    private static nint CFString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* pointer = bytes)
        {
            return CFStringCreateWithCString(0, pointer, CFStringEncodingUtf8);
        }
    }

    private static string? FromCFString(nint value, bool release)
    {
        try
        {
            var length = CFStringGetLength(value);
            var size = (length * 4) + 1;
            var buffer = new byte[size];
            fixed (byte* pointer = buffer)
            {
                if (!CFStringGetCString(value, pointer, size, CFStringEncodingUtf8))
                {
                    return null;
                }
            }

            return Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
        }
        finally
        {
            if (release)
            {
                CFRelease(value);
            }
        }
    }

    /// <summary>A CFDictionary under construction; owns the CF objects it created, and the dictionary.</summary>
    private sealed class Query : IDisposable
    {
        private readonly List<nint> _keys = [];
        private readonly List<nint> _values = [];
        private readonly List<nint> _owned = [];
        private nint _dictionary;

        public Query()
        {
        }

        public Query(string service, string account)
        {
            Add(Constants.Class, Constants.ClassGenericPassword);
            Add(Constants.AttrService, Own(CFString(service)));
            Add(Constants.AttrAccount, Own(CFString(account)));
        }

        public nint Own(nint value)
        {
            _owned.Add(value);
            return value;
        }

        public void Add(nint key, nint value)
        {
            _keys.Add(key);
            _values.Add(value);
        }

        public nint Build()
        {
            if (_dictionary != 0)
            {
                CFRelease(_dictionary);
            }

            var keys = _keys.ToArray();
            var values = _values.ToArray();
            fixed (nint* k = keys)
            fixed (nint* v = values)
            {
                _dictionary = CFDictionaryCreate(0, k, v, keys.Length, Constants.KeyCallBacks, Constants.ValueCallBacks);
            }

            return _dictionary;
        }

        public void Dispose()
        {
            if (_dictionary != 0)
            {
                CFRelease(_dictionary);
            }

            foreach (var value in _owned)
            {
                CFRelease(value);
            }
        }
    }

    /// <summary>The frameworks' exported constants, read once.</summary>
    private static class Constants
    {
        private static readonly nint s_security = NativeLibrary.Load(Security);
        private static readonly nint s_coreFoundation = NativeLibrary.Load(CoreFoundation);

        public static readonly nint Class = Value(s_security, "kSecClass");
        public static readonly nint ClassGenericPassword = Value(s_security, "kSecClassGenericPassword");
        public static readonly nint AttrService = Value(s_security, "kSecAttrService");
        public static readonly nint AttrAccount = Value(s_security, "kSecAttrAccount");
        public static readonly nint Label = Value(s_security, "kSecAttrLabel");
        public static readonly nint ValueData = Value(s_security, "kSecValueData");
        public static readonly nint ReturnData = Value(s_security, "kSecReturnData");
        public static readonly nint MatchLimit = Value(s_security, "kSecMatchLimit");
        public static readonly nint MatchLimitOne = Value(s_security, "kSecMatchLimitOne");
        public static readonly nint True = Value(s_coreFoundation, "kCFBooleanTrue");

        // The callback structures themselves are passed, by address.
        public static readonly nint KeyCallBacks = NativeLibrary.GetExport(s_coreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly nint ValueCallBacks = NativeLibrary.GetExport(s_coreFoundation, "kCFTypeDictionaryValueCallBacks");

        /// <summary>A <c>CFStringRef</c> (or <c>CFBooleanRef</c>) global: the export is the variable's address.</summary>
        private static nint Value(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);
    }

    [LibraryImport(Security)]
    private static partial int SecItemAdd(nint attributes, nint result);

    [LibraryImport(Security)]
    private static partial int SecItemCopyMatching(nint query, out nint result);

    [LibraryImport(Security)]
    private static partial int SecItemUpdate(nint query, nint attributesToUpdate);

    [LibraryImport(Security)]
    private static partial int SecItemDelete(nint query);

    [LibraryImport(Security)]
    private static partial nint SecCopyErrorMessageString(int status, nint reserved);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
