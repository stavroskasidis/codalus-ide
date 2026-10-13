using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace Codalus.Workbench.Web;

/// <summary>The state of the ASP.NET Core HTTPS development certificate.</summary>
public enum DevCertificateState
{
    /// <summary>There is no valid development certificate.</summary>
    Missing,

    /// <summary>It exists but isn't trusted by this user.</summary>
    Untrusted,

    Trusted,

    /// <summary>The check couldn't run (no SDK found).</summary>
    Unknown,
}

/// <summary>
/// ASP.NET Core development chores (M15a), through the SDK's own commands: the HTTPS development certificate
/// (<c>dotnet dev-certs https</c>) and user secrets (<c>dotnet user-secrets init</c>, and the <c>secrets.json</c> file).
/// <paramref name="dotnetRoot"/> is the folder of the <c>dotnet</c> the solution builds with (null: the one on PATH).
/// </summary>
public sealed class WebDevelopmentService(Func<string?> dotnetRoot)
{
    private string Dotnet =>
        dotnetRoot() is { } root && File.Exists(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
            : "dotnet";

    /// <summary>Checks the development certificate: <c>--check --trust</c>, then <c>--check</c>.</summary>
    public async Task<DevCertificateState> CheckCertificateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if ((await RunAsync(["dev-certs", "https", "--check", "--trust"], null, cancellationToken).ConfigureAwait(false)).ExitCode == 0)
            {
                return DevCertificateState.Trusted;
            }

            return (await RunAsync(["dev-certs", "https", "--check"], null, cancellationToken).ConfigureAwait(false)).ExitCode == 0
                ? DevCertificateState.Untrusted
                : DevCertificateState.Missing;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DevCertificateState.Unknown;
        }
    }

    /// <summary>
    /// Creates the certificate if needed and trusts it (<c>dotnet dev-certs https --trust</c>). Windows and macOS ask
    /// the user to confirm; on Linux the SDK trusts it for .NET, and for browsers where it can (see the guidance).
    /// </summary>
    public Task<(int ExitCode, string Output)> TrustCertificateAsync(CancellationToken cancellationToken) =>
        RunAsync(["dev-certs", "https", "--trust"], null, cancellationToken);

    /// <summary>Linux trust stores differ by distribution; what to do when <c>--trust</c> isn't enough.</summary>
    public const string LinuxTrustGuidance =
        "On Linux, 'dotnet dev-certs https --trust' trusts the certificate for .NET and, where it can, for browsers (NSS databases of Firefox and Chromium) and OpenSSL (SSL_CERT_DIR). " +
        "If a browser still warns, import the certificate exported with 'dotnet dev-certs https --export-path ~/aspnetcore-dev.crt --format PEM' into that browser, " +
        "or add it to the system store (for example, copy it to /usr/local/share/ca-certificates/ and run 'sudo update-ca-certificates' on Debian and Ubuntu).";

    /// <summary>The project's <c>UserSecretsId</c>, or null.</summary>
    public static string? UserSecretsId(string projectPath)
    {
        try
        {
            return XDocument.Load(projectPath).Descendants().FirstOrDefault(e => e.Name.LocalName == "UserSecretsId")?.Value.Trim() is { Length: > 0 } id ? id : null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where the SDK keeps a project's user secrets.</summary>
    public static string SecretsPath(string id) => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", id, "secrets.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets", id, "secrets.json");

    /// <summary>
    /// The project's <c>secrets.json</c>, created (with <c>{}</c>) if needed. A project without a <c>UserSecretsId</c>
    /// gets one first through <c>dotnet user-secrets init</c>, which edits the project file.
    /// </summary>
    public async Task<string> SecretsFileAsync(string projectPath, CancellationToken cancellationToken)
    {
        var file = SecretsPath(await InitUserSecretsAsync(projectPath, cancellationToken).ConfigureAwait(false));
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{\n}\n", cancellationToken).ConfigureAwait(false);
        }

        return file;
    }

    /// <summary>The project's <c>UserSecretsId</c>, added with <c>dotnet user-secrets init</c> when it has none.</summary>
    public async Task<string> InitUserSecretsAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (UserSecretsId(projectPath) is { } existing)
        {
            return existing;
        }

        var (exitCode, output) = await RunAsync(["user-secrets", "init", "--project", projectPath], Path.GetDirectoryName(projectPath), cancellationToken).ConfigureAwait(false);
        return UserSecretsId(projectPath) ?? throw new InvalidOperationException($"dotnet user-secrets init failed (exit code {exitCode}): {output.Trim()}");
    }

    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        output.Append(await stdout.ConfigureAwait(false)).Append(await stderr.ConfigureAwait(false));
        return (process.ExitCode, output.ToString());
    }
}
