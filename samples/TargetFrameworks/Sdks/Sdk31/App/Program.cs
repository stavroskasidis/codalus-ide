using System;
using System.IO;

namespace App
{
    public static class Program
    {
        public static void Main()
        {
            var runtime = Path.GetFileName(Path.GetDirectoryName(typeof(object).Assembly.Location));
            Console.WriteLine("runtime=" + runtime + " " + Lib.Text.Hello("Sdk31")); // [break]
        }
    }
}
