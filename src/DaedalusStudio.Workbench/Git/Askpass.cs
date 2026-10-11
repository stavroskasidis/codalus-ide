using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DaedalusStudio.Workbench.Git;

/// <summary>What git or ssh asks through askpass (ADR 0031): <c>Username for 'https://…': </c>, a password, a key's passphrase, a host key.</summary>
public sealed record AskpassPrompt(string Text)
{
    /// <summary>A yes/no question (ssh's unknown host key).</summary>
    public bool IsConfirmation => Text.Contains("(yes/no", StringComparison.OrdinalIgnoreCase);

    /// <summary>The answer is a secret: shown masked, never logged.</summary>
    public bool IsSecret => !IsConfirmation
        && (Text.Contains("password", StringComparison.OrdinalIgnoreCase)
            || Text.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
            || Text.Contains("PIN", StringComparison.Ordinal)
            || Text.Contains("token", StringComparison.OrdinalIgnoreCase));

    /// <summary>A title for the prompt's dialog.</summary>
    public string Title =>
        IsConfirmation ? "SSH: Confirm the Host"
        : Text.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ? "SSH Key Passphrase"
        : Text.StartsWith("Username", StringComparison.OrdinalIgnoreCase) ? "Git: Sign In"
        : IsSecret ? "Git: Password"
        : "Git";
}

/// <summary>
/// The shell's end of askpass (ADR 0031). Git and ssh run <see cref="Program"/> (the <c>daedalus</c> executable) with the
/// prompt; it connects to this server's pipe (named in <see cref="Environment"/>, with a random token) and prints the
/// answer the user gave. Nothing is stored: the answer goes back to git, which hands it to the user's credential helper
/// if one is configured.
/// </summary>
public sealed class AskpassServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<AskpassPrompt, CancellationToken, Task<string?>> _answer;
    private readonly string _pipeName;
    private readonly string _token;

    /// <param name="program">The askpass program: the <c>daedalus</c> executable.</param>
    /// <param name="answer">Asks the user; null cancels (git then fails with its own message).</param>
    public AskpassServer(string program, Func<AskpassPrompt, CancellationToken, Task<string?>> answer)
    {
        _answer = answer;
        // Short: on macOS the pipe is a socket under TMPDIR, whose path has a length limit.
        _pipeName = "daedalus-ask-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Program = program;
        Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_ASKPASS"] = program,
            ["SSH_ASKPASS"] = program,
            ["SSH_ASKPASS_REQUIRE"] = "force",
            [AskpassClient.PipeVariable] = _pipeName,
            [AskpassClient.TokenVariable] = _token,
        };
        _ = Task.Run(ListenAsync);
    }

    public string Program { get; }

    /// <summary>The variables for git processes (<see cref="GitService.AskpassEnvironment"/>).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Prompts answered or cancelled, for tests and the log (never the answers).</summary>
    public event EventHandler<AskpassPrompt>? Asked;

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                _ = ServeAsync(pipe);
                pipe = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                await using (writer.ConfigureAwait(false))
                {
                    var token = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    var encoded = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (token is null || encoded is null
                        || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(_token)))
                    {
                        await writer.WriteLineAsync("0").ConfigureAwait(false);
                        return;
                    }

                    var prompt = new AskpassPrompt(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                    string? answer;
                    try
                    {
                        answer = await _answer(prompt, _stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Asked?.Invoke(this, prompt);
                    }

                    await writer.WriteLineAsync(answer is null ? "0" : "1 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(answer))).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away (git was cancelled), or the shell is closing.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary>
/// The program end of askpass: <c>daedalus</c> started by git or ssh with the askpass variables and the prompt as its
/// only argument (ADR 0031). It asks the running shell and prints the answer; exit code 1 means cancelled.
/// </summary>
public static class AskpassClient
{
    public const string PipeVariable = "DAEDALUS_ASKPASS_PIPE";
    public const string TokenVariable = "DAEDALUS_ASKPASS_TOKEN";

    /// <summary>Whether this process was started as askpass: the variables are set and the only argument is the prompt.</summary>
    public static bool IsInvocation(IReadOnlyList<string> args, Func<string, string?>? getEnvironment = null)
    {
        var environment = getEnvironment ?? System.Environment.GetEnvironmentVariable;
        return args.Count == 1 && environment(PipeVariable) is { Length: > 0 } && environment(TokenVariable) is { Length: > 0 };
    }

    /// <summary>Asks the shell named by the environment; writes the answer and a newline to <paramref name="output"/>.</summary>
    public static int Run(string prompt, TextWriter output, Func<string, string?>? getEnvironment = null, TextWriter? error = null)
    {
        var environment = getEnvironment ?? System.Environment.GetEnvironmentVariable;
        if (environment(PipeVariable) is not { Length: > 0 } pipeName || environment(TokenVariable) is not { Length: > 0 } token)
        {
            return 1;
        }

        try
        {
            using var pipe = Connect(pipeName, TimeSpan.FromSeconds(5));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(token);
            writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt)));
            if (reader.ReadLine() is not { } line)
            {
                error?.WriteLine("daedalus askpass: the IDE closed the request without an answer.");
                return 1;
            }

            if (!line.StartsWith("1 ", StringComparison.Ordinal))
            {
                return 1; // declined, or a wrong token
            }

            output.Write(Encoding.UTF8.GetString(Convert.FromBase64String(line[2..])));
            output.Write('\n');
            output.Flush();
            return 0;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or FormatException)
        {
            error?.WriteLine($"daedalus askpass: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Connects to the IDE's pipe, trying again until <paramref name="budget"/> runs out: on Unix (a socket) a
    /// connection can be refused for a moment under load, before the server has seen the request.
    /// </summary>
    private static NamedPipeClientStream Connect(string pipeName, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            try
            {
                var left = deadline - DateTime.UtcNow;
                pipe.Connect(left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1));
                return pipe;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                pipe.Dispose();
                Thread.Sleep(50);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
    }
}
