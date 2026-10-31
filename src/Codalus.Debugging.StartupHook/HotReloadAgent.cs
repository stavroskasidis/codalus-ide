using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

/// <summary>
/// The program's side of Hot Reload while debugging (ADR 0090): the Codalus debugger applies an edit's deltas through
/// the debugging API, which, unlike <c>MetadataUpdater.ApplyUpdate</c> in <c>dotnet watch</c>'s agent, tells nobody.
/// This agent connects to the IDE's pipe (<c>CODALUS_HOTRELOAD_PIPE</c>), and for each edit it is told of, calls the
/// program's <c>[MetadataUpdateHandler]</c>s with the updated types, as <c>dotnet watch</c>'s agent does: every
/// handler's <c>ClearCache</c>, then every <c>UpdateApplication</c>, lower-level assemblies first. Blazor re-renders its
/// components then. A line per edit, <c>update {mvid}:{token},{token};{mvid}:…</c>, is answered with <c>done</c> or
/// <c>error {message}</c>.
/// </summary>
internal static class HotReloadAgent
{
    public const string PipeVariable = "CODALUS_HOTRELOAD_PIPE";

    private const string HandlerAttribute = "System.Reflection.Metadata.MetadataUpdateHandlerAttribute";

    private static readonly char[] s_modules = { ';' };
    private static readonly char[] s_tokens = { ',' };

    public static void Start()
    {
        var name = Environment.GetEnvironmentVariable(PipeVariable);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        // The program's own child processes aren't debugged by this session.
        Environment.SetEnvironmentVariable(PipeVariable, null);
        var thread = new Thread(() => Run(name)) { IsBackground = true, Name = "Codalus Hot Reload agent" };
        thread.Start();
    }

    private static void Run(string name)
    {
        try
        {
            using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut))
            {
                pipe.Connect(10_000);
                var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("update ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string answer;
                    try
                    {
                        answer = Invoke(UpdatedTypes(line.Substring("update ".Length)));
                    }
                    catch (Exception ex)
                    {
                        answer = "error " + ex.Message;
                    }

                    writer.WriteLine(answer.Replace('\r', ' ').Replace('\n', ' '));
                }
            }
        }
        catch (Exception)
        {
            // The IDE isn't there (it ended, or timed out): the program runs on without its edits' handlers.
        }
    }

    /// <summary>The types an edit changed, from their module's id and metadata tokens.</summary>
    private static Type[] UpdatedTypes(string modules)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var types = new List<Type>();
        foreach (var module in modules.Split(s_modules, StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = module.IndexOf(':');
            if (colon < 0 || !Guid.TryParse(module.Substring(0, colon), out var mvid))
            {
                continue;
            }

            var loaded = assemblies.FirstOrDefault(a => !a.IsDynamic && a.ManifestModule.ModuleVersionId == mvid);
            if (loaded == null)
            {
                continue;
            }

            foreach (var token in module.Substring(colon + 1).Split(s_tokens, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    types.Add(loaded.ManifestModule.ResolveType(int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture)));
                }
                catch (Exception)
                {
                    // A type the edit added and nothing loaded yet: the handlers get the others.
                }
            }
        }

        return types.ToArray();
    }

    /// <summary>Every handler's <c>ClearCache</c>, then every <c>UpdateApplication</c>.</summary>
    private static string Invoke(Type[] updatedTypes)
    {
        var handlers = Handlers();
        var errors = new List<string>();
        foreach (var name in new[] { "ClearCache", "UpdateApplication" })
        {
            foreach (var handler in handlers)
            {
                var method = handler.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Type[]) }, null);
                if (method == null)
                {
                    continue;
                }

                try
                {
                    method.Invoke(null, new object[] { updatedTypes });
                }
                catch (TargetInvocationException ex)
                {
                    errors.Add($"{handler.FullName}.{name}: {(ex.InnerException ?? ex).Message}");
                }
            }
        }

        return errors.Count == 0 ? "done" : "error " + string.Join("; ", errors);
    }

    /// <summary>The handler types of the loaded assemblies, an assembly's after those of the assemblies it references.</summary>
    private static List<Type> Handlers()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        var byName = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name;
            if (name != null && !byName.ContainsKey(name))
            {
                byName[name] = assembly;
            }
        }

        var ordered = new List<Assembly>();
        var visited = new HashSet<Assembly>();
        foreach (var assembly in assemblies)
        {
            Visit(assembly, byName, visited, ordered);
        }

        var handlers = new List<Type>();
        foreach (var assembly in ordered)
        {
            IList<CustomAttributeData> attributes;
            try
            {
                attributes = assembly.GetCustomAttributesData();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var attribute in attributes)
            {
                if (attribute.AttributeType.FullName == HandlerAttribute && attribute.ConstructorArguments.Count == 1
                    && attribute.ConstructorArguments[0].Value is Type handler && !handlers.Contains(handler))
                {
                    handlers.Add(handler);
                }
            }
        }

        return handlers;
    }

    private static void Visit(Assembly assembly, Dictionary<string, Assembly> byName, HashSet<Assembly> visited, List<Assembly> ordered)
    {
        if (!visited.Add(assembly))
        {
            return;
        }

        AssemblyName[] references;
        try
        {
            references = assembly.GetReferencedAssemblies();
        }
        catch (Exception)
        {
            references = Array.Empty<AssemblyName>();
        }

        foreach (var reference in references)
        {
            if (reference.Name != null && byName.TryGetValue(reference.Name, out var referenced))
            {
                Visit(referenced, byName, visited, ordered);
            }
        }

        ordered.Add(assembly);
    }
}
