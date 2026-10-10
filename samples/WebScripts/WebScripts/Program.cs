var app = WebApplication.CreateBuilder(args).Build();

// A static site: wwwroot/index.html and its script.
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
