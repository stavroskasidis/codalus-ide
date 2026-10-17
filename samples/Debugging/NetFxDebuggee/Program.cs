using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NetFxDebuggee
{
    [Flags]
    public enum Mood
    {
        Calm = 0,
        Busy = 1,
        Loud = 2,
    }

    public sealed class Item
    {
        public Item(string name, int price)
        {
            Name = name;
            Price = price;
        }

        public string Name { get; }

        public int Price { get; }
    }

    public sealed class Cart
    {
        private readonly List<Item> _items = new List<Item>();
        private readonly int[] _codes = { 7, 8, 9 };

        public Cart(string owner)
        {
            Owner = owner;
        }

        public string Owner { get; }

        public int Count => _items.Count;

        public Mood Mood { get; set; } = Mood.Busy | Mood.Loud;

        public void Add(Item item) => _items.Add(item);

        public int Twice(int value) => value * 2;

        public int Total()
        {
            var sum = 0;
            foreach (var item in _items)
            {
                sum += item.Price; // [netfx-sum]
            }

            return sum;
        }

        public void Checkout()
        {
            throw new InvalidOperationException("The till is closed."); // [netfx-throw]
        }
    }

    internal static class Program
    {
        private static int s_ticks;

        private static int Main(string[] args)
        {
            Thread.CurrentThread.Name = "Main thread";
            if (args.Length > 0 && args[0] == "wait")
            {
                return Wait();
            }

            var cart = new Cart("Ada");
            cart.Add(new Item("Tea", 3));
            cart.Add(new Item("Bread", 2));
            var total = cart.Total(); // [netfx-total]
            Console.WriteLine(cart.Owner + ": " + total);
            Debug.WriteLine("netfx-log");
            var doubled = ComputeAsync(total).Result;
            Console.WriteLine("doubled: " + doubled);
            try
            {
                cart.Checkout(); // [netfx-checkout]
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("caught: " + ex.Message);
            }

            Console.WriteLine("done");
            return 3;
        }

        private static async Task<int> ComputeAsync(int value)
        {
            var factor = 2;
            await Task.Yield();
            var result = value * factor; // [netfx-async]
            return result;
        }

        private static int Wait()
        {
            Console.WriteLine("waiting");
            for (var i = 0; i < 600; i++)
            {
                Tick(i);
                Thread.Sleep(100);
            }

            return 0;
        }

        private static void Tick(int i)
        {
            s_ticks = i + s_ticks - s_ticks; // [netfx-tick]
        }
    }
}
