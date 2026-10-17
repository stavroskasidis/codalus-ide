using System.Runtime.InteropServices;

namespace Library
{
    /// <summary>Greets people, in C# 7.3 (the .NET Framework's default language version).</summary>
    public class Greeter
    {
        private readonly string _name;

        public Greeter(string name)
        {
            _name = name;
        }

        /// <summary>The runtime's description: ".NET Framework 4.8…" on the desktop CLR.</summary>
        public static string RuntimeName => RuntimeInformation.FrameworkDescription;

        public string Greet() => $"Hello from .NET Framework, {_name}!";
    }
}
