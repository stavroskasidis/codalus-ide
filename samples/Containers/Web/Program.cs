var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Greeting.For(Environment.MachineName));

app.Run();

/// <summary>What the app answers. The container tests stop at the line that builds the greeting.</summary>
public static class Greeting
{
    public static string For(string host)
    {
        var greeting = $"Hello from {host} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
        return greeting;
    }
}
