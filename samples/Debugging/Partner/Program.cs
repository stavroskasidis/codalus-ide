// The second program of a compound launch (multiple startup projects, M14c). Tests find lines by the "// [name]"
// markers; keep each marker on its statement's line.
var greeting = args.Length > 0 ? string.Join(' ', args) : "partner";
var length = greeting.Length;
Console.WriteLine($"partner says {greeting}"); // [partner]
Thread.Sleep(200);
return length;
