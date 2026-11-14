var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("back", client => client.BaseAddress = new Uri(builder.Configuration["BACK_URL"] ?? "http://localhost:5291"));
var app = builder.Build();

app.MapGet("/", async (IHttpClientFactory clients) => Page.For(await clients.CreateClient("back").GetStringAsync("/")));

app.Run();

/// <summary>What the front answers: its own line and the back's. The compose tests stop at the line that builds it.</summary>
public static class Page
{
    public static string For(string back)
    {
        var page = $"front, then {back}";
        return page;
    }
}
