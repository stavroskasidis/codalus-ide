using System.Text.Json;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M60's code-change and runtime tools in the catalog (D52): every change and everything that runs or touches the
/// program is an action, approved by <c>ai.agent.ideActions</c>; listing the code actions only reads.
/// </summary>
public sealed class IdeCodeChangeAndRuntimeToolsTests
{
    [Fact]
    public void Changes_and_the_runtime_tools_are_actions_and_listing_code_actions_reads()
    {
        Assert.Equal(["rename_symbol", "get_code_actions", "apply_code_action", "format_document", "replace_in_files"], IdeCodeChangeTools.Tools.Select(t => t.Name));
        Assert.Equal(["apply_code_changes", "profile_start", "profile_stop", "memory_snapshot"], IdeRuntimeTools.Tools.Select(t => t.Name));
        foreach (var tool in IdeCodeChangeTools.Tools.Concat(IdeRuntimeTools.Tools))
        {
            Assert.Equal(tool.Name == "get_code_actions" ? IdeToolKind.Read : IdeToolKind.Action, tool.Kind);
            Assert.Same(tool, IdeTools.Find(tool.Name));
        }
    }

    [Fact]
    public void The_schemas_require_what_each_tool_needs()
    {
        static string[] Required(string name)
        {
            using var schema = JsonDocument.Parse(IdeTools.Find(name)!.Schema);
            return schema.RootElement.TryGetProperty("required", out var required) ? [.. required.EnumerateArray().Select(r => r.GetString()!)] : [];
        }

        Assert.Equal(["newName"], Required("rename_symbol"));
        Assert.Equal(["path", "line"], Required("get_code_actions"));
        Assert.Equal(["path", "line"], Required("apply_code_action"));
        Assert.Equal(["path"], Required("format_document"));
        Assert.Equal(["find", "replace"], Required("replace_in_files"));
        Assert.Empty(Required("apply_code_changes"));
        Assert.Empty(Required("profile_start"));
    }
}
