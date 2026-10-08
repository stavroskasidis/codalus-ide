using Spike.Core.Inventory;
using Spike.Utilities;

namespace Spike.App;

internal static class Program
{
    private const int DefaultCapacity = 16;

    private static async Task<int> Main(string[] args)
    {
        var store = new InventoryStore(DefaultCapacity);
        store.Add(new Product("SKU-001", "Widget", 9.99m, Category.Hardware));
        store.Add(new Product("SKU-002", "Gadget", 24.50m, Category.Hardware));
        store.Add(new Product("SKU-003", "Manual", 4.25m, Category.Books));

        var expensive = store.Where(p => p.Price > 5m).OrderBy(p => p.Name).ToList();
        foreach (var product in expensive)
        {
            Console.WriteLine($"{product.Sku,-8}\t{product.Name}\t{product.Price:C}");
        }

        var total = await store.ComputeTotalAsync(CancellationToken.None);
        Console.WriteLine(Text.Indent($"Total: {total}\n", depth: 2));
        return args.Length == 0 ? 0 : 1;
    }
}
