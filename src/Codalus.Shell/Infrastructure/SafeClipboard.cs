using System.Runtime.InteropServices;
using Avalonia.Input.Platform;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The clipboard while another program may hold it: on Windows <c>OpenClipboard</c> fails until that program lets go
/// (<c>CLIPBRD_E_CANT_OPEN</c>), and Avalonia throws. Copy and paste retry briefly, then say so on the status bar
/// (<see cref="Busy"/>) instead of failing with an error or an unobserved exception.
/// </summary>
internal static class SafeClipboard
{
    /// <summary><c>CLIPBRD_E_CANT_OPEN</c>.</summary>
    private const int CantOpen = unchecked((int)0x800401D0);

    private static readonly TimeSpan[] s_retries = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150)];

    /// <summary>The clipboard stayed busy: the shell shows it on the status bar.</summary>
    public static event Action? Busy;

    public static Task<bool> TrySetTextAsync(IClipboard? clipboard, string text) =>
        clipboard is null ? Task.FromResult(false) : TryAsync(async () =>
        {
            await ClipboardExtensions.SetTextAsync(clipboard, text).ConfigureAwait(true);
            return true;
        });

    public static Task<string?> TryGetTextAsync(IClipboard? clipboard) =>
        clipboard is null ? Task.FromResult<string?>(null) : TryAsync(() => ClipboardExtensions.TryGetTextAsync(clipboard));

    /// <summary>Whether <paramref name="exception"/> is the clipboard being held by another program.</summary>
    public static bool IsBusy(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsBusy),
        ExternalException external => external.HResult == CantOpen || external.StackTrace?.Contains("Clipboard", StringComparison.Ordinal) == true,
        _ => false,
    };

    /// <summary>A clipboard error from code we can't wrap (the editor's own Copy): shown like ours.</summary>
    public static void ReportBusy() => Busy?.Invoke();

    internal static async Task<T?> TryAsync<T>(Func<Task<T>> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(true);
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                if (attempt == s_retries.Length)
                {
                    Busy?.Invoke();
                    return default;
                }

                await Task.Delay(s_retries[attempt]).ConfigureAwait(true);
            }
        }
    }
}
