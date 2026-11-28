using System.Text.Json;
using Codalus.Workbench.Secrets;

namespace Codalus.Workbench.Mobile;

/// <summary>An Android signing key the user added (ADR 0114): a keystore file and the key's alias in it.</summary>
public sealed record AndroidKeystore(string Id, string Name, string Path, string Alias);

/// <summary>A Windows code-signing certificate file (<c>.pfx</c>) the user added for sideloaded MSIX packages (ADR 0114).</summary>
public sealed record CertificateFile(string Id, string Name, string Path);

/// <summary>The user's signing identities' file (<c>&lt;data&gt;/signing.json</c>): paths and names, never a password.</summary>
internal sealed class SigningIdentityFile
{
    public List<AndroidKeystore> Android { get; set; } = [];

    public List<CertificateFile> Windows { get; set; } = [];
}

/// <summary>
/// The user's signing identities (ADR 0114), kept per user, never in a repository: Android keystores and Windows
/// <c>.pfx</c> files in <c>signing.json</c> in the user-data folder, their passwords in the OS credential store
/// (<see cref="SecretStorage"/>, under <see cref="SecretOwner"/>). Apple's identities are the keychain's and Windows'
/// store certificates are the certificate store's: they aren't kept here.
/// </summary>
public sealed class SigningIdentities(string file, SecretStorage secrets)
{
    /// <summary>The owner the passwords are kept under in <see cref="SecretStorage"/>.</summary>
    public const string SecretOwner = "codalus.signing";

    private readonly object _gate = new();

    public string File => file;

    public IReadOnlyList<AndroidKeystore> Keystores()
    {
        lock (_gate)
        {
            return Load().Android;
        }
    }

    public IReadOnlyList<CertificateFile> Certificates()
    {
        lock (_gate)
        {
            return Load().Windows;
        }
    }

    /// <summary>Adds a keystore and keeps its passwords (the key's defaults to the store's, as a PKCS12 keystore has it).</summary>
    public async Task<AndroidKeystore> AddKeystoreAsync(string name, string path, string alias, string storePassword, string? keyPassword, CancellationToken cancellationToken)
    {
        var keystore = new AndroidKeystore(NewId(), name, System.IO.Path.GetFullPath(path), alias);
        await secrets.StoreAsync(SecretOwner, keystore.Id + ".storePassword", storePassword, cancellationToken).ConfigureAwait(false);
        await secrets.StoreAsync(SecretOwner, keystore.Id + ".keyPassword", keyPassword ?? storePassword, cancellationToken).ConfigureAwait(false);
        Change(f => f.Android.Add(keystore));
        return keystore;
    }

    public async Task<CertificateFile> AddCertificateAsync(string name, string path, string password, CancellationToken cancellationToken)
    {
        var certificate = new CertificateFile(NewId(), name, System.IO.Path.GetFullPath(path));
        await secrets.StoreAsync(SecretOwner, certificate.Id + ".password", password, cancellationToken).ConfigureAwait(false);
        Change(f => f.Windows.Add(certificate));
        return certificate;
    }

    /// <summary>Forgets an identity and its passwords; the keystore or certificate file stays where it is.</summary>
    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        Change(f =>
        {
            f.Android.RemoveAll(k => k.Id == id);
            f.Windows.RemoveAll(c => c.Id == id);
        });
        foreach (var key in new[] { ".storePassword", ".keyPassword", ".password" })
        {
            await secrets.DeleteAsync(SecretOwner, id + key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A keystore's store and key passwords, or nulls when the credential store doesn't have them (any more).</summary>
    public async Task<(string? Store, string? Key)> KeystorePasswordsAsync(AndroidKeystore keystore, CancellationToken cancellationToken)
    {
        var store = await secrets.GetAsync(SecretOwner, keystore.Id + ".storePassword", cancellationToken).ConfigureAwait(false);
        var key = await secrets.GetAsync(SecretOwner, keystore.Id + ".keyPassword", cancellationToken).ConfigureAwait(false);
        return (store, key ?? store);
    }

    public Task<string?> CertificatePasswordAsync(CertificateFile certificate, CancellationToken cancellationToken) =>
        secrets.GetAsync(SecretOwner, certificate.Id + ".password", cancellationToken);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private void Change(Action<SigningIdentityFile> change)
    {
        lock (_gate)
        {
            var identities = Load();
            change(identities);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, JsonSerializer.Serialize(identities, ArchivesJsonContext.Default.SigningIdentityFile));
        }
    }

    private SigningIdentityFile Load()
    {
        try
        {
            return System.IO.File.Exists(file)
                ? JsonSerializer.Deserialize(System.IO.File.ReadAllText(file), ArchivesJsonContext.Default.SigningIdentityFile) ?? new SigningIdentityFile()
                : new SigningIdentityFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SigningIdentityFile();
        }
    }
}
