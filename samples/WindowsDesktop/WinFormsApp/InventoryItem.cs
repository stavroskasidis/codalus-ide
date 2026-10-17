namespace WinFormsApp
{
    /// <summary>An item of the inventory: <c>InventoryForm</c>'s binding source's data source (the designer's data binding fixture, M41b).</summary>
    public class InventoryItem
    {
        public string Name { get; set; } = string.Empty;

        public int Quantity { get; set; }

        public bool InStock { get; set; }
    }
}
