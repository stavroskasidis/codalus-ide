namespace App;

/// <summary>Inline values while debugging (M62): what each line reads and writes.</summary>
internal sealed class Values
{
    private int _count = 2;
    private static string s_label = "total";

    public int Limit { get; set; } = 10;

    public int Computed => _count * 2;

    public int Sum(int[] numbers, int start)
    {
        var total = start; // [declare]
        foreach (var n in numbers)
        {
            total += n * _count; // [loop]
        }

        if (numbers is [var first, ..] && int.TryParse("3", out var parsed))
        {
            total += first + parsed + Limit + Computed; // [patterns]
        }

        var (low, high) = (total - 1, total + 1);
        Func<int, int> twice = x => x * 2; // [lambda]
        Console.WriteLine(s_label + low + high + twice(total) + Math.Abs(total)); // [stop]
        return total;
    }
}
