using MonoDebuggee;

var cart = new Cart("Ada");
cart.Add(new Item("Tea", 3));
cart.Add(new Item("Bread", 2));
var total = cart.Total(); // [mono-total]
Console.WriteLine($"{cart.Owner}: {total}");

try
{
    cart.Checkout(); // [mono-checkout]
}
catch (InvalidOperationException ex)
{
    Console.WriteLine("caught: " + ex.Message);
}

Console.WriteLine("done");

namespace MonoDebuggee
{
    public sealed record Item(string Name, int Price);

    public sealed class Cart(string owner)
    {
        private readonly List<Item> _items = [];

        public string Owner { get; } = owner;

        public int Count => _items.Count;

        public void Add(Item item) => _items.Add(item);

        public int Total()
        {
            var sum = 0;
            foreach (var item in _items)
            {
                sum += item.Price; // [mono-sum]
            }

            return sum;
        }

        public void Checkout() => throw new InvalidOperationException("The till is closed.");
    }
}
