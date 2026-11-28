using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Codalus.Debugging.Host;

/// <summary>
/// The process's standard input, output and error as their file descriptors (ADR 0115). On Android .NET's console is
/// the system log (<c>logcat</c>) and has no input, while the debugger there is started by adb's shell, whose
/// standard streams are the IDE's DAP connection and its log.
/// </summary>
internal static class StandardStreams
{
    public static (Stream Input, Stream Output) Open()
    {
        var input = new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, bufferSize: 1);
        var output = new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, bufferSize: 1);
        var error = new StreamWriter(new FileStream(new SafeFileHandle(2, ownsHandle: false), FileAccess.Write, bufferSize: 1), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetError(error);
        return (input, output);
    }
}
