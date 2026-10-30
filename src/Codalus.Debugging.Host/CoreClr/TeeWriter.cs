using System.Globalization;
using System.Text;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// The session's messages to both the adapter's stderr (the IDE's Debug log) and its log file
/// (<see cref="CoreClrAdapter.LogVariable"/>, M54), which also gets the DAP traffic.
/// </summary>
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    /// <summary>A log file in <see cref="CoreClrAdapter.LogVariable"/>'s folder, written through; null when it isn't set.</summary>
    public static TextWriter? OpenLog()
    {
        if (Environment.GetEnvironmentVariable(CoreClrAdapter.LogVariable) is not { Length: > 0 } folder)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"clr-dap-{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.log");
            return Synchronized(new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The adapter's log couldn't be opened in {folder}: {ex.Message}");
            return null;
        }
    }

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }
}
