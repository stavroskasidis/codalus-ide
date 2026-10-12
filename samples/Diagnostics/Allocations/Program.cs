// Allocations: allocation-heavy work (M18): many short-lived strings and arrays, and a few large ones, for the GC
// events in the Diagnostic Tools window and, later, allocation sampling. Runs for the seconds given (default 5; 0: until killed).
using System.Globalization;
using System.Text;

var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 5;
var until = seconds <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
Console.WriteLine($"Allocations {Environment.ProcessId} started");
long bytes = 0;
var keep = new List<byte[]>();
while (DateTime.UtcNow < until)
{
    var builder = new StringBuilder();
    for (var i = 0; i < 1_000; i++)
    {
        builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
    }

    bytes += builder.ToString().Length * sizeof(char);
    keep.Add(new byte[100_000]); // the large object heap
    if (keep.Count > 50)
    {
        keep.Clear();
    }
}

Console.WriteLine($"Allocations finished: {bytes} bytes of strings");
