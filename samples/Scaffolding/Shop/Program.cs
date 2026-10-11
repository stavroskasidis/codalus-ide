using Microsoft.EntityFrameworkCore;
using Shop.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddDbContext<ShopContext>(options => options.UseSqlite("Data Source=shop.db"));

var app = builder.Build();
app.MapGet("/", () => "Hello from the Scaffolding fixture");
app.MapControllers();
app.MapRazorPages();

app.Run();
