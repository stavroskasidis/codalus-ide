using System.Diagnostics;
using System.Runtime.InteropServices;
using DaedalusStudio.Platform.Credentials;
using DaedalusStudio.Workbench.Secrets;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// M13b (ADR 0031): the OS credential stores and the plugins' secret storage. The OS-store test uses a service name of
/// its own and deletes what it wrote. On Linux it runs against a private D-Bus session and GNOME Keyring in a temporary
/// folder (never the user's keyring), and is skipped, with the reason, where those aren't installed.
/// </summary>
public sealed partial class CredentialStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_in_memory_store_keeps_secrets_for_the_process()
    {
        var store = CredentialStore.Create(name => name == CredentialStore.EnvironmentVariable ? "memory" : null);
        Assert.IsType<InMemoryCredentialStore>(store);
        Assert.False(store.IsPersistent);
        await RoundTripAsync(store, "Daedalus Studio Tests " + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task The_OS_store_stores_replaces_and_deletes_a_secret()
    {
        using var session = OperatingSystem.IsLinux() ? PrivateSecretService.Start() : null;
        var store = CredentialStore.Create(_ => null);
        Assert.True(store.IsPersistent);
        var service = "Daedalus Studio Tests " + Guid.NewGuid().ToString("N");
        try
        {
            await RoundTripAsync(store, service);
        }
        catch (CredentialStoreUnavailableException ex) when (!OperatingSystem.IsWindows())
        {
            Assert.Skip($"The {store.Name} isn't usable here: {ex.Message}");
        }
    }

    private static async Task RoundTripAsync(ICredentialStore store, string service)
    {
        const string Account = "daedalus.github/github.com";
        try
        {
            Assert.Null(await store.GetAsync(service, Account, Ct));
            await store.SetAsync(service, Account, "first-secret-ü", Ct);
            Assert.Equal("first-secret-ü", await store.GetAsync(service, Account, Ct));

            // Replacing it, with a secret longer than Credential Manager's 2,560 bytes (split into parts there).
            var large = string.Concat(Enumerable.Repeat("0123456789abcdef", 400));
            await store.SetAsync(service, Account, large, Ct);
            Assert.Equal(large, await store.GetAsync(service, Account, Ct));

            // And back to a short one: the parts go away.
            await store.SetAsync(service, Account, "short", Ct);
            Assert.Equal("short", await store.GetAsync(service, Account, Ct));
            Assert.Null(await store.GetAsync(service, "another-account", Ct));

            Assert.True(await store.DeleteAsync(service, Account, Ct));
            Assert.Null(await store.GetAsync(service, Account, Ct));
            Assert.False(await store.DeleteAsync(service, Account, Ct));
        }
        finally
        {
            try
            {
                await store.DeleteAsync(service, Account, CancellationToken.None);
            }
            catch (CredentialStoreUnavailableException)
            {
            }
        }
    }

    [Fact]
    public async Task Plugin_secrets_are_scoped_per_plugin_and_fall_back_to_memory_once()
    {
        var store = new FailingStore();
        var storage = new SecretStorage(store);
        var reasons = new List<string>();
        storage.FellBack += (_, reason) => reasons.Add(reason);
        Assert.True(storage.IsPersistent);

        await storage.StoreAsync("daedalus.github", "github.com", "token-1", Ct);
        await storage.StoreAsync("daedalus.gitlab", "github.com", "token-2", Ct);
        Assert.Equal("token-1", await storage.GetAsync("daedalus.github", "github.com", Ct));
        Assert.Equal("token-2", await storage.GetAsync("daedalus.gitlab", "github.com", Ct));
        Assert.True(await storage.DeleteAsync("daedalus.github", "github.com", Ct));
        Assert.Null(await storage.GetAsync("daedalus.github", "github.com", Ct));

        Assert.Equal(["No Secret Service here."], reasons);
        Assert.False(storage.IsPersistent);
        Assert.Contains("memory", storage.StoreName, StringComparison.Ordinal);
        Assert.Equal(1, store.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.StoreAsync("daedalus.github", " ", "x", Ct));
    }

    [Fact]
    public async Task Plugin_secrets_use_the_service_and_the_plugin_scoped_account()
    {
        var store = new InMemoryCredentialStore();
        var storage = new SecretStorage(store);
        await storage.StoreAsync("daedalus.azuredevops", "session", "value", Ct);
        Assert.Equal("value", await store.GetAsync(SecretStorage.Service, "daedalus.azuredevops/session", Ct));
    }

    private sealed class FailingStore : ICredentialStore
    {
        public int Calls { get; private set; }

        public string Name => "failing";

        public bool IsPersistent => true;

        public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default) => Fail<string?>();

        public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default) => Fail<object?>();

        public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) => Fail<bool>();

        private Task<T> Fail<T>()
        {
            Calls++;
            throw new CredentialStoreUnavailableException("No Secret Service here.");
        }
    }

    /// <summary>
    /// A D-Bus session bus and GNOME Keyring (secrets only) of this test, with the keyring, runtime folder and password in
    /// a temporary folder, so the user's keyring is never touched. libsecret reads the bus address from the process's
    /// native environment, so it's set there (before the first libsecret call of this process).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private sealed partial class PrivateSecretService : IDisposable
    {
        private readonly string _folder;
        private readonly List<int> _pids = [];

        private PrivateSecretService(string folder) => _folder = folder;

        public static PrivateSecretService? Start()
        {
            if (Which("dbus-daemon") is not { } dbus || Which("gnome-keyring-daemon") is not { } keyring)
            {
                Assert.Skip("The Secret Service test needs dbus-daemon and gnome-keyring-daemon (packages dbus and gnome-keyring).");
                return null;
            }

            var folder = Path.Combine(Path.GetTempPath(), "daedalus-keyring-" + Guid.NewGuid().ToString("N")[..8]);
            var service = new PrivateSecretService(folder);
            try
            {
                var runtime = Directory.CreateDirectory(Path.Combine(folder, "run"));
                File.SetUnixFileMode(runtime.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.CreateDirectory(Path.Combine(folder, "data"));
                var (busOutput, _) = Run(dbus, ["--session", "--fork", "--print-address=1", "--print-pid=1"], null, null);
                var lines = busOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var address = lines[0];
                service._pids.Add(int.Parse(lines[1], System.Globalization.CultureInfo.InvariantCulture));
                var environment = new Dictionary<string, string>
                {
                    ["DBUS_SESSION_BUS_ADDRESS"] = address,
                    ["XDG_RUNTIME_DIR"] = runtime.FullName,
                    ["XDG_DATA_HOME"] = Path.Combine(folder, "data"),
                };
                _ = Run(keyring, ["--unlock", "--components=secrets", "--daemonize"], "daedalus-test\n", environment);
                _ = setenv("DBUS_SESSION_BUS_ADDRESS", address, 1);
                return service;
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }

        private static string? Which(string program) =>
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, program))
                .FirstOrDefault(File.Exists);

        private static (string Output, string Error) Run(string program, string[] arguments, string? input, Dictionary<string, string>? environment)
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            foreach (var (name, value) in environment ?? [])
            {
                start.Environment[name] = value;
            }

            using var process = Process.Start(start)!;
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), $"{program} didn't exit.");
            return (output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }

        public void Dispose()
        {
            // The keyring daemon doesn't say its process id when it daemonizes: find it by its environment.
            foreach (var process in Process.GetProcessesByName("gnome-keyring-d"))
            {
                try
                {
                    if (File.ReadAllText($"/proc/{process.Id}/environ").Contains(_folder, StringComparison.Ordinal))
                    {
                        _pids.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (var pid in _pids)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int setenv(string name, string value, int overwrite);
    }
}
