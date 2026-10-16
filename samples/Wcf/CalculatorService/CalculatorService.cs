using CoreWCF;

namespace CalculatorService;

public sealed class CalculatorService : ICalculatorService
{
    private static readonly Person[] s_people =
    [
        new() { Name = "Ada", Age = 36, Mood = Mood.Happy, Tags = ["math", "engines"] },
        new() { Name = "Alan", Age = 41, Mood = Mood.Calm, Tags = ["codes"] },
        new() { Name = "Grace", Age = 85, Mood = Mood.Grumpy, Tags = ["compilers", "navy"] },
    ];

    public double Add(double a, double b) => a + b;

    public double Divide(double a, double b) =>
        b == 0
            ? throw new FaultException<CalculationFault>(new CalculationFault { Operation = "Divide", Problem = "Division by zero" }, new FaultReason("Division by zero"))
            : a / b;

    public string Greet(Person person) =>
        $"Hello {person.Name}, {person.Age} and {person.Mood.ToString().ToLowerInvariant()}{(person.Tags.Count > 0 ? " (" + string.Join(", ", person.Tags) + ")" : string.Empty)}!";

    public Person[] FindPeople(string prefix, int max) =>
        [.. s_people.Where(p => p.Name.StartsWith(prefix ?? string.Empty, StringComparison.OrdinalIgnoreCase)).Take(Math.Max(0, max))];
}
