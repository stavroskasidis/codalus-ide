namespace Core.Tests;

internal static class GreeterChecks
{
    public static bool Works() => Greeter.Greet("x") == "Hello, x!";
}

