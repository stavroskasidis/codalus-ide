using System.Globalization;
using System.Runtime.InteropServices;

namespace Daedalus.Projects.Host;

/// <summary>
/// Moves files and folders to the OS trash, so deleting from the Solution Explorer can be undone outside the IDE:
/// the Recycle Bin on Windows, <c>~/.Trash</c> on macOS, and the freedesktop.org trash on Linux. It never falls back
/// to a permanent delete; failures are reported instead.
/// </summary>
internal static partial class Trash
{
    public static void Move(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Nothing to delete.", path);
        }

        if (Environment.GetEnvironmentVariable("DAEDALUS_TRASH_DIR") is { Length: > 0 } testTrash)
        {
            // Tests: a throwaway folder instead of the user's real trash.
            MoveInto(testTrash, path);
        }
        else if (OperatingSystem.IsWindows())
        {
            RecycleOnWindows(path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            MoveInto(Path.Combine(Home(), ".Trash"), path);
        }
        else
        {
            MoveToFreedesktopTrash(path);
        }
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string MoveInto(string trashDirectory, string path)
    {
        Directory.CreateDirectory(trashDirectory);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trashDirectory, name);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
        {
            target = Path.Combine(trashDirectory, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        }

        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        return target;
    }

    /// <summary>The home trash of the freedesktop.org trash specification (files/ plus a .trashinfo record).</summary>
    private static void MoveToFreedesktopTrash(string path)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home(), ".local", "share");
        var trash = Path.Combine(dataHome, "Trash");
        var target = MoveInto(Path.Combine(trash, "files"), path);
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(info);
        File.WriteAllText(
            Path.Combine(info, Path.GetFileName(target) + ".trashinfo"),
            string.Create(CultureInfo.InvariantCulture, $"[Trash Info]\nPath={Uri.EscapeDataString(Path.GetFullPath(path)).Replace("%2F", "/", StringComparison.Ordinal)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n"));
    }

    private static void RecycleOnWindows(string path)
    {
        var operation = new ShFileOperation
        {
            Func = FoDelete,
            From = Path.GetFullPath(path) + "\0\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
        {
            throw new IOException($"Could not move {path} to the Recycle Bin (error {result}).");
        }
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public nint Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public nint NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
#pragma warning disable SYSLIB1054 // The struct needs runtime marshalling of strings; the host runs on the JIT.
    private static extern int SHFileOperation(ref ShFileOperation operation);
#pragma warning restore SYSLIB1054
}
