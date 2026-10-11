using Blogging.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BloggingContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Blogging")));
var app = builder.Build();

app.MapGet("/", () => "Hello from the Data fixture");
app.MapGet("/blogs", async (BloggingContext db) => await db.Blogs.Select(b => new { b.Id, b.Url, b.Name }).ToListAsync());

app.Run();
