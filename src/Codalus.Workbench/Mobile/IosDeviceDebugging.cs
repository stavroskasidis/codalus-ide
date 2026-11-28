using System.Globalization;
using Codalus.Debugging;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// Debugging a CoreCLR app on an iPhone or iPad (ADR 0117). iOS starts no other process for an app, so the Codalus
/// debugger goes into the app: F5's build adds the in-app debugger (<c>CodalusDebugger.framework</c>, the debugger
/// compiled ahead of time for iOS) to the app's bundle, and the launch's environment has CoreCLR load it as a profiler
/// and tells it the port to serve DAP on. The IDE reaches that port over the device's tunnel (devicectl's address).
/// <para>
/// F5's build goes to folders of its own (<c>obj/&lt;configuration&gt;/codalus-device/</c>,
/// <c>bin/&lt;configuration&gt;/codalus-device/</c>), so the app the user's own builds and Ctrl+F5 make stays as it was, and
/// nothing is written into the project: the framework comes in through a targets file of the IDE's
/// (<c>CustomAfterMicrosoftCommonTargets</c>).
/// </para>
/// </summary>
public static class IosDeviceDebugging
{
    /// <summary>The platform of the in-app debugger's build: iPhones and iPads (arm64).</summary>
    public const string Platform = "ios-arm64";

    /// <summary>The variable naming a folder with the framework in it (a source checkout's or a test's own build).</summary>
    public const string FrameworkVariable = "CODALUS_DEBUGGER_IOS_ARM64";

    /// <summary>The folder name of F5's build, under the project's <c>obj/&lt;configuration&gt;</c> and <c>bin/&lt;configuration&gt;</c>.</summary>
    public const string OutputFolder = "codalus-device";

    /// <summary>The archive a published install for macOS ships the framework in, next to the shell: <c>remote/ios-arm64/</c>.</summary>
    public const string Archive = "debugger.tar.gz";

    /// <summary>The archive's build, next to it: the folder it is unpacked into is named after it.</summary>
    public const string ArchiveId = "debugger.id";

    /// <summary>
    /// The in-app debugger's framework on this Mac, or null when there is none: the variable's folder, the archive next
    /// to the shell (a published install for macOS: unpacked into <paramref name="cacheFolder"/> once per build of it),
    /// or a source checkout's build in <c>.work/codalus-debugger-ios-arm64</c> (<c>eng/get-codalus-debugger.sh ios-arm64</c>).
    /// </summary>
    public static string? Framework(string cacheFolder, string? baseDirectory = null, Func<string, string?>? environment = null)
    {
        environment ??= System.Environment.GetEnvironmentVariable;
        baseDirectory ??= AppContext.BaseDirectory;
        static string? In(string folder)
        {
            var framework = Path.Combine(folder, InAppDebugger.FrameworkName + ".framework");
            return File.Exists(Path.Combine(framework, InAppDebugger.FrameworkName)) ? framework : null;
        }

        if (environment(FrameworkVariable) is { Length: > 0 } given)
        {
            return In(given);
        }

        var shipped = Path.Combine(baseDirectory, "remote", Platform);
        if (File.Exists(Path.Combine(shipped, Archive)) && File.Exists(Path.Combine(shipped, ArchiveId)))
        {
            var id = File.ReadAllText(Path.Combine(shipped, ArchiveId)).Trim();
            var unpacked = Path.Combine(cacheFolder, id);
            if (In(unpacked) is { } cached)
            {
                return cached;
            }

            Unpack(Path.Combine(shipped, Archive), unpacked);
            return In(unpacked);
        }

        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Codalus.slnx")))
            {
                return In(Path.Combine(directory.FullName, ".work", "codalus-debugger-" + Platform));
            }
        }

        return null;
    }

    /// <summary>Unpacks the archive into <paramref name="folder"/>, whole or not at all (another window may be doing the same).</summary>
    private static void Unpack(string archive, string folder)
    {
        var staging = folder + "." + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            Directory.CreateDirectory(staging);
            using (var file = File.OpenRead(archive))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            {
                System.Formats.Tar.TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
            }

            try
            {
                Directory.Move(staging, folder);
            }
            catch (IOException) when (Directory.Exists(folder))
            {
                // Another window was first.
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The targets file F5's build imports: the framework, as the iOS SDK takes a native framework into the app.</summary>
    public const string TargetsContent = """
        <Project>
          <!-- Codalus IDE (ADR 0117): the in-app debugger in F5's build for an iPhone or iPad. Written by the IDE; nothing in the project names it. -->
          <ItemGroup Condition="'$(CodalusInAppDebuggerFramework)' != ''">
            <NativeReference Include="$(CodalusInAppDebuggerFramework)" Kind="Framework" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>Writes the targets file into <paramref name="folder"/> (when it isn't there as it should be) and returns its path.</summary>
    public static string WriteTargets(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "codalus-in-app-debugger.targets");
        if (!File.Exists(path) || File.ReadAllText(path) != TargetsContent)
        {
            File.WriteAllText(path, TargetsContent);
        }

        return path;
    }

    /// <summary>
    /// The arguments F5 adds to the device's run (<see cref="MobileTargets.RunArguments"/>): the build's own folders,
    /// the targets file and the framework, and the app's environment for the in-app debugger through mlaunch's
    /// <c>--setenv</c> (one <c>;</c>-separated property, in quotes so MSBuild's command line reads it whole).
    /// </summary>
    /// <param name="trace">The session's DAP traffic in the app's console (<see cref="InAppDebugger.TraceVariable"/>).</param>
    public static IReadOnlyList<string> RunArguments(string framework, string targets, int port, string? configuration, bool trace = false)
    {
        var config = string.IsNullOrEmpty(configuration) ? "Debug" : configuration;
        var environment = string.Join(';', InAppDebugger.Environment(port, trace: trace).Select(v => "--setenv=" + v.Key + "=" + v.Value));
        return
        [
            $"-p:IntermediateOutputPath=obj/{config}/{OutputFolder}/",
            $"-p:OutputPath=bin/{config}/{OutputFolder}/",
            "-p:CustomAfterMicrosoftCommonTargets=" + targets,
            "-p:CodalusInAppDebuggerFramework=" + framework,
            $"-p:MlaunchAdditionalArgumentsProperty=\"{environment}\"",
        ];
    }

    /// <summary>A port for the app to serve on: the dynamic range, by chance (the app is alone on its device's ports).</summary>
    public static int PickPort(Random? random = null) => (random ?? Random.Shared).Next(49152, 65536);

    /// <summary>The port, as the app's console says it waits (<c>[codalus-debugger] waiting for the IDE on port N</c>).</summary>
    public static string WaitingLine(int port) => "waiting for the IDE on port " + port.ToString(CultureInfo.InvariantCulture);
}
