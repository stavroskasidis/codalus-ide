using Greetings;

Console.WriteLine(Greeter.Greet("Hello"));
Console.WriteLine($"args: {string.Join(' ', args)}");
Console.WriteLine($"GREETING: {Environment.GetEnvironmentVariable("GREETING")}");
Console.WriteLine($"cwd: {Environment.CurrentDirectory}");
Console.Error.WriteLine("to stderr");

var exit = Array.IndexOf(args, "--exit");
return exit >= 0 && exit + 1 < args.Length ? int.Parse(args[exit + 1], System.Globalization.CultureInfo.InvariantCulture) : 0;
