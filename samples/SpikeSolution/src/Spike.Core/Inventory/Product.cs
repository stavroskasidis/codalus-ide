namespace Spike.Core.Inventory;

/// <summary>A product category.</summary>
public enum Category
{
    Hardware,
    Books,
    Software,
}

/// <summary>An immutable product record.</summary>
/// <param name="Sku">Stock keeping unit.</param>
/// <param name="Name">Display name.</param>
/// <param name="Price">Unit price.</param>
/// <param name="Category">The product's category.</param>
public sealed record Product(string Sku, string Name, decimal Price, Category Category)
{
    public static readonly Product Empty = new(string.Empty, "(none)", 0m, Category.Software);

    public bool IsFree => Price == 0m;
}

public readonly record struct StockLevel(string Sku, int Quantity);
