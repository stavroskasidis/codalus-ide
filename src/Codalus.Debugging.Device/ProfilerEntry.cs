using System.Runtime.InteropServices;

namespace Codalus.Debugging.Device;

/// <summary>
/// The library's way in (ADR 0117): CoreCLR loads it as a profiler (<c>DOTNET_PROFILER_PATH</c>) while it starts, asks
/// its <c>DllGetClassObject</c> for a class factory, makes the profiler with it, and calls the profiler's
/// <c>Initialize</c> before any managed code runs. That is where the in-app debugger starts (<see cref="InAppServer"/>)
/// and holds the app for the IDE. It profiles nothing: its event mask stays empty, and every other callback answers
/// S_OK. The COM objects are plain vtables of unmanaged function pointers: the runtime calls them on its own threads
/// before ours has a COM wrapper of any kind.
/// </summary>
public static unsafe class ProfilerEntry
{
    private static readonly Guid s_unknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid s_callback = new("176FBED1-A55C-4796-98CA-A9DA0EF883E7");
    private static readonly Guid s_callback2 = new("8A8CC829-CCF2-49FE-BBAE-0F022228071A");

    /// <summary>ICorProfilerCallback's methods, through ICorProfilerCallback2 and the later ones the runtime may call anyway.</summary>
    private const int CallbackSlots = 128;

    private const int NoInterface = unchecked((int)0x80004002); // E_NOINTERFACE
    private const int ClassNotAvailable = unchecked((int)0x80040111); // CLASS_E_CLASSNOTAVAILABLE

    private static nint s_factory;
    private static nint s_profiler;

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* classId, Guid* interfaceId, nint* result)
    {
        if (*classId != new Guid(InAppDebugger.ProfilerClassId))
        {
            *result = 0;
            return ClassNotAvailable;
        }

        if (s_factory == 0)
        {
            var vtable = (nint*)NativeMemory.Alloc((nuint)(5 * sizeof(nint)));
            vtable[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&FactoryQueryInterface;
            vtable[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
            vtable[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
            vtable[3] = (nint)(delegate* unmanaged<nint, nint, Guid*, nint*, int>)&CreateInstance;
            vtable[4] = (nint)(delegate* unmanaged<nint, int, int>)&LockServer;
            s_factory = NewObject(vtable);
        }

        *result = s_factory;
        return 0;
    }

    private static nint NewObject(nint* vtable)
    {
        var instance = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        instance[0] = (nint)vtable;
        return (nint)instance;
    }

    [UnmanagedCallersOnly]
    private static int FactoryQueryInterface(nint self, Guid* interfaceId, nint* result)
    {
        *result = self;
        return 0;
    }

    // The objects live as long as the process: the counts don't matter.
    [UnmanagedCallersOnly]
    private static uint AddRef(nint self) => 1;

    [UnmanagedCallersOnly]
    private static uint Release(nint self) => 1;

    [UnmanagedCallersOnly]
    private static int LockServer(nint self, int lockServer) => 0;

    [UnmanagedCallersOnly]
    private static int CreateInstance(nint self, nint outer, Guid* interfaceId, nint* result)
    {
        if (s_profiler == 0)
        {
            var vtable = (nint*)NativeMemory.Alloc((nuint)(CallbackSlots * sizeof(nint)));
            for (var i = 0; i < CallbackSlots; i++)
            {
                vtable[i] = (nint)(delegate* unmanaged<nint, int>)&Ignored;
            }

            vtable[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&ProfilerQueryInterface;
            vtable[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
            vtable[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
            vtable[3] = (nint)(delegate* unmanaged<nint, nint, int>)&Initialize;
            s_profiler = NewObject(vtable);
        }

        *result = s_profiler;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int ProfilerQueryInterface(nint self, Guid* interfaceId, nint* result)
    {
        var id = *interfaceId;
        if (id == s_unknown || id == s_callback || id == s_callback2)
        {
            *result = self;
            return 0;
        }

        *result = 0;
        return NoInterface;
    }

    /// <summary>Every callback but <c>Initialize</c>: nothing to do (the event mask asks for none of them).</summary>
    [UnmanagedCallersOnly]
    private static int Ignored(nint self) => 0;

    /// <summary>ICorProfilerCallback::Initialize, on the runtime's starting thread, before any managed code: the debugger starts.</summary>
    [UnmanagedCallersOnly]
    private static int Initialize(nint self, nint profilerInfo)
    {
        try
        {
            InAppServer.Start();
        }
        catch (Exception ex)
        {
            // The app runs without the debugger rather than not at all.
            InAppServer.Log("didn't start: " + ex);
        }

        return 0;
    }
}
