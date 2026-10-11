var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/weather", () => Forecast.Today());

app.Run();

/// <summary>The API's answer. The Aspire tests stop at the line that builds it.</summary>
public static class Forecast
{
    public static string Today()
    {
        var forecast = $"Sunny, from the api (process {Environment.ProcessId})";
        return forecast;
    }
}
