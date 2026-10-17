using System;
using System.Configuration;

namespace LegacyApp
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var greeting = ConfigurationManager.AppSettings["greeting"] ?? "Hello";
            Console.WriteLine(Greeting.For(greeting, args.Length > 0 ? args[0] : "world"));
        }
    }
}
