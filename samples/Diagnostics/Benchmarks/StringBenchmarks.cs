using System.Text;
using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>Three ways to build the same string, with allocations measured.</summary>
[MemoryDiagnoser]
public class StringBenchmarks
{
    [Params(16)]
    public int Count { get; set; }

    [Benchmark(Baseline = true)]
    public string Builder()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Count; i++)
        {
            builder.Append(i).Append(',');
        }

        return builder.ToString();
    }

    [Benchmark]
    public string Concat()
    {
        var text = string.Empty;
        for (var i = 0; i < Count; i++)
        {
            text += i + ",";
        }

        return text;
    }

    [Benchmark, BenchmarkCategory("Join")]
    public string Join() => string.Join(',', Enumerable.Range(0, Count)) + ",";

    // Not a benchmark: no glyph.
    public string Helper() => Builder();
}

/// <summary>A second class, so a file can hold more than one.</summary>
public class HashBenchmarks
{
    private readonly byte[] _data = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();

    [Benchmark]
    public int Sum()
    {
        var sum = 0;
        foreach (var b in _data)
        {
            sum += b;
        }

        return sum;
    }
}
