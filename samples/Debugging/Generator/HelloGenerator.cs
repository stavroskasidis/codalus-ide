using Microsoft.CodeAnalysis;

namespace Generator;

/// <summary>Adds <c>Generated.Hello.Names</c>: the names of the classes in the compilation.</summary>
[Generator]
public sealed class HelloGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var names = context.CompilationProvider.Select((compilation, _) => string.Join(", ", compilation.Assembly.TypeNames));
        context.RegisterSourceOutput(names, (output, list) =>
        {
            var source = "namespace Generated;\n\npublic static class Hello\n{\n    public const string Names = \"" + list + "\";\n}\n"; // [generate]
            output.AddSource("Hello.g.cs", source);
        });
    }
}
