using Codalus.Sample.Legacy;
using Library;

namespace LegacyApp
{
    /// <summary>Uses a packages.config package (Codalus.Sample.Legacy) and an SDK-style project (Library).</summary>
    public static class Greeting
    {
        public static string For(string greeting, string name)
        {
            return greeting + ", " + name + ". " + Legacy.Describe(name) + " " + new Greeter(name).Greet();
        }
    }
}
