namespace Calculator.Service;

public sealed class CalculatorService : ICalculatorService
{
    public int Add(int a, int b) => a + b;

    public string Greet(string name) => $"Hello, {name}!";

    public double Distance(Point from, Point to) => Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
}
