namespace AsyncDebuggee;

/// <summary>
/// The async debugging fixture (M61, ADR 0100). Built twice from these files: AsyncDebuggee (state machines) and
/// AsyncDebuggee.RuntimeAsync (runtime async, .NET 11). An argument picks one scenario (steps, chain, throw, mixed,
/// race, held); none runs them all but held. Tests find lines by the markers.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var scenario = args.Length > 0 ? args[0] : "all";
        var total = 0;
        if (scenario is "all" or "steps")
        {
            var worker = new Worker("worker");
            total += await worker.RunAsync(3); // [main-run]
        } // [main-run-end]

        if (scenario is "all" or "chain")
        {
            total += await Chain.OuterAsync(5); // [main-chain]
        }

        if (scenario is "all" or "throw")
        {
            try
            {
                await Thrower.FailLaterAsync("late");
            }
            catch (InvalidOperationException ex)
            {
                total += ex.Message.Length; // [main-caught]
            }
        }

        if (scenario is "all" or "mixed")
        {
            total += await Mixed.RoundTripAsync(4);
        }

        if (scenario is "all" or "race")
        {
            total += await Elsewhere.CallAsync(1);
        }

        // Not in "all": its caller waits for the method to release it, which only a stop in between makes interesting.
        if (scenario is "held")
        {
            total += await Held.HoldAsync(3); // [main-held]
        }

        Console.WriteLine($"async total={total}"); // [main-end]
        return total % 100;
    }
}
