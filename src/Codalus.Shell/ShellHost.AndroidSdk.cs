using System.Diagnostics;
using System.Text;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// Changing the Android SDK (ADR 0104): installs, updates and uninstalls run where the SDK's folder lets them. In a
/// folder the user can write, the IDE runs the command and shows its output; in one only an administrator can change
/// (the classic IDE's SDK is under <c>C:\Program Files (x86)</c>), it runs the same command elevated on Windows, one UAC
/// prompt, following its output from a log; elsewhere it runs it under <c>sudo</c> in the terminal, where sudo asks.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The Android SDK's own tools (the SDK Manager, the Device Manager, the Device Log).</summary>
    public AndroidSdkService AndroidSdk { get; private set; } = null!;

    /// <summary>Runs a command elevated and returns its exit code, or null when the user declined (tests use a stand-in).</summary>
    internal Func<ToolCommand, Action<string>, CancellationToken, Task<int?>> RunElevated { get; set; } = RunElevatedAsync;

    /// <summary>Whether this process can change a folder (tests describe an SDK only an administrator can change).</summary>
    internal Func<string, bool> CanWriteSdk { get; set; } = Elevation.CanWrite;

    /// <summary>Runs a tool (tests use a stand-in that replays output).</summary>
    internal ToolProcessRunner RunTool { get; set; } = ToolProcess.RunAsync;

    /// <summary>The output channel of the Android SDK's installs.</summary>
    public Workbench.Output.OutputChannel AndroidSdkLog => Workbench.Output.GetOrCreate("codalus.android", Localizer.Get("msg.android.channel", "Android SDK"));

    /// <summary>The Android SDK's tools go through <paramref name="service"/> (tests give one on stand-in tools and folders).</summary>
    internal void UseAndroidSdkService(AndroidSdkService service) => AndroidSdk = service;

    private void InitializeAndroidSdk() =>
        AndroidSdk = new AndroidSdkService(Mobile, (command, onLine, cancellationToken) => RunTool(command, onLine, cancellationToken), Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Runs <paramref name="command"/>, which changes the SDK in <paramref name="sdk"/>: here when the folder is writable,
    /// else elevated (Windows) or under <c>sudo</c> in the terminal (macOS, Linux). Its output goes to the Android SDK
    /// channel. Returns its exit code, or null when the user declined the elevation.
    /// </summary>
    internal async Task<int?> RunSdkChangeAsync(ToolCommand command, string sdk, string summary)
    {
        var log = AndroidSdkLog;
        Workbench.Output.Show(log);
        log.AppendLine($"[{DateTime.Now:T}] {summary}");
        if (CanWriteSdk(sdk))
        {
            log.AppendLine("> " + command.Display);
            try
            {
                var code = await RunTool(command, log.AppendLine, _lifetime.Token).ConfigureAwait(true);
                log.AppendLine(Localizer.Format("msg.android.exited", "Exit code {0}.", code));
                return code;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                log.AppendLine(ex.Message);
                return -1;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        if (Workbench.Platform == PlatformKind.Windows)
        {
            log.AppendLine(Localizer.Format("msg.android.elevating", "{0} can only be changed by an administrator: running it elevated.", sdk));
            log.AppendLine("> " + command.Display);
            var code = await RunElevated(command, log.AppendLine, _lifetime.Token).ConfigureAwait(true);
            log.AppendLine(code is { } exit ? Localizer.Format("msg.android.exited", "Exit code {0}.", exit) : Localizer.Get("msg.android.declined", "Cancelled: the administrator's permission wasn't given."));
            return code;
        }

        var sudo = Elevation.SudoCommand(command);
        if (!await Dialogs.ConfirmAsync(
                Localizer.Get("msg.android.sudoTitle", "Run as Administrator"),
                Localizer.Format("msg.android.sudoMessage", "The Android SDK in {0} can only be changed by an administrator. This runs in the terminal, where sudo asks for your password:\n{1}", sdk, sudo),
                Localizer.Get("msg.setup.runInTerminal", "Run in Terminal")).ConfigureAwait(true))
        {
            return null;
        }

        log.AppendLine("> " + sudo);
        var terminal = await RunInstallInTerminalAsync(new InstallPlan("android-sdk", InstallKind.Terminal, summary, sudo)).ConfigureAwait(true);
        log.AppendLine(Localizer.Format("msg.android.exited", "Exit code {0}.", terminal));
        return terminal;
    }

    /// <summary>
    /// Runs <paramref name="command"/> elevated on Windows (UAC's <c>runas</c>) through a <c>cmd</c> script that writes its
    /// output to a log, followed here as it grows, and its exit code to a file. Null: the user declined.
    /// </summary>
    private static async Task<int?> RunElevatedAsync(ToolCommand command, Action<string> onLine, CancellationToken cancellationToken)
    {
        var folder = Directory.CreateTempSubdirectory("codalus-elevated-").FullName;
        var script = Path.Combine(folder, "run.cmd");
        var logPath = Path.Combine(folder, "output.log");
        var exitPath = Path.Combine(folder, "exit.txt");
        var inputPath = Path.Combine(folder, "input.txt");

        // The elevated process starts from the user's profile, not this process's environment: the tools' telemetry
        // opt-outs go in its script, with this process's value where it has one (an AVALONIA_TELEMETRY_OPTOUT=0 the user set).
        var environment = new Dictionary<string, string>(command.Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in ToolTelemetry.Variables)
        {
            environment.TryAdd(name, Environment.GetEnvironmentVariable(name) ?? "1");
        }

        command = command with { Environment = environment };
        try
        {
            if (command.Input is { } input)
            {
                await File.WriteAllTextAsync(inputPath, input.Replace("\n", "\r\n", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(script, Elevation.WindowsScript(command, logPath, exitPath, inputPath), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            Process? process;
            try
            {
                process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return null; // ERROR_CANCELLED: the user said no to UAC
            }

            if (process is null)
            {
                return -1;
            }

            using (process)
            {
                var decoder = Encoding.UTF8.GetDecoder();
                var pending = new StringBuilder();
                long position = 0;
                void Follow(bool final)
                {
                    if (!File.Exists(logPath))
                    {
                        return;
                    }

                    try
                    {
                        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        stream.Seek(position, SeekOrigin.Begin);
                        var bytes = new byte[8192];
                        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                        int read;
                        while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
                        {
                            position += read;
                            pending.Append(chars, 0, decoder.GetChars(bytes, 0, read, chars, 0));
                        }
                    }
                    catch (IOException)
                    {
                        return;
                    }

                    var text = pending.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
                    var end = final ? text.Length : text.LastIndexOf('\n') + 1;
                    foreach (var line in text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        // Progress bars rewrite their line with \r: the last state is the one to show.
                        onLine(line[(line.LastIndexOf('\r') + 1)..]);
                    }

                    pending.Clear().Append(text[end..]);
                }

                while (!process.HasExited)
                {
                    try
                    {
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The IDE is closing: the elevated install goes on by itself (it can't be ended from here).
                        return null;
                    }

                    Follow(final: false);
                }

                Follow(final: true);
                return File.Exists(exitPath) && int.TryParse((await File.ReadAllTextAsync(exitPath, CancellationToken.None).ConfigureAwait(false)).Trim(), out var code) ? code : process.ExitCode;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
