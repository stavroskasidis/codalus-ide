using System.Text.Json;
using Lib;

var circle = new Circle(2);
Console.WriteLine(circle.Describe(2));
Console.WriteLine(Geometry.Scale(circle, 3));

var json = JsonSerializer.Serialize(new Person("Ada"), AppJsonContext.Default.Person);
Console.WriteLine(json);
Console.WriteLine(Conditional.Framework);

Reporter.Run();
