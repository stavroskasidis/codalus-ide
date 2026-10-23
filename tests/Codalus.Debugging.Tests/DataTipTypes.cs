using System.Collections.ObjectModel;

#pragma warning disable CA1050, CA1823, CA2211, CS0414, IDE0051, IDE0052 // the Debuggee's types as they are (samples/Debugging/Debuggee/DataTip.cs)

namespace Debuggee;

/// <summary>
/// The Debuggee's <c>Inventory</c> (samples/Debugging/Debuggee/DataTip.cs), the same members under the same name: the
/// grouping tests read its metadata from the test assembly as the companion reads the Debuggee's. Keep the two alike.
/// </summary>
public sealed class Inventory : Collection<string>
{
    public static string Summaries = "Freezing, Bracing";

    private readonly string logger = "inventory log";

    public int Size => Count;

    public string Session => throw new InvalidOperationException("Session has not been configured for this application or request.");

    public override string ToString() => logger;
}
