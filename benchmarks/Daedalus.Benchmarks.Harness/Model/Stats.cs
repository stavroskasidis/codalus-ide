namespace Daedalus.Benchmarks.Harness.Model;

/// <summary>Distribution summary of one metric. Percentiles use the nearest-rank method.</summary>
public sealed record Distribution(int N, double Min, double P50, double P95, double P99, double Max, double Mean, double StdDev)
{
    public static Distribution? Of(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var mean = sorted.Average();
        var variance = sorted.Length > 1 ? sorted.Sum(v => (v - mean) * (v - mean)) / (sorted.Length - 1) : 0;
        return new Distribution(
            sorted.Length,
            sorted[0],
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            mean,
            Math.Sqrt(variance));
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(p, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(p, 1);
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
