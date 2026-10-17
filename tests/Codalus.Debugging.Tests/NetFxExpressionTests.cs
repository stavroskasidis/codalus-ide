using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M43b (ADR 0076): the desktop-CLR adapter's C# expression parser and the operators it computes itself, on every OS.
/// </summary>
public sealed class NetFxExpressionTests
{
    [Theory]
    [InlineData("1 + 2 * 3", "Binary(+, 1, Binary(*, 2, 3))")]
    [InlineData("(1 + 2) * 3", "Binary(*, Binary(+, 1, 2), 3)")]
    [InlineData("a.b.c", "Member(Member(Name(a), b), c)")]
    [InlineData("a?.b ?? c", "Binary(??, Member?(Name(a), b), Name(c))")]
    [InlineData("items[0].Name", "Member(Index(Name(items), [0]), Name)")]
    [InlineData("m[1, 2]", "Index(Name(m), [1, 2])")]
    [InlineData("cart.Twice(Count + 1)", "Call(Member(Name(cart), Twice), [Binary(+, Name(Count), 1)])")]
    [InlineData("Math.Max(1, 2)", "Call(Member(Name(Math), Max), [1, 2])")]
    [InlineData("!done && x >= 3 || y != null", "Binary(||, Binary(&&, Unary(!, Name(done)), Binary(>=, Name(x), 3)), Binary(!=, Name(y), null))")]
    [InlineData("a < b == c > d", "Binary(==, Binary(<, Name(a), Name(b)), Binary(>, Name(c), Name(d)))")]
    [InlineData("x ? 1 : y ? 2 : 3", "Conditional(Name(x), 1, Conditional(Name(y), 2, 3))")]
    [InlineData("(int)x", "Cast(int, Name(x))")]
    [InlineData("(int)-x", "Cast(int, Unary(-, Name(x)))")]
    [InlineData("(System.Object)value", "Cast(System.Object, Name(value))")]
    [InlineData("(a) + b", "Binary(+, Name(a), Name(b))")]
    [InlineData("1 << 2 + 1", "Binary(<<, 1, Binary(+, 2, 1))")]
    [InlineData("a & b | c ^ d", "Binary(|, Binary(&, Name(a), Name(b)), Binary(^, Name(c), Name(d)))")]
    [InlineData("-a.b", "Unary(-, Member(Name(a), b))")]
    [InlineData("this.Owner", "Member(Name(this), Owner)")]
    [InlineData("@class", "Name(class)")]
    public void Parses_with_csharp_precedence(string text, string expected) =>
        Assert.Equal(expected, Show(ExpressionParser.Parse(text)));

    [Theory]
    [InlineData("42", 42)]
    [InlineData("0x10", 16)]
    [InlineData("0b101", 5)]
    [InlineData("1_000", 1000)]
    [InlineData("3000000000", 3000000000u)]
    [InlineData("5000000000", 5000000000L)]
    [InlineData("5u", 5u)]
    [InlineData("5L", 5L)]
    [InlineData("5UL", 5UL)]
    [InlineData("1.5", 1.5)]
    [InlineData("1.5f", 1.5f)]
    [InlineData("2e3", 2000.0)]
    [InlineData("'a'", 'a')]
    [InlineData("'\\n'", '\n')]
    [InlineData("\"a\\\"b\\tc\"", "a\"b\tc")]
    [InlineData("@\"C:\\x\"\"y\"", "C:\\x\"y")]
    [InlineData("\"\\u0041\"", "A")]
    [InlineData("true", true)]
    [InlineData("null", null)]
    public void Reads_literals(string text, object? expected)
    {
        var literal = Assert.IsType<LiteralExpr>(ExpressionParser.Parse(text));
        Assert.Equal(expected, literal.Value);
        Assert.Equal(expected?.GetType(), literal.Value?.GetType());
    }

    [Fact]
    public void Reads_decimal_literals() => Assert.Equal(1.25m, Assert.IsType<LiteralExpr>(ExpressionParser.Parse("1.25m")).Value);

    [Theory]
    [InlineData("", "empty")]
    [InlineData("a +", "ends too early")]
    [InlineData("(a", "')' is missing")]
    [InlineData("a b", "Unexpected 'b'")]
    [InlineData("\"open", "isn't closed")]
    [InlineData("'ab'", "one character")]
    [InlineData("new Cart()", "isn't supported")]
    [InlineData("a.", "member name")]
    [InlineData("1(2)", "Only methods can be called")]
    [InlineData("a # b", "Unexpected '#'")]
    public void Rejects_invalid_expressions(string text, string message) =>
        Assert.Contains(message, Assert.Throws<ExpressionException>(() => ExpressionParser.Parse(text)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("+", 1, 2, 3)]
    [InlineData("-", 1, 2L, -1L)]
    [InlineData("*", 3, 1.5, 4.5)]
    [InlineData("/", 7, 2, 3)]
    [InlineData("%", 7, 2, 1)]
    [InlineData("/", 7.0, 2, 3.5)]
    [InlineData("<<", 1, 4, 16)]
    [InlineData("&", 6, 3, 2)]
    [InlineData("|", true, false, true)]
    [InlineData("<", 1, 2.5, true)]
    [InlineData(">=", 'b', 'a', true)]
    [InlineData("==", "a", "a", true)]
    [InlineData("!=", 1, 1L, false)]
    [InlineData("==", null, "a", false)]
    [InlineData("+", 'a', 1, 98)]
    [InlineData("+", 1u, 2u, 3u)]
    [InlineData("+", 1u, -2, -1L)]
    [InlineData("*", 1.5f, 2.0f, 3.0f)]
    public void Computes_binary_operators_with_numeric_promotion(string op, object? left, object? right, object expected)
    {
        var result = ClrEvaluator.Arithmetic(op, left, right);
        Assert.Equal(expected, result);
        Assert.Equal(expected.GetType(), result!.GetType());
    }

    [Fact]
    public void Division_by_zero_and_bad_operands_say_so()
    {
        Assert.Contains("Division by zero", Assert.Throws<ExpressionException>(() => ClrEvaluator.Arithmetic("/", 1, 0)).Message, StringComparison.Ordinal);
        Assert.Contains("can't be applied", Assert.Throws<ExpressionException>(() => ClrEvaluator.Arithmetic("-", "a", 1)).Message, StringComparison.Ordinal);
        Assert.Contains("can't be applied", Assert.Throws<ExpressionException>(() => ClrEvaluator.Arithmetic("&&", true, true)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3.9, "int", 3)]
    [InlineData(-3.9, "long", -3L)]
    [InlineData(300, "byte", (byte)44)]
    [InlineData('A', "int", 65)]
    [InlineData(66, "char", 'B')]
    [InlineData(5, "double", 5.0)]
    [InlineData(-1, "uint", uint.MaxValue)]
    public void Converts_like_csharp_explicit_casts(object value, string type, object expected) =>
        Assert.Equal(expected, ClrEvaluator.ConvertTo(value, type));

    [Theory]
    [InlineData("System.InvalidOperationException", "", false, true)]
    [InlineData("System.InvalidOperationException", "System.InvalidOperationException,System.ArgumentException", false, true)]
    [InlineData("System.FormatException", "System.InvalidOperationException", false, false)]
    [InlineData("System.FormatException", "System.InvalidOperationException", true, true)]
    [InlineData("System.InvalidOperationException", "System.InvalidOperationException", true, false)]
    public void Exception_conditions_match_type_names_as_netcoredbg_does(string type, string list, bool negated, bool breaks) =>
        Assert.Equal(breaks, NetFxDebugSession.BreaksWhenThrown(type, list.Split(',', StringSplitOptions.RemoveEmptyEntries), negated));

    [Theory]
    [InlineData("<total>5__2", "total")]
    [InlineData("value", "value")]
    [InlineData("<>1__state", null)]
    [InlineData("<>t__builder", null)]
    [InlineData("<>4__this", null)]
    public void State_machine_fields_show_as_the_method_variables(string field, string? name) =>
        Assert.Equal(name, NetFxDebugSession.HoistedName(field));

    [Theory]
    [InlineData("<Owner>k__BackingField", "Owner")]
    [InlineData("_items", "_items")]
    [InlineData("<>c__DisplayClass0_0", null)]
    public void Backing_fields_show_as_their_property(string field, string? name) =>
        Assert.Equal(name, ClrTypeSystem.DisplayFieldName(field));

    [Theory]
    [InlineData(new[] { "a b", "c" }, "app.exe \"a b\" c")]
    [InlineData(new[] { "x\"y" }, "app.exe \"x\\\"y\"")]
    [InlineData(new[] { "C:\\dir\\" }, "app.exe C:\\dir\\")]
    [InlineData(new[] { "C:\\my dir\\" }, "app.exe \"C:\\my dir\\\\\"")]
    [InlineData(new[] { "" }, "app.exe \"\"")]
    public void Command_lines_are_quoted_for_windows(string[] arguments, string expected) =>
        Assert.Equal(expected, DesktopClr.CommandLine("app.exe", arguments));

    private static string Show(Expr expression) => expression switch
    {
        LiteralExpr { Value: null } => "null",
        LiteralExpr literal => Convert.ToString(literal.Value, System.Globalization.CultureInfo.InvariantCulture)!,
        NameExpr name => $"Name({name.Name})",
        MemberExpr member => $"Member{(member.Conditional ? "?" : string.Empty)}({Show(member.Target)}, {member.Name})",
        IndexExpr index => $"Index({Show(index.Target)}, [{string.Join(", ", index.Arguments.Select(Show))}])",
        CallExpr call => $"Call({Show(call.Callee)}, [{string.Join(", ", call.Arguments.Select(Show))}])",
        UnaryExpr unary => $"Unary({unary.Operator}, {Show(unary.Operand)})",
        BinaryExpr binary => $"Binary({binary.Operator}, {Show(binary.Left)}, {Show(binary.Right)})",
        ConditionalExpr conditional => $"Conditional({Show(conditional.Condition)}, {Show(conditional.WhenTrue)}, {Show(conditional.WhenFalse)})",
        CastExpr cast => $"Cast({cast.TypeName}, {Show(cast.Operand)})",
        _ => "?",
    };
}
