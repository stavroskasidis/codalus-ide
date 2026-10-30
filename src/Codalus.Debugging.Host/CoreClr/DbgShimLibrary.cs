using System.Runtime.InteropServices;
using ClrDebug;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// dbgshim, from the <c>Microsoft.Diagnostics.DbgShim.&lt;rid&gt;</c> package (M54, ADR 0086): next to the host in a
/// package, under <c>runtimes/&lt;rid&gt;/native</c> in a build, which the deps.json resolves.
/// </summary>
internal static class DbgShimLibrary
{
    private static readonly Lazy<DbgShim> s_shim = new(Load);

    public static DbgShim Shim => s_shim.Value;

    private static DbgShim Load()
    {
        if (!NativeLibrary.TryLoad("dbgshim", typeof(DbgShimLibrary).Assembly, DllImportSearchPath.AssemblyDirectory, out var handle))
        {
            throw new ClrException($"The debugger's dbgshim library isn't installed next to codalus-debughost ({RuntimeInformation.RuntimeIdentifier}).");
        }

        return new DbgShim(handle);
    }
}
