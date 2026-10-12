// Activities: requests, database commands, spans and file I/O for the activity views (M18b). Each round it sends HTTP
// requests to a tiny server of its own (System.Net.Http's events), runs EF Core queries against an in-memory SQLite
// database (EF Core's DiagnosticSource events), wraps both in ActivitySource spans (Daedalus.Samples.Activities:
// "round" with "fetch" and "query" children), and writes and reads a 1 MB file. Runs for the seconds given (default
// 5; 0: until killed).
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 5;

using var source = new ActivitySource("Daedalus.Samples.Activities");
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
_ = Task.Run(() => Server.ServeAsync(listener));

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<Shop>().UseSqlite(connection).Options;
using (var setup = new Shop(options))
{
    setup.Database.EnsureCreated();
    setup.Products.AddRange(Enumerable.Range(1, 50).Select(i => new Product { Name = "Product " + i, Price = i * 1.5 }));
    setup.SaveChanges();
}

using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
var file = Path.Combine(Path.GetTempPath(), $"daedalus-activities-{Environment.ProcessId}.bin");
var block = new byte[1024 * 1024];
Console.WriteLine($"Activities {Environment.ProcessId} started");

// The time counts from here (a cold EF Core start can take seconds on a busy machine), and at least three rounds run.
var until = seconds <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);
var rounds = 0;
try
{
    while (rounds < 3 || DateTime.UtcNow < until)
    {
        using (var round = source.StartActivity("round"))
        {
            round?.SetTag("round", rounds);
            using (source.StartActivity("fetch", ActivityKind.Client))
            {
                await http.GetStringAsync("products?page=" + (rounds % 3));
                await http.GetStringAsync("slow");
            }

            using (source.StartActivity("query"))
            {
                using var shop = new Shop(options);
                var cheap = await shop.Products.Where(p => p.Price < 20).OrderBy(p => p.Name).ToListAsync();
                var count = await shop.Products.CountAsync();
                _ = cheap.Count + count;
            }

            await File.WriteAllBytesAsync(file, block);
            _ = (await File.ReadAllBytesAsync(file)).Length;
        }

        rounds++;
        await Task.Delay(20);
    }
}
finally
{
    File.Delete(file);
}

Console.WriteLine($"Activities finished: {rounds} rounds");

internal static class Server
{
    /// <summary>Answers every request with a small body; /slow after 30 ms.</summary>
    public static async Task ServeAsync(TcpListener listener)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    while (await reader.ReadLineAsync() is { } requestLine && requestLine.Length > 0)
                    {
                        while (await reader.ReadLineAsync() is { Length: > 0 })
                        {
                        }

                        if (requestLine.Contains("/slow", StringComparison.Ordinal))
                        {
                            await Task.Delay(30);
                        }

                        var body = Encoding.UTF8.GetBytes("""{"items":[1,2,3]}""");
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                    }
                }
            });
        }
    }
}

internal sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double Price { get; set; }
}

internal sealed class Shop(DbContextOptions<Shop> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}
