using System.Globalization;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>An evaluation's result: a value computed here, a value in the debuggee, a type or a namespace (for <c>System.Math.Max</c>).</summary>
internal abstract record EvalValue;

internal sealed record LocalValue(object? Value) : EvalValue;

internal sealed record RemoteValue(IClrValue Value) : EvalValue;

internal sealed record TypeValue(IClrModule Module, int Token) : EvalValue;

internal sealed record NamespaceValue(string Name) : EvalValue;

/// <summary>What a frame offers an expression: its variables by name, <c>this</c>, and the method's type and namespace.</summary>
internal sealed record ClrFrameContext(
    IClrFrame Frame,
    IReadOnlyList<(string Name, Func<IClrValue?> Read)> Variables,
    Func<IClrValue?>? This,
    (IClrModule Module, int Token)? DeclaringType,
    string? Namespace);

/// <summary>
/// Evaluates <see cref="Expr"/>essions in a stopped frame (M43b, ADR 0076): names (locals, arguments, <c>this</c>'s
/// members, static members of the method's types, types and namespaces), member access (fields, properties through
/// their getters), indexers (arrays, strings, <c>this[...]</c> through <c>get_Item</c>), method calls with overloads
/// picked by their arguments (func-eval), and C#'s operators on primitives and strings, computed here.
/// </summary>
internal sealed class ClrEvaluator(ClrTypeSystem types, IClrProcess process, ClrFrameContext context, TimeSpan timeout)
{
    private static readonly string[] s_numeric = ["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "char", "float", "double", "decimal"];

    private static readonly Dictionary<string, string[]> s_widening = new(StringComparer.Ordinal)
    {
        ["sbyte"] = ["short", "int", "long", "float", "double", "decimal"],
        ["byte"] = ["short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal"],
        ["short"] = ["int", "long", "float", "double", "decimal"],
        ["ushort"] = ["int", "uint", "long", "ulong", "float", "double", "decimal"],
        ["int"] = ["long", "float", "double", "decimal"],
        ["uint"] = ["long", "ulong", "float", "double", "decimal"],
        ["long"] = ["float", "double", "decimal"],
        ["ulong"] = ["float", "double", "decimal"],
        ["char"] = ["ushort", "int", "uint", "long", "ulong", "float", "double", "decimal"],
        ["float"] = ["double"],
    };

    private IClrThread Thread => context.Frame.Thread;

    public EvalValue Evaluate(string text) => Evaluate(ExpressionParser.Parse(text));

    public EvalValue Evaluate(Expr expression) => expression switch
    {
        LiteralExpr literal => new LocalValue(literal.Value),
        NameExpr name => Name(name.Name),
        MemberExpr member => Member(member),
        IndexExpr index => Index(index),
        CallExpr call => Call(call),
        UnaryExpr unary => Unary(unary.Operator, Primitive(Evaluate(unary.Operand), unary.Operator)),
        BinaryExpr binary => Binary(binary),
        ConditionalExpr conditional => Truth(Evaluate(conditional.Condition)) ? Evaluate(conditional.WhenTrue) : Evaluate(conditional.WhenFalse),
        CastExpr cast => Cast(cast.TypeName, Evaluate(cast.Operand)),
        _ => throw new ExpressionException("The expression isn't supported."),
    };

    /// <summary>A condition's value (breakpoint conditions).</summary>
    public bool IsTrue(string text) => Truth(Evaluate(text));

    // ------------------------------------------------------------------ names

    private EvalValue Name(string name)
    {
        if (name == "this")
        {
            return context.This?.Invoke() is { } self ? new RemoteValue(self) : throw new ExpressionException("'this' isn't available in a static method.");
        }

        foreach (var (variable, read) in context.Variables)
        {
            if (variable == name)
            {
                return read() is { } value ? new RemoteValue(value) : throw new ExpressionException($"'{name}' isn't available here (optimized code).");
            }
        }

        if (context.This?.Invoke() is { Kind: ClrValueKind.Object } self2 && TryInstanceMember(self2, name) is { } member)
        {
            return member;
        }

        if (context.DeclaringType is { } declaring)
        {
            // Static members of the method's type and the types it's nested in.
            for (var type = ((IClrModule Module, int Token)?)declaring; type is { } t; type = OuterType(t))
            {
                if (TryStaticMember(t.Module, t.Token, name) is { } staticMember)
                {
                    return staticMember;
                }
            }
        }

        if (types.FindTypeByName(name, context.Namespace) is { } found)
        {
            return new TypeValue(found.Module, found.Token);
        }

        if (types.Modules.Any(m => m.Info.Metadata.TypeDefinitions.Any(h => m.Info.Metadata.GetString(m.Info.Metadata.GetTypeDefinition(h).Namespace) is var ns && (ns == name || ns.StartsWith(name + ".", StringComparison.Ordinal)))))
        {
            return new NamespaceValue(name);
        }

        throw new ExpressionException($"The name '{name}' doesn't exist in the current context.");
    }

    private (IClrModule Module, int Token)? OuterType((IClrModule Module, int Token) type)
    {
        var info = types.Info(type.Module);
        var full = info?.TypeFullName(type.Token);
        var plus = full?.LastIndexOf('+') ?? -1;
        return plus > 0 && info!.FindType(full![..plus]) is var outer and not 0 ? (type.Module, outer) : null;
    }

    // ------------------------------------------------------------------ members

    private EvalValue Member(MemberExpr member)
    {
        var target = Evaluate(member.Target);
        switch (target)
        {
            case NamespaceValue ns:
                var full = ns.Name + "." + member.Name;
                return types.FindType(full) is { } type ? new TypeValue(type.Module, type.Token) : new NamespaceValue(full);
            case TypeValue typeValue:
                return TryStaticMember(typeValue.Module, typeValue.Token, member.Name)
                    ?? throw new ExpressionException($"'{member.Name}' isn't a static member of {types.Name(ClrTypeSystem.TypeOf(typeValue.Module, typeValue.Token))}.");
        }

        if (IsNull(target))
        {
            return member.Conditional ? new LocalValue(null) : throw new ExpressionException($"The value is null, so '{member.Name}' can't be read.");
        }

        switch (target)
        {
            case LocalValue { Value: string s } when member.Name == "Length":
                return new LocalValue(s.Length);
            case LocalValue local:
                throw new ExpressionException($"'{member.Name}' can't be read from {ClrTypeSystem.FormatPrimitive(local.Value)} here.");
        }

        var value = ((RemoteValue)target).Value;
        switch (value.Kind)
        {
            case ClrValueKind.String when member.Name == "Length":
                return new LocalValue(value.Text?.Length ?? 0);
            case ClrValueKind.Array when member.Name is "Length" or "LongLength":
                return new LocalValue(member.Name == "Length" ? value.Length : (object)(long)value.Length);
            case ClrValueKind.Array when member.Name == "Rank":
                return new LocalValue(value.Dimensions.Count);
        }

        return TryInstanceMember(value, member.Name) ?? throw new ExpressionException($"'{member.Name}' isn't a member of {types.Name(value.Type)}.");
    }

    /// <summary>An instance field (an auto-property by its name) or property of a value's type or its bases.</summary>
    private RemoteValue? TryInstanceMember(IClrValue value, string name)
    {
        foreach (var (module, token, info) in types.Chain(value.Type))
        {
            if (info.Fields(token).FirstOrDefault(f => !f.IsStatic && (f.Name == name || ClrTypeSystem.DisplayFieldName(f.Name) == name)) is { } field)
            {
                return new RemoteValue(value.Field(module, token, field.Token));
            }

            if (info.Properties(token).FirstOrDefault(p => p.Name == name && !p.IsStatic && p.IndexParameters == 0 && p.GetterToken != 0) is { } property)
            {
                return new RemoteValue(Invoke(module, property.GetterToken, value.Type.TypeArguments, [value], name));
            }
        }

        return null;
    }

    private EvalValue? TryStaticMember(IClrModule module, int token, string name)
    {
        var info = types.Info(module);
        if (info is null)
        {
            return null;
        }

        for (var current = ((IClrModule Module, int Token, ClrModuleInfo Info)?)(module, token, info); current is { } c;)
        {
            if (c.Info.Fields(c.Token).FirstOrDefault(f => f.IsStatic && f.Name == name) is { } field)
            {
                return field.IsLiteral ? new LocalValue(field.Constant) : new RemoteValue(process.GetStaticField(Thread, ClrTypeSystem.TypeOf(c.Module, c.Token), field.Token));
            }

            if (c.Info.Properties(c.Token).FirstOrDefault(p => p.Name == name && p.IsStatic && p.IndexParameters == 0 && p.GetterToken != 0) is { } property)
            {
                return new RemoteValue(Invoke(c.Module, property.GetterToken, [], [], name));
            }

            current = c.Info.BaseType(c.Token) is { } baseName && types.Resolve(c.Module, baseName) is { } resolved && types.Info(resolved.Module) is { } baseInfo
                ? (resolved.Module, resolved.Token, baseInfo)
                : null;
        }

        return info.FindType(info.TypeFullName(token) + "+" + name) is var nested and not 0 ? new TypeValue(module, nested) : null;
    }

    // ------------------------------------------------------------------ indexers

    private EvalValue Index(IndexExpr index)
    {
        var target = Evaluate(index.Target);
        if (IsNull(target))
        {
            throw new ExpressionException("The value is null, so it can't be indexed.");
        }

        var arguments = index.Arguments.Select(Evaluate).ToList();
        if (target is LocalValue { Value: string s })
        {
            return new LocalValue(s[CheckedIndex(Integer(arguments.Single()), s.Length)]);
        }

        if (target is not RemoteValue { Value: var value })
        {
            throw new ExpressionException("Only arrays, strings and objects with indexers can be indexed.");
        }

        switch (value.Kind)
        {
            case ClrValueKind.String:
                var text = value.Text ?? string.Empty;
                return new LocalValue(text[CheckedIndex(Integer(arguments.Single()), text.Length)]);
            case ClrValueKind.Array:
                var dimensions = value.Dimensions.Count == 0 ? [value.Length] : value.Dimensions;
                if (arguments.Count != dimensions.Count)
                {
                    throw new ExpressionException($"The array has {dimensions.Count} dimension(s), not {arguments.Count}.");
                }

                var flat = 0;
                for (var i = 0; i < arguments.Count; i++)
                {
                    flat = (flat * dimensions[i]) + CheckedIndex(Integer(arguments[i]), dimensions[i]);
                }

                return new RemoteValue(value.Element(flat));
            case ClrValueKind.Object:
                return new RemoteValue(CallMethod(value, value.Type, "get_Item", arguments, "this[]"));
            default:
                throw new ExpressionException("Only arrays, strings and objects with indexers can be indexed.");
        }
    }

    private static int CheckedIndex(long index, int length) =>
        index >= 0 && index < length ? (int)index : throw new ExpressionException($"The index {index.ToString(CultureInfo.InvariantCulture)} is outside the bounds (the length is {length.ToString(CultureInfo.InvariantCulture)}).");

    // ------------------------------------------------------------------ calls

    private EvalValue Call(CallExpr call)
    {
        var arguments = call.Arguments.Select(Evaluate).ToList();
        switch (call.Callee)
        {
            case NameExpr { Name: var name }:
                if (context.This?.Invoke() is { Kind: ClrValueKind.Object } self && TryFindMethod(self.Type, name, arguments, instance: true) is not null)
                {
                    return new RemoteValue(CallMethod(self, self.Type, name, arguments, name));
                }

                if (context.DeclaringType is { } declaring)
                {
                    return new RemoteValue(CallStatic(declaring.Module, declaring.Token, name, arguments));
                }

                throw new ExpressionException($"The name '{name}' doesn't exist in the current context.");
            case MemberExpr member:
                var target = Evaluate(member.Target);
                switch (target)
                {
                    case TypeValue type:
                        return new RemoteValue(CallStatic(type.Module, type.Token, member.Name, arguments));
                    case NamespaceValue ns:
                        throw new ExpressionException($"'{ns.Name}.{member.Name}' isn't a method.");
                }

                if (IsNull(target))
                {
                    return member.Conditional ? new LocalValue(null) : throw new ExpressionException($"The value is null, so '{member.Name}' can't be called.");
                }

                if (target is LocalValue local)
                {
                    if (member.Name == "ToString" && arguments.Count == 0)
                    {
                        return new LocalValue(local.Value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : local.Value?.ToString());
                    }

                    target = new RemoteValue(ToRemote(local, null));
                }

                var receiver = ((RemoteValue)target).Value;
                return new RemoteValue(CallMethod(receiver, receiver.Type, member.Name, arguments, member.Name));
            default:
                throw new ExpressionException("Only methods can be called.");
        }
    }

    private IClrValue CallMethod(IClrValue receiver, ClrType type, string name, IReadOnlyList<EvalValue> arguments, string display)
    {
        var (module, method) = TryFindMethod(type, name, arguments, instance: true)
            ?? throw new ExpressionException($"{types.Name(type)} has no method '{display}' that takes {Describe(arguments)}.");
        return Invoke(module, method.Token, type.TypeArguments, [receiver, .. Arguments(method, arguments)], display);
    }

    private IClrValue CallStatic(IClrModule module, int token, string name, IReadOnlyList<EvalValue> arguments)
    {
        var type = ClrTypeSystem.TypeOf(module, token);
        var (found, method) = TryFindMethod(type, name, arguments, instance: false)
            ?? throw new ExpressionException($"{types.Name(type)} has no static method '{name}' that takes {Describe(arguments)}.");
        return Invoke(found, method.Token, [], Arguments(method, arguments), name);
    }

    /// <summary>The best overload named <paramref name="name"/> in a type or its bases for the arguments; null when none fits.</summary>
    private (IClrModule Module, ClrMethodInfo Method)? TryFindMethod(ClrType type, string name, IReadOnlyList<EvalValue> arguments, bool instance)
    {
        (IClrModule Module, ClrMethodInfo Method)? best = null;
        var bestScore = -1;
        foreach (var (module, token, info) in types.Chain(type))
        {
            foreach (var method in info.Methods(token).Where(m => m.Name == name && m.IsStatic != instance && m.ParameterTypes.Count == arguments.Count && m.GenericParameters == 0))
            {
                var score = 0;
                for (var i = 0; i < arguments.Count && score >= 0; i++)
                {
                    var fit = Fit(arguments[i], method.ParameterTypes[i]);
                    score = fit < 0 ? -1 : score + fit;
                }

                if (score > bestScore)
                {
                    (best, bestScore) = ((module, method), score);
                }
            }

            if (best is not null)
            {
                return best; // overloads in a derived type hide the base's, as in C#
            }
        }

        return null;
    }

    /// <summary>How well an argument fits a parameter type: 3 exact, 2 an implicit conversion, 1 object or a type parameter, -1 not at all.</summary>
    private int Fit(EvalValue argument, string parameter)
    {
        if (parameter.StartsWith('!'))
        {
            return 1;
        }

        if (parameter.StartsWith("ref ", StringComparison.Ordinal))
        {
            return -1;
        }

        var argumentType = TypeKeyword(argument);
        if (argumentType is null)
        {
            // null: any reference type.
            return parameter is "int" or "uint" or "long" or "ulong" or "short" or "ushort" or "byte" or "sbyte" or "bool" or "char" or "float" or "double" or "decimal" ? -1 : 1;
        }

        if (argumentType == parameter)
        {
            return 3;
        }

        if (s_widening.TryGetValue(argumentType, out var wider) && wider.Contains(parameter))
        {
            return 2;
        }

        if (parameter == "object")
        {
            return argument is RemoteValue { Value.IsValueType: false } or LocalValue { Value: string } || (argument is RemoteValue r && r.Value.Kind is ClrValueKind.String or ClrValueKind.Array) ? 1 : -1;
        }

        if (argument is RemoteValue { Value: { Kind: ClrValueKind.Object or ClrValueKind.Array } remote } && !IsPrimitiveKeyword(parameter))
        {
            var generic = parameter.IndexOf('<', StringComparison.Ordinal) is var lt and > 0 ? parameter[..lt] : parameter;
            return types.Chain(remote.Type).Any(c => ClrModuleInfo.DisplayName(c.Info.TypeFullName(c.Token)) == generic) ? 2 : remote.Kind == ClrValueKind.Array && parameter.EndsWith("[]", StringComparison.Ordinal) ? 2 : 0;
        }

        return -1;
    }

    private static bool IsPrimitiveKeyword(string type) => s_numeric.Contains(type) || type is "bool" or "string";

    /// <summary>The C# keyword of an argument's type (<c>int</c>, <c>string</c>), its type's name for objects, null for null.</summary>
    private string? TypeKeyword(EvalValue value) => value switch
    {
        LocalValue { Value: null } => null,
        LocalValue local => Keyword(local.Value!),
        RemoteValue { Value.Kind: ClrValueKind.Null } => null,
        RemoteValue { Value.Kind: ClrValueKind.String } => "string",
        RemoteValue { Value: { Kind: ClrValueKind.Primitive } p } => p.Primitive is { } o ? Keyword(o) : null,
        RemoteValue r => types.Name(r.Value.Type),
        _ => "?",
    };

    private static string Keyword(object value) => value switch
    {
        bool => "bool",
        char => "char",
        sbyte => "sbyte",
        byte => "byte",
        short => "short",
        ushort => "ushort",
        int => "int",
        uint => "uint",
        long => "long",
        ulong => "ulong",
        float => "float",
        double => "double",
        decimal => "decimal",
        string => "string",
        _ => value.GetType().FullName ?? "?",
    };

    private static string Describe(IReadOnlyList<EvalValue> arguments) =>
        arguments.Count switch { 0 => "no arguments", 1 => "1 argument", _ => arguments.Count.ToString(CultureInfo.InvariantCulture) + " arguments" };

    /// <summary>The arguments as the debuggee's values, converted to the parameters' primitive types.</summary>
    private List<IClrValue> Arguments(ClrMethodInfo method, IReadOnlyList<EvalValue> arguments)
    {
        var result = new List<IClrValue>();
        for (var i = 0; i < arguments.Count; i++)
        {
            result.Add(ToRemote(arguments[i], method.ParameterTypes[i]));
        }

        return result;
    }

    private IClrValue ToRemote(EvalValue value, string? parameter)
    {
        switch (value)
        {
            case RemoteValue { Value: { Kind: ClrValueKind.Primitive } primitive } when parameter is not null && s_numeric.Contains(parameter) && primitive.Primitive is { } raw && Keyword(raw) != parameter:
                return process.NewPrimitive(Thread, ConvertTo(raw, parameter));
            case RemoteValue remote:
                return remote.Value;
            case LocalValue { Value: string s }:
                return process.NewString(Thread, s, timeout);
            case LocalValue { Value: null }:
                throw new ExpressionException("null can't be passed as an argument here.");
            case LocalValue { Value: decimal }:
                throw new ExpressionException("Decimal values can't be passed as arguments here.");
            case LocalValue local:
                if (parameter == "object")
                {
                    throw new ExpressionException("A number can't be passed where an object is expected here (it would need boxing).");
                }

                return process.NewPrimitive(Thread, parameter is not null && s_numeric.Contains(parameter) ? ConvertTo(local.Value!, parameter) : local.Value!);
            default:
                throw new ExpressionException("A type or a namespace isn't a value.");
        }
    }

    private IClrValue Invoke(IClrModule module, int methodToken, IReadOnlyList<ClrType> typeArguments, IReadOnlyList<IClrValue> arguments, string display)
    {
        try
        {
            return process.Call(Thread, module, methodToken, typeArguments, arguments, timeout);
        }
        catch (ClrEvalException ex)
        {
            throw new ExpressionException($"{display} threw {types.FullName(ex.Exception.Type)}" + (types.Field(ex.Exception, "_message") is { Kind: ClrValueKind.String } message ? ": " + message.Text : "."));
        }
        catch (TimeoutException)
        {
            throw new ExpressionException($"Evaluating {display} timed out after {timeout.TotalSeconds:0} seconds.");
        }
    }

    // ------------------------------------------------------------------ operators

    private static bool IsNull(EvalValue value) => value is LocalValue { Value: null } or RemoteValue { Value.Kind: ClrValueKind.Null };

    /// <summary>A value as a primitive computed here: numbers, bools, chars, strings and null; enums as their numbers.</summary>
    private object? Primitive(EvalValue value, string what)
    {
        switch (value)
        {
            case LocalValue local:
                return local.Value;
            case RemoteValue { Value: var remote }:
                switch (remote.Kind)
                {
                    case ClrValueKind.Null:
                        return null;
                    case ClrValueKind.Primitive:
                        return remote.Primitive;
                    case ClrValueKind.String:
                        return remote.Text;
                    case ClrValueKind.Object when types.Unboxed(remote) is { } unboxed:
                        return unboxed.Primitive;
                    case ClrValueKind.Object when types.Definition(remote.Type) is { } d && types.Info(d.Module) is { } info && info.IsEnum(d.Token):
                        return info.Fields(d.Token).FirstOrDefault(f => !f.IsStatic) is { } field ? remote.Field(d.Module, d.Token, field.Token).Primitive : null;
                    case ClrValueKind.Object when types.FullName(remote.Type) == "System.Decimal" && types.Format(remote) is var text && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2):
                        return d2;
                }

                break;
        }

        throw new ExpressionException($"'{what}' can't be applied to {Describe(value)}.");
    }

    private string Describe(EvalValue value) => value switch
    {
        RemoteValue r => types.Name(r.Value.Type),
        TypeValue t => "the type " + types.Name(ClrTypeSystem.TypeOf(t.Module, t.Token)),
        NamespaceValue n => "the namespace " + n.Name,
        LocalValue l => l.Value is null ? "null" : Keyword(l.Value),
        _ => "the value",
    };

    private bool Truth(EvalValue value) => Primitive(value, "a condition") is bool b ? b : throw new ExpressionException($"The condition isn't a bool ({Describe(value)}).");

    private static long Integer(EvalValue value) => value switch
    {
        LocalValue { Value: var v } when IsIntegral(v) => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        RemoteValue { Value: { Kind: ClrValueKind.Primitive, Primitive: var p } } when IsIntegral(p) => Convert.ToInt64(p, CultureInfo.InvariantCulture),
        _ => throw new ExpressionException("An index must be an integer."),
    };

    private static bool IsIntegral(object? value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or char;

    private static LocalValue Unary(string op, object? operand) => (op, operand) switch
    {
        ("!", bool b) => new LocalValue(!b),
        ("-", double d) => new LocalValue(-d),
        ("-", float f) => new LocalValue(-f),
        ("-", decimal m) => new LocalValue(-m),
        ("-", long l) => new LocalValue(-l),
        ("-", ulong) => throw new ExpressionException("'-' can't be applied to a ulong."),
        ("-", uint u) => new LocalValue(-(long)u),
        ("-", _) when IsIntegral(operand) => new LocalValue(-Convert.ToInt32(operand, CultureInfo.InvariantCulture)),
        ("+", _) when IsIntegral(operand) || operand is float or double or decimal => new LocalValue(operand is char c ? (int)c : operand),
        ("~", long l) => new LocalValue(~l),
        ("~", ulong u) => new LocalValue(~u),
        ("~", uint u) => new LocalValue(~u),
        ("~", _) when IsIntegral(operand) => new LocalValue(~Convert.ToInt32(operand, CultureInfo.InvariantCulture)),
        _ => throw new ExpressionException($"'{op}' can't be applied to {(operand is null ? "null" : Keyword(operand))}."),
    };

    private EvalValue Binary(BinaryExpr binary)
    {
        switch (binary.Operator)
        {
            case "&&":
                return new LocalValue(Truth(Evaluate(binary.Left)) && Truth(Evaluate(binary.Right)));
            case "||":
                return new LocalValue(Truth(Evaluate(binary.Left)) || Truth(Evaluate(binary.Right)));
            case "??":
                var left = Evaluate(binary.Left);
                return IsNull(left) ? Evaluate(binary.Right) : left;
        }

        var leftValue = Evaluate(binary.Left);
        var rightValue = Evaluate(binary.Right);
        if (binary.Operator is "==" or "!=" && ReferenceEquality(leftValue, rightValue) is { } same)
        {
            return new LocalValue(binary.Operator == "==" ? same : !same);
        }

        var a = Primitive(leftValue, binary.Operator);
        var b = Primitive(rightValue, binary.Operator);
        if (binary.Operator == "+" && (a is string || b is string))
        {
            return new LocalValue(Text(leftValue, a) + Text(rightValue, b));
        }

        return new LocalValue(Arithmetic(binary.Operator, a, b));
    }

    /// <summary>== between objects, or an object and null, by reference; null when it's a comparison of values (strings, numbers).</summary>
    private static bool? ReferenceEquality(EvalValue left, EvalValue right)
    {
        static bool IsReference(EvalValue v) => v is RemoteValue { Value: { Kind: ClrValueKind.Object or ClrValueKind.Array, IsValueType: false } };
        if (IsReference(left) && IsReference(right))
        {
            return ((RemoteValue)left).Value.Address == ((RemoteValue)right).Value.Address;
        }

        if ((IsReference(left) && IsNull(right)) || (IsNull(left) && IsReference(right)))
        {
            return false;
        }

        return null;
    }

    private string Text(EvalValue value, object? primitive) => primitive switch
    {
        string s => s,
        null => value is RemoteValue { Value.Kind: ClrValueKind.Object } r ? Format(r.Value) : string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => primitive.ToString() ?? string.Empty,
    };

    /// <summary>An object's text for <c>+</c> with a string: its ToString() in the debuggee.</summary>
    private string Format(IClrValue value)
    {
        var result = CallMethod(value, value.Type, "ToString", [], "ToString()");
        return result.Text ?? string.Empty;
    }

    internal static object? Arithmetic(string op, object? a, object? b)
    {
        if (op is "==" or "!=")
        {
            bool equal;
            if (a is null || b is null || a is string || b is string || a is bool || b is bool)
            {
                equal = Equals(a, b);
            }
            else
            {
                equal = Compare(a, b) == 0;
            }

            return op == "==" ? equal : !equal;
        }

        if (a is bool x && b is bool y)
        {
            return op switch
            {
                "&" => x & y,
                "|" => x | y,
                "^" => x ^ y,
                _ => throw new ExpressionException($"'{op}' can't be applied to bools."),
            };
        }

        if (a is null || b is null || a is string || b is string || a is bool || b is bool)
        {
            throw new ExpressionException($"'{op}' can't be applied to {Name(a)} and {Name(b)}.");
        }

        if (op is "<" or ">" or "<=" or ">=")
        {
            var c = Compare(a, b);
            return op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 };
        }

        if (op is "<<" or ">>")
        {
            var count = Convert.ToInt32(b, CultureInfo.InvariantCulture);
            return a switch
            {
                long l => op == "<<" ? l << count : l >> count,
                ulong u => op == "<<" ? u << count : u >> count,
                uint u => op == "<<" ? u << count : u >> count,
                _ when IsIntegral(a) => op == "<<" ? Convert.ToInt32(a, CultureInfo.InvariantCulture) << count : Convert.ToInt32(a, CultureInfo.InvariantCulture) >> count,
                _ => throw new ExpressionException($"'{op}' needs an integer."),
            };
        }

        switch (Promote(a, b))
        {
            case "decimal":
                var dm = Convert.ToDecimal(a, CultureInfo.InvariantCulture);
                var dn = Convert.ToDecimal(b, CultureInfo.InvariantCulture);
                return op switch
                {
                    "+" => dm + dn,
                    "-" => dm - dn,
                    "*" => dm * dn,
                    "/" => dn == 0 ? throw new ExpressionException("Division by zero.") : dm / dn,
                    "%" => dn == 0 ? throw new ExpressionException("Division by zero.") : dm % dn,
                    _ => throw new ExpressionException($"'{op}' can't be applied to decimals."),
                };
            case "double" or "float":
                var dx = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                var dy = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                double result = op switch
                {
                    "+" => dx + dy,
                    "-" => dx - dy,
                    "*" => dx * dy,
                    "/" => dx / dy,
                    "%" => dx % dy,
                    _ => throw new ExpressionException($"'{op}' can't be applied to floating-point numbers."),
                };
                return Promote(a, b) == "float" ? (object)(float)result : result;
            case "ulong":
                var ux = Convert.ToUInt64(a, CultureInfo.InvariantCulture);
                var uy = Convert.ToUInt64(b, CultureInfo.InvariantCulture);
                return IntegerOp(op, ux, uy);
            case "long":
                var lx = Convert.ToInt64(a, CultureInfo.InvariantCulture);
                var ly = Convert.ToInt64(b, CultureInfo.InvariantCulture);
                return IntegerOp(op, lx, ly);
            case "uint":
                return (uint)IntegerOp(op, Convert.ToUInt32(a, CultureInfo.InvariantCulture), (ulong)Convert.ToUInt32(b, CultureInfo.InvariantCulture));
            default:
                return (int)IntegerOp(op, Convert.ToInt64(a is char ca ? (int)ca : a, CultureInfo.InvariantCulture), Convert.ToInt64(b is char cb ? (int)cb : b, CultureInfo.InvariantCulture));
        }
    }

    private static long IntegerOp(string op, long x, long y) => op switch
    {
        "+" => unchecked(x + y),
        "-" => unchecked(x - y),
        "*" => unchecked(x * y),
        "/" => y == 0 ? throw new ExpressionException("Division by zero.") : x / y,
        "%" => y == 0 ? throw new ExpressionException("Division by zero.") : x % y,
        "&" => x & y,
        "|" => x | y,
        "^" => x ^ y,
        _ => throw new ExpressionException($"'{op}' can't be applied to integers."),
    };

    private static ulong IntegerOp(string op, ulong x, ulong y) => op switch
    {
        "+" => unchecked(x + y),
        "-" => unchecked(x - y),
        "*" => unchecked(x * y),
        "/" => y == 0 ? throw new ExpressionException("Division by zero.") : x / y,
        "%" => y == 0 ? throw new ExpressionException("Division by zero.") : x % y,
        "&" => x & y,
        "|" => x | y,
        "^" => x ^ y,
        _ => throw new ExpressionException($"'{op}' can't be applied to integers."),
    };

    /// <summary>C#'s binary numeric promotion of two operands' types.</summary>
    private static string Promote(object a, object b)
    {
        if (a is decimal || b is decimal)
        {
            return "decimal";
        }

        if (a is double || b is double)
        {
            return "double";
        }

        if (a is float || b is float)
        {
            return "float";
        }

        if (a is ulong || b is ulong)
        {
            return "ulong";
        }

        if (a is long || b is long || (a is uint && b is sbyte or short or int) || (b is uint && a is sbyte or short or int))
        {
            return "long";
        }

        return a is uint || b is uint ? "uint" : "int";
    }

    private static int Compare(object a, object b) => Promote(a, b) switch
    {
        "decimal" => Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture)),
        "double" or "float" => Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture)),
        "ulong" => Convert.ToUInt64(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToUInt64(b, CultureInfo.InvariantCulture)),
        _ => Convert.ToInt64(a is char ca ? (int)ca : a, CultureInfo.InvariantCulture).CompareTo(Convert.ToInt64(b is char cb ? (int)cb : b, CultureInfo.InvariantCulture)),
    };

    private static string Name(object? value) => value is null ? "null" : Keyword(value);

    // ------------------------------------------------------------------ casts

    private EvalValue Cast(string typeName, EvalValue value)
    {
        if (s_numeric.Contains(typeName) || typeName == "bool")
        {
            var primitive = Primitive(value, "(" + typeName + ")");
            return primitive is null ? throw new ExpressionException($"null can't be cast to {typeName}.") : new LocalValue(ConvertTo(primitive, typeName));
        }

        if (typeName is "object" or "string" || IsNull(value) || value is not RemoteValue { Value: var remote })
        {
            return value;
        }

        // A reference conversion: allowed when the value's type is, or derives from, the named type (interfaces aren't checked).
        var target = types.FindTypeByName(typeName, context.Namespace) ?? throw new ExpressionException($"The type '{typeName}' wasn't found.");
        var targetName = types.Info(target.Module)!.TypeFullName(target.Token);
        return types.IsA(remote.Type, targetName) || types.Info(target.Module)!.BaseType(target.Token) is null
            ? value
            : throw new ExpressionException($"{types.Name(remote.Type)} can't be cast to {typeName}.");
    }

    internal static object ConvertTo(object value, string type)
    {
        if (value is char c && type != "char")
        {
            value = (int)c;
        }

        try
        {
            return type switch
            {
                "bool" => value is bool b ? b : throw new ExpressionException("Only a bool converts to bool."),
                "char" => (char)Convert.ToUInt16(value is char ch ? ch : Truncate(value), CultureInfo.InvariantCulture),
                "sbyte" => unchecked((sbyte)Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture)),
                "byte" => unchecked((byte)Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture)),
                "short" => unchecked((short)Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture)),
                "ushort" => unchecked((ushort)Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture)),
                "int" => unchecked((int)Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture)),
                "uint" => unchecked((uint)Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture)),
                "long" => value is ulong ul ? unchecked((long)ul) : Convert.ToInt64(Truncate(value), CultureInfo.InvariantCulture),
                "ulong" => value is long l ? unchecked((ulong)l) : value is int i ? unchecked((ulong)i) : Convert.ToUInt64(Truncate(value), CultureInfo.InvariantCulture),
                "float" => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                "double" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "decimal" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                _ => value,
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            throw new ExpressionException($"{ClrTypeSystem.FormatPrimitive(value)} can't be converted to {type}.");
        }
    }

    /// <summary>A floating-point number truncated toward zero, as C#'s explicit conversions do.</summary>
    private static object Truncate(object value) => value switch
    {
        double d => Math.Truncate(d),
        float f => Math.Truncate(f),
        decimal m => decimal.Truncate(m),
        bool => throw new ExpressionException("A bool doesn't convert to a number."),
        _ => value,
    };
}
