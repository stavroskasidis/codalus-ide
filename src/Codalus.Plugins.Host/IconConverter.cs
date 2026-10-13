using System.Text;
using System.Text.Json;

namespace Codalus.Plugins.Host;

/// <summary>
/// <c>codalus-pluginhost --compile-icons &lt;output.json&gt; &lt;list.txt&gt;</c> (M22b; the list has one SVG path per line): converts the SVG icons of a
/// plugin's icon theme into the drawing operations the shell replays for its own icons (eng/icons/CompileIcons.cs, the
/// same parser), so the shell never parses SVG. The output has one entry per icon, with its operations or the reason
/// it can't be drawn (an element or attribute the parser doesn't support).
/// </summary>
internal static class IconConverter
{
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            Console.Error.WriteLine("usage: codalus-pluginhost --compile-icons <output.json> <list.txt>");
            return 2;
        }

        using var stream = File.Create(args[0]);
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteStartArray("icons");
        foreach (var path in File.ReadAllLines(args[1]).Where(line => line.Length > 0))
        {
            json.WriteStartObject();
            json.WriteString("path", path);
            try
            {
                var ops = new StringBuilder();
                var numbers = new List<double>();
                var colors = new List<uint>();
                var size = CompileIcons.Parse(File.ReadAllText(path), ops, numbers, colors);
                json.WriteNumber("size", size);
                json.WriteString("ops", ops.ToString());
                json.WriteStartArray("numbers");
                foreach (var number in numbers)
                {
                    json.WriteNumberValue(Math.Round(number, 4));
                }

                json.WriteEndArray();
                json.WriteStartArray("colors");
                foreach (var color in colors)
                {
                    json.WriteNumberValue(color);
                }

                json.WriteEndArray();
            }
            catch (Exception ex) when (ex is FormatException or System.Xml.XmlException or IOException or UnauthorizedAccessException)
            {
                json.WriteString("error", ex.Message);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        return 0;
    }
}
