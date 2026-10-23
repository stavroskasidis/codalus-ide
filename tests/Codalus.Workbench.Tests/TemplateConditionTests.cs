using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>The template engine's conditions as New Project evaluates them, as an IDE host (ADR 0082).</summary>
public sealed class TemplateConditionTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public void Comparisons_with_strings()
    {
        const string condition = "(InteractivityPlatform != \"None\")";
        Assert.True(TemplateCondition.Evaluate(condition, Values(("InteractivityPlatform", "Server"))));
        Assert.False(TemplateCondition.Evaluate(condition, Values(("InteractivityPlatform", "None"))));
        Assert.True(TemplateCondition.Evaluate("XUnitVersion == v3", Values(("XUnitVersion", "v3")))); // a bare word is its text
        Assert.False(TemplateCondition.Evaluate("(XUnitVersion == v3)", Values(("XUnitVersion", "v2"))));
        Assert.True(TemplateCondition.Evaluate("(auth == 'Individual')", Values(("auth", "individual")))); // values compare in any case
    }

    [Fact]
    public void The_host_is_an_ide()
    {
        const string ide = "(HostIdentifier != \"dotnetcli\" && HostIdentifier != \"dotnetcli-preview\")";
        Assert.True(TemplateCondition.Evaluate(ide, Values()));
        Assert.False(TemplateCondition.Evaluate(ide, Values(("HostIdentifier", "dotnetcli"))));
        Assert.Equal("vs", TemplateCondition.IdeHostIdentifier);
    }

    [Fact]
    public void Booleans_and_operators()
    {
        Assert.True(TemplateCondition.Evaluate("(!skipRestore)", Values(("skipRestore", "false"))));
        Assert.False(TemplateCondition.Evaluate("(!skipRestore)", Values(("skipRestore", "true"))));
        Assert.True(TemplateCondition.Evaluate("a && (b || !c)", Values(("a", "true"), ("b", "false"), ("c", "false"))));
        Assert.False(TemplateCondition.Evaluate("a && (b || !c)", Values(("a", "true"), ("b", "false"), ("c", "true"))));
        Assert.True(TemplateCondition.Evaluate("a || b && c", Values(("a", "true"), ("b", "false"), ("c", "false")))); // && binds tighter
        Assert.False(TemplateCondition.Evaluate("false", Values()));
        Assert.True(TemplateCondition.Evaluate("TRUE", Values()));
        Assert.True(TemplateCondition.Evaluate("count == 3", Values(("count", "3"))));
    }

    [Fact]
    public void Computed_symbols_are_evaluated_from_their_conditions()
    {
        var computed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UseWebAssembly"] = "(InteractivityPlatform == \"WebAssembly\" || InteractivityPlatform == \"Auto\")",
            ["Loop"] = "(Loop)",
        };
        Assert.True(TemplateCondition.Evaluate("(!UseWebAssembly)", Values(("InteractivityPlatform", "Server")), computed));
        Assert.False(TemplateCondition.Evaluate("(!UseWebAssembly)", Values(("InteractivityPlatform", "Auto")), computed));
        Assert.True(TemplateCondition.Evaluate("Loop", Values(), computed)); // a cycle ends as unknown: true
    }

    [Fact]
    public void Unknown_symbols_and_malformed_text_count_as_true()
    {
        Assert.True(TemplateCondition.Evaluate("NotASymbol", Values()));
        Assert.True(TemplateCondition.Evaluate("!NotASymbol", Values()));
        Assert.True(TemplateCondition.Evaluate("(a == ", Values(("a", "x"))));
        Assert.True(TemplateCondition.Evaluate("a $ b", Values()));
        Assert.True(TemplateCondition.Evaluate("\"unterminated", Values()));
        Assert.True(TemplateCondition.Evaluate("a b", Values(("a", "false")))); // trailing text
        Assert.True(TemplateCondition.Evaluate(null, Values()));
        Assert.True(TemplateCondition.Evaluate("  ", Values()));
    }
}
