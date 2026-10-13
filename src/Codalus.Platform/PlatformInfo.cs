namespace Codalus.Platform;

public enum PlatformKind
{
    Windows,
    MacOS,
    Linux,
}

public static class PlatformInfo
{
    public static PlatformKind Current { get; } =
        OperatingSystem.IsWindows() ? PlatformKind.Windows
        : OperatingSystem.IsMacOS() ? PlatformKind.MacOS
        : PlatformKind.Linux;
}
