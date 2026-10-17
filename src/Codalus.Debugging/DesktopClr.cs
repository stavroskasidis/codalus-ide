using System.Reflection.PortableExecutable;
using System.Text;

namespace Codalus.Debugging;

/// <summary>
/// The desktop CLR (.NET Framework) as the debugger sees it (M43b, ADR 0076): which programs and processes run on it,
/// whether their bitness matches the debugger's, and the command line ICorDebug starts them with. The adapter itself
/// is <c>codalus-debughost --netfx-dap</c>.
/// </summary>
public static class DesktopClr
{
    /// <summary>The debug companion host's argument that runs the desktop-CLR debugger adapter over DAP on stdio.</summary>
    public const string AdapterArgument = "--netfx-dap";

    /// <summary>The desktop CLR 4 (.NET Framework 4.x): <c>clr.dll</c>.</summary>
    public const string Clr4 = "clr.dll";

    /// <summary>The desktop CLR 2 (.NET Framework 2.0 to 3.5): <c>mscorwks.dll</c>.</summary>
    public const string Clr2 = "mscorwks.dll";

    /// <summary>
    /// Whether <paramref name="path"/> is a managed executable: a PE image with a CLI header (an <c>.exe</c> built for
    /// .NET Framework). .NET Core and .NET 5+ programs start through a native apphost or <c>dotnet</c>, so a managed
    /// <c>.exe</c> is a desktop-CLR (or Mono) program.
    /// </summary>
    public static bool IsManagedExecutable(string path)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CorHeader is not null && !pe.PEHeaders.IsDll;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Why a managed executable can't be debugged by a debugger of this bitness (ICorDebug debugs processes of its own
    /// bitness): a 32-bit one (x86, or AnyCPU with Prefer32Bit) under a 64-bit debugger, or an x64 one under a 32-bit
    /// debugger; null when it can.
    /// </summary>
    public static string? BitnessProblem(string path, bool debugger64)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.CorHeader is not { } cor)
            {
                return null;
            }

            var requires32 = (cor.Flags & CorFlags.Requires32Bit) != 0 || (headers.CoffHeader.Machine == Machine.I386 && (cor.Flags & CorFlags.ILOnly) == 0);
            var prefers32 = (cor.Flags & CorFlags.Prefers32Bit) != 0;
            var name = Path.GetFileName(path);
            if (debugger64 && (requires32 || prefers32))
            {
                return $"{name} runs as a 32-bit process ({(prefers32 ? "AnyCPU with Prefer32Bit" : "x86")}), and the .NET Framework debugger here debugs 64-bit processes only. Set <Prefer32Bit>false</Prefer32Bit> (or PlatformTarget AnyCPU or x64) to debug it.";
            }

            if (!debugger64 && headers.PEHeader?.Magic == PEMagic.PE32Plus)
            {
                return $"{name} is a 64-bit program, and this debugger is 32-bit.";
            }

            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The desktop CLR a process has loaded (<see cref="Clr4"/> or <see cref="Clr2"/>), on Windows; null when it runs
    /// CoreCLR (<c>coreclr.dll</c> wins: a .NET process that also loaded the desktop CLR is debugged by netcoredbg), none,
    /// or its modules can't be read.
    /// </summary>
    public static string? LoadedRuntime(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string? found = null;
            foreach (System.Diagnostics.ProcessModule module in process.Modules)
            {
                var name = module.ModuleName;
                if (string.Equals(name, "coreclr.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (string.Equals(name, Clr4, StringComparison.OrdinalIgnoreCase) || (found is null && string.Equals(name, Clr2, StringComparison.OrdinalIgnoreCase)))
                {
                    found = name.ToLowerInvariant();
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether process <paramref name="processId"/> runs the desktop CLR (Attach to Process sends it to the desktop-CLR adapter).</summary>
    public static bool RunsDesktopClr(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return LoadedRuntime(process) is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A Windows command line for <paramref name="program"/> and its arguments, quoted as the C runtime parses them.</summary>
    public static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        Append(builder, program);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            Append(builder, argument);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2).Append('"');
    }
}
