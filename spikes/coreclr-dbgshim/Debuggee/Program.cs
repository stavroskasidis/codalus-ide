if (args.Length > 0 && args[0] == "wait") { Console.WriteLine("ready"); Console.ReadLine(); } else if (args.Length > 0 && args[0] == "child") return Children.Child();
var order = new Order { Id = 42, Customer = "Ada" };
int total = 0;
for (int i = 1; i <= 3; i++)
{
    total += i * order.Id;
    Console.WriteLine($"step {i}: {total}");
}
Console.WriteLine($"total {total} for {order.Label}");
if (Environment.GetEnvironmentVariable("SPIKE_CHILD") == "1") Children.Spawn();
return 7;

class Order
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public string Label => $"#{Id} for {Customer}";
}
