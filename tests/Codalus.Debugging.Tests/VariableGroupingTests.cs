using System.Collections.ObjectModel;
using Codalus.Debugging.Companion;
using Codalus.Debugging.Host;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The data tip's grouping (D44, decision 3), on netcoredbg's recorded answers (<c>Recorded/datatip-*.json</c>) and the
/// companion's member list read from metadata: your own type's non-public members inline with the lock, a library's
/// under Non-Public members, Static members last; indexers out; a getter that threw; no grouping without the list.
/// </summary>
public sealed class VariableGroupingTests : IDisposable
{
    private readonly TypeAttributes _attributes = new();

    /// <summary>The Debuggee's <c>Inventory</c>, as the test assembly's own code; Collection&lt;T&gt; is a library's.</summary>
    private static string[] Modules => [typeof(Debuggee.Inventory).Assembly.Location, typeof(Collection<>).Assembly.Location, typeof(object).Assembly.Location];

    private static bool IsUserModule(string? path) => path == typeof(Debuggee.Inventory).Assembly.Location;

    public void Dispose() => _attributes.Dispose();

    [Fact]
    public async Task Your_own_private_members_are_inline_and_a_librarys_go_under_non_public_members()
    {
        var recorded = RecordedVariables.Read("datatip-net11.0");
        var inventory = recorded.Local("inventory");
        var members = _attributes.MembersOf("Debuggee.Inventory", Modules);
        Assert.NotNull(members);
        var rows = VariableGrouping.Group(await recorded.VariablesAsync(inventory.VariablesReference), members, IsUserModule);

        // Inline, in name order: the public members, and logger (private, your code) with the lock.
        Assert.Equal(["Count", "logger", "Session", "Size", "Non-Public members", "Static members"], rows.Select(r => r.Variable.Name));
        var logger = rows[1];
        Assert.True(logger is { Kind: MemberKind.Field, IsNonPublic: true, IsUserCode: true, Access: "private", DeclaringType: "Debuggee.Inventory", CanWrite: false });
        Assert.Equal((MemberKind.Property, false), (rows[3].Kind, rows[3].CanWrite)); // Size: no setter

        // Session's getter threw: netcoredbg shows the exception; the member's declared type is string.
        var session = rows[2];
        Assert.True(session.Threw);
        Assert.Equal("string", session.DeclaredType);

        // Collection<string>'s own: items, Items, the explicit implementations; the indexer (Item) isn't listed.
        var hidden = rows[4];
        Assert.Equal(MemberKind.NonPublicGroup, hidden.Kind);
        Assert.Contains(hidden.Children!, r => r is { Variable.Name: "items", Kind: MemberKind.Field, IsUserCode: false, Access: "private" });
        Assert.Contains(hidden.Children!, r => r is { Variable.Name: "Items", Kind: MemberKind.Property, Access: "protected" });
        Assert.Contains(hidden.Children!, r => r.Variable.Name == "System.Collections.ICollection.IsSynchronized");
        Assert.DoesNotContain(rows.Concat(hidden.Children!), r => r.Variable.Name is "Item" or "System.Collections.IList.Item");
        Assert.Equal(hidden.Children!.Select(r => r.Variable.Name).Order(StringComparer.OrdinalIgnoreCase), hidden.Children!.Select(r => r.Variable.Name));
        Assert.Equal(MemberKind.StaticGroup, rows[5].Kind);
    }

    [Fact]
    public async Task Without_the_member_list_nothing_is_grouped_and_kinds_come_from_the_names()
    {
        // A remote program: its modules aren't here, so the companion has no list (decision 15).
        var recorded = RecordedVariables.Read("datatip-net11.0");
        var variables = await recorded.VariablesAsync(recorded.Local("inventory").VariablesReference);
        Assert.Null(_attributes.MembersOf("Debuggee.Inventory", ["/remote/app/Debuggee.dll"]));
        var rows = VariableGrouping.Group(variables, null, IsUserModule);
        Assert.Equal(variables.Select(v => v.Name), rows.Select(r => r.Variable.Name)); // netcoredbg's order, nothing moved
        Assert.Equal(MemberKind.Field, rows.Single(r => r.Variable.Name == "logger").Kind);
        Assert.Equal(MemberKind.Field, rows.Single(r => r.Variable.Name == "items").Kind);
        Assert.Equal(MemberKind.Property, rows.Single(r => r.Variable.Name == "Size").Kind);
        Assert.Equal(MemberKind.StaticGroup, rows[^1].Kind);
        Assert.DoesNotContain(rows, r => r.IsNonPublic || r.Threw);
    }

    [Fact]
    public void A_hidden_members_duplicate_is_matched_by_its_owner_and_netcoredbgs_error_is_marked()
    {
        IReadOnlyList<TypeMember> members =
        [
            new("Name", "property", "public", false, "App.Derived", "/app/App.dll", true, "string"),
            new("Name", "property", "public", false, "Lib.Base", "/lib/Lib.dll", false, "string"),
            new("Error", "property", "public", false, "App.Derived", "/app/App.dll", false, "System.Exception"),
            new("Slow", "property", "public", false, "App.Derived", "/app/App.dll", false, "int"),
        ];
        var rows = VariableGrouping.Group(
            [
                new Variable("Name", "\"derived\"", "string", 0, "o.Name"),
                new Variable("Name (Base)", "\"base\"", "string", 0, null),
                new Variable("Error", "{System.InvalidOperationException}", "System.InvalidOperationException", 7, "o.Error"),
                new Variable("Slow", "<error>", "int", 0, "o.Slow"),
            ],
            members,
            p => p == "/app/App.dll");
        var duplicate = rows.Single(r => r.Variable.Name == "Name (Base)");
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal((false, "Lib.Base"), (duplicate.CanWrite, duplicate.DeclaringType));
        Assert.False(rows.Single(r => r.Variable.Name == "Error").Threw); // declared as an exception: it holds one
        Assert.True(rows.Single(r => r.Variable.Name == "Slow").CouldNotEvaluate);
    }

    [Theory]
    [InlineData("System.InvalidOperationException", true)]
    [InlineData("Exception", true)]
    [InlineData("App.ValidationException<int>", true)]
    [InlineData("System.Collections.Generic.List<System.Exception>", false)]
    [InlineData("string", false)]
    [InlineData(null, false)]
    public void Exception_types_are_told_by_their_names(string? type, bool expected) => Assert.Equal(expected, VariableGrouping.IsException(type));

    [Fact]
    public void The_member_list_follows_generic_base_types_and_says_what_can_be_set()
    {
        var members = _attributes.MembersOf("System.Collections.ObjectModel.ObservableCollection<int>", [typeof(System.Collections.ObjectModel.ObservableCollection<>).Assembly.Location, typeof(object).Assembly.Location]);
        Assert.NotNull(members);
        Assert.Contains(members, m => m is { Name: "CollectionChanged", Kind: "event" or "field" });
        Assert.Contains(members, m => m is { Name: "items", DeclaringType: "System.Collections.ObjectModel.Collection`1", Access: "private" }); // Collection<T>, through ObservableCollection<T>'s generic base
        Assert.Contains(members, m => m is { Name: "Count", Kind: "property", Access: "public", CanWrite: false });
        Assert.Contains(members, m => m is { Name: "Item", Kind: "indexer" });
    }
}
