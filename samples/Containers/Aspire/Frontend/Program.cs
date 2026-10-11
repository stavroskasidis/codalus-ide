var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// The AppHost's WithReference(api) gives the api's address as services__api__http__0.
var api = builder.Configuration["services:api:http:0"] ?? "http://localhost:5301";

app.MapGet("/", async () =>
{
    using var http = new HttpClient { BaseAddress = new Uri(api) };
    return "Frontend: " + await http.GetStringAsync("/weather");
});

app.Run();
