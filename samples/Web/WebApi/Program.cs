// Tests find lines by the "// [name]" markers; keep each marker on its statement's line.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();

app.MapGet("/", () => "Hello from the Web fixture"); // [root]
app.MapGet("/weatherforecast", Forecasts.Get); // [forecast]

var todos = app.MapGroup("/api/todos").WithTags("Todos");
todos.MapGet("/", () => TodoStore.All); // [todos-list]
todos.MapGet("/{id:int}", (int id) => TodoStore.All.FirstOrDefault(t => t.Id == id) is { } todo ? Results.Ok(todo) : Results.NotFound()); // [todos-get]
todos.MapPost("/", (Todo todo) => Results.Created($"/api/todos/{todo.Id}", TodoStore.Add(todo))); // [todos-post]
app.MapMethods("/ping", ["GET", "HEAD"], () => "pong"); // [ping]
app.MapControllers();

app.Run();

public sealed record Todo(int Id, string Title, bool Done = false);

public static class TodoStore
{
    private static readonly List<Todo> s_todos = [new(1, "Write the fixture"), new(2, "Test the endpoints")];

    public static IReadOnlyList<Todo> All => s_todos;

    public static Todo Add(Todo todo)
    {
        s_todos.Add(todo);
        return todo;
    }
}

public static class Forecasts
{
    public static string[] Get() => ["Sunny", "Cloudy"]; // [forecast-handler]
}
