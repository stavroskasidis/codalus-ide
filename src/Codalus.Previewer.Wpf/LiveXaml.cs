using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Xaml;
using static Codalus.Previewer.Wpf.WpfTree;
using XamlReader = System.Windows.Markup.XamlReader;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// Failed bindings in a running WPF app (M64, ADR 0103): a listener on <c>PresentationTraceSources.DataBindingSource</c>
/// (warnings and errors) reads WPF's binding errors (<c>BindingExpression:Path=…; … target element is 'TextBlock'
/// (Name='…'); target property is 'Text'</c>) and keeps them for the IDE's <c>bindings</c> request, as the Avalonia
/// agent does; <c>event bindings</c> says new ones came. WPF names no element instance, so no ancestors are given.
/// </summary>
internal sealed partial class WpfBindings : TraceListener
{
    private const int MaxFailures = 500;
    private static readonly object s_gate = new();
    private static readonly List<string[]> s_failures = [];
    private static readonly HashSet<string> s_seen = new(StringComparer.Ordinal);
    private static Action<string>? s_send;
    private static bool s_notified;
    private static int s_installed;
    private readonly StringBuilder _line = new();

    /// <summary>Listens from the startup hook on (WPF's trace source needs no UI thread); <paramref name="send"/> once connected.</summary>
    public static void Install(Action<string>? send)
    {
        Action<string>? pending = null;
        lock (s_gate)
        {
            s_send = send ?? s_send;
            pending = s_failures.Count > 0 ? Notify() : null;
        }

        Tell(pending);
        if (Interlocked.Exchange(ref s_installed, 1) == 1)
        {
            return;
        }

        // WPF traces only under a debugger, with its registry switch, or once refreshed: refreshed here, as the classic
        // IDE's XAML tools turn it on for the app they run.
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(new WpfBindings());
        if ((source.Switch.Level & SourceLevels.Warning) != SourceLevels.Warning)
        {
            source.Switch.Level = SourceLevels.Warning;
        }
    }

    public override void Write(string? message) => _line.Append(message);

    public override void WriteLine(string? message)
    {
        _line.Append(message);
        var line = _line.ToString();
        _line.Clear();
        if (Parse(line) is not { } failure)
        {
            return;
        }

        Action<string>? send;
        lock (s_gate)
        {
            if (s_failures.Count >= MaxFailures || !s_seen.Add(string.Join("\u001f", failure)))
            {
                return;
            }

            s_failures.Add(failure);
            send = Notify();
        }

        Tell(send);
    }

    [GeneratedRegex(@"BindingExpression:Path=(?<path>[^;]*);.*?target element is '(?<type>[^']+)' \(Name='(?<name>[^']*)'\); target property is '(?<property>[^']+)'", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex LineRegex();

    /// <summary>A binding error of WPF's trace as the agent's record (property, path, message, type, name, ancestors), or null.</summary>
    internal static string[]? Parse(string line)
    {
        var match = LineRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var message = line[..line.IndexOf("BindingExpression:Path=", StringComparison.Ordinal)];
        var colon = message.IndexOf(" : ", StringComparison.Ordinal);
        message = (colon >= 0 ? message[(colon + 3)..] : message).Trim().TrimEnd(';').Trim();
        return [match.Groups["property"].Value, match.Groups["path"].Value, message, match.Groups["type"].Value, match.Groups["name"].Value, string.Empty];
    }

    private static Action<string>? Notify()
    {
        if (s_notified || s_send is null)
        {
            return null;
        }

        s_notified = true;
        return s_send;
    }

    private static void Tell(Action<string>? send)
    {
        try
        {
            send?.Invoke("event\tbindings");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>The failures since the last request.</summary>
    public static string Take()
    {
        lock (s_gate)
        {
            var records = s_failures.ToList();
            s_failures.Clear();
            s_notified = false;
            return Join(records);
        }
    }
}

/// <summary>
/// XAML Hot Reload in a running WPF app (M64, ADR 0103, D56; Windows only): a saved <c>.xaml</c> file's text is
/// loaded again into each live instance of its <c>x:Class</c> with WPF's XAML schema and System.Xaml's object writer
/// populating the instance (<c>RootObjectInstance</c>), as WPF's loader reads loose XAML (the previewer's reader, with
/// event handlers kept: they name the running class's methods). The instance keeps its data context and what XAML
/// doesn't set; its resources and name scope are new, and its named fields point at the new elements. A failure
/// leaves it as it was and comes back with its line.
/// </summary>
internal static class WpfReload
{
    private static readonly Regex s_class = new(@"\bx:Class\s*=\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant);

    /// <summary><c>applied count</c>, <c>failed line column message</c>, or <c>none</c> without <c>x:Class</c>.</summary>
    public static string Reload(Application application, string xaml)
    {
        var className = s_class.Match(xaml) is { Success: true } match ? match.Groups["name"].Value.Trim() : null;
        if (className is null)
        {
            return "none";
        }

        var type = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.GetType(className, throwOnError: false)).FirstOrDefault(t => t is not null);
        if (type is null)
        {
            return Failed(0, 0, $"The running app has no class {className} (x:Class): restart it to apply this XAML.");
        }

        var instances = new List<object>();
        if (application.GetType() == type)
        {
            instances.Add(application);
        }

        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>(application.Windows.Cast<Window>());
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (!seen.Add(element))
            {
                continue;
            }

            if (element.GetType() == type)
            {
                instances.Add(element);
            }

            foreach (var child in LogicalChildren(element).Concat(VisualChildren(element)))
            {
                stack.Push(child);
            }
        }

        foreach (var instance in instances)
        {
            if (Populate(instance, type.Assembly, xaml) is { } failure)
            {
                return failure;
            }
        }

        return "applied\t" + instances.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static string? Populate(object instance, Assembly local, string xaml)
    {
        var restore = new List<Action>();
        try
        {
            switch (instance)
            {
                case FrameworkElement element:
                    var resources = element.Resources;
                    element.Resources = new ResourceDictionary();
                    restore.Add(() => element.Resources = resources);
                    break;
                case Application app:
                    var appResources = app.Resources;
                    app.Resources = new ResourceDictionary();
                    restore.Add(() => app.Resources = appResources);
                    break;
            }

            if (instance is System.Windows.Controls.ContentControl content)
            {
                var old = content.Content;
                restore.Add(() => content.Content = old);
            }

            var scope = new NameScope();
            if (instance is DependencyObject dependency)
            {
                var oldScope = NameScope.GetNameScope(dependency);
                NameScope.SetNameScope(dependency, scope);
                restore.Add(() => NameScope.SetNameScope(dependency, oldScope));
            }

            var readerSettings = new XamlXmlReaderSettings { LocalAssembly = local, ProvideLineInfo = true };
            using var reader = new DesignTimeXamlReader(new XamlXmlReader(new StringReader(xaml), XamlReader.GetWpfSchemaContext(), readerSettings), keepEvents: true);
            var writer = new XamlObjectWriter(reader.SchemaContext, new XamlObjectWriterSettings
            {
                RootObjectInstance = instance,
                ExternalNameScope = scope,
                RegisterNamesOnExternalNamescope = true,
            });
            while (reader.Read())
            {
                writer.SetLineInfo(reader.LineNumber, reader.LinePosition);
                writer.WriteNode(reader);
            }

            writer.Close();

            // The named fields (the markup compiler's, in obj/*.g.i.cs): the new elements.
            for (var type = instance.GetType(); type is not null && type.Assembly == local; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (scope.FindName(field.Name) is { } named && field.FieldType.IsInstanceOfType(named))
                    {
                        field.SetValue(instance, named);
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            for (var i = restore.Count - 1; i >= 0; i--)
            {
                try
                {
                    restore[i]();
                }
                catch (Exception undo) when (undo is not OutOfMemoryException)
                {
                    _ = undo;
                }
            }

            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return error is XamlException xamlError
                ? Failed(xamlError.LineNumber, xamlError.LinePosition, error.Message)
                : error is System.Windows.Markup.XamlParseException parse ? Failed(parse.LineNumber, parse.LinePosition, error.Message) : Failed(0, 0, error.Message);
        }
    }

    private static string Failed(int line, int column, string message) =>
        "failed\t" + line.ToString(CultureInfo.InvariantCulture) + "\t" + column.ToString(CultureInfo.InvariantCulture) + "\t" + Clean(message);

    public static string Decode(string base64) => new UTF8Encoding(false).GetString(Convert.FromBase64String(base64));
}
