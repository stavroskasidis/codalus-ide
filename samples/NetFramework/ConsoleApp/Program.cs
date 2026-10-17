using System;
using Library;

namespace ConsoleApp
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var greeter = new Greeter("Codalus");
            Console.WriteLine(greeter.Greet());
            Console.WriteLine("Runtime: " + Greeter.RuntimeName);
            return args.Length;
        }
    }
}
