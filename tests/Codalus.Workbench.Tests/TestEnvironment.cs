using System.Runtime.CompilerServices;

namespace Codalus.Workbench.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// The shells these tests start keep no history in the real home: an interactive <c>/bin/sh</c> (bash on macOS)
    /// wrote the pseudo-terminal test's commands to <c>~/.bash_history</c> (the Mac's finding, M21a).
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable("HISTFILE", "/dev/null");
}
