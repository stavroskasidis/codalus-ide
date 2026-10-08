using System.Collections;
using Spike.Utilities;

namespace Spike.Core.Inventory;

public delegate void ProductAddedHandler(object sender, Product product);

public interface IInventory : IEnumerable<Product>
{
    event ProductAddedHandler? ProductAdded;

    void Add(Product product);
}

/// <summary>In-memory inventory with change notification.</summary>
public class InventoryStore(int capacity) : IInventory
{
    private readonly List<Product> _items = new(capacity);
    private static int s_instances;

    public event ProductAddedHandler? ProductAdded;

    public int Count => _items.Count;

    public static int Instances => s_instances;

    public void Add(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        _items.Add(product);
        Interlocked.Increment(ref s_instances);
        ProductAdded?.Invoke(this, product);
    }

    public async Task<decimal> ComputeTotalAsync(CancellationToken cancellationToken)
    {
        decimal total = 0;
        foreach (var chunk in _items.Chunk(4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            total += chunk.Sum(p => p.Price);
        }

        return Money.Round(total);
    }

    public IEnumerator<Product> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
