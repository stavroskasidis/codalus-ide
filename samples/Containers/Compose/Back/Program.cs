var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Answer.For(Environment.MachineName));

app.Run();

/// <summary>What the back answers. The compose tests stop at the line that builds the answer.</summary>
public static class Answer
{
    public static string For(string host)
    {
        var answer = $"back on {host}";
        return answer;
    }
}
