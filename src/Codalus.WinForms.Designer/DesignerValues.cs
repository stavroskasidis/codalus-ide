using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;
using Codalus.LanguageServices.Protocol;

namespace Codalus.WinForms.Designer;

/// <summary>Finds types by the full C# names the model uses, in the framework and the loaded assemblies.</summary>
internal sealed class DesignerTypes
{
    private readonly Dictionary<string, Type?> _cache = new(StringComparer.Ordinal);
    private readonly List<Assembly> _assemblies = [typeof(Control).Assembly, typeof(Point).Assembly, typeof(Color).Assembly, typeof(Component).Assembly, typeof(object).Assembly];

    private static readonly Dictionary<string, Type> s_keywords = new(StringComparer.Ordinal)
    {
        ["object"] = typeof(object), ["string"] = typeof(string), ["bool"] = typeof(bool), ["char"] = typeof(char), ["byte"] = typeof(byte),
        ["short"] = typeof(short), ["int"] = typeof(int), ["long"] = typeof(long), ["float"] = typeof(float), ["double"] = typeof(double),
        ["decimal"] = typeof(decimal), ["uint"] = typeof(uint), ["ulong"] = typeof(ulong), ["sbyte"] = typeof(sbyte), ["ushort"] = typeof(ushort),
    };

    public void Add(Assembly assembly)
    {
        if (!_assemblies.Contains(assembly))
        {
            _assemblies.Insert(0, assembly);
            _cache.Clear();
        }
    }

    /// <summary>The type <paramref name="name"/> names (<c>System.Windows.Forms.Button</c>, <c>Outer.Inner</c>), or null.</summary>
    public Type? Find(string name)
    {
        if (s_keywords.TryGetValue(name, out var keyword))
        {
            return keyword;
        }

        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        Type? found = Type.GetType(name, throwOnError: false);
        foreach (var candidate in Candidates(name))
        {
            if (found is not null)
            {
                break;
            }

            foreach (var assembly in _assemblies.Concat(AppDomain.CurrentDomain.GetAssemblies()))
            {
                if (assembly.GetType(candidate, throwOnError: false) is { } type)
                {
                    found = type;
                    break;
                }
            }
        }

        _cache[name] = found;
        return found;
    }

    /// <summary><c>A.B.C</c> as written, then with the last dots as nested types (<c>A.B+C</c>, <c>A+B+C</c>).</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        var nested = name;
        for (var dot = nested.LastIndexOf('.'); dot > 0; dot = nested.LastIndexOf('.', dot - 1))
        {
            nested = nested[..dot] + "+" + nested[(dot + 1)..];
            yield return nested;
        }
    }

    /// <summary>A type's full C# name (nested types with dots).</summary>
    public static string NameOf(Type type) => (type.FullName ?? type.Name).Replace('+', '.');
}

/// <summary>
/// Turns the model's values into objects (what running the statement would give) and objects back into values (what
/// Visual Studio's CodeDOM serializers write: literals, enum members, <c>InstanceDescriptor</c>s' constructors and
/// static members, colors by name or <c>FromArgb</c>, the class's components by name).
/// </summary>
internal sealed class DesignerValues(DesignerTypes types, Func<string, object?> component, Func<object, string?> nameOf, Func<string, object?> resource)
{
    // ------------------------------------------------------------------ values → objects

    public object? Evaluate(DesignerValue value, Type? target = null)
    {
        var result = value.Kind switch
        {
            DesignerValueKinds.Literal => Literal(value),
            DesignerValueKinds.Enum => Enum(value),
            DesignerValueKinds.Member => Member(value),
            DesignerValueKinds.New => New(value),
            DesignerValueKinds.Call => Call(value),
            DesignerValueKinds.Array => Array(value),
            DesignerValueKinds.Resource => resource(value.Value ?? string.Empty),
            DesignerValueKinds.Reference => component(value.Value ?? string.Empty),
            DesignerValueKinds.TypeOf => Type(value.Type),
            _ => throw new InvalidOperationException($"Unknown value kind {value.Kind}."),
        };
        return target is null ? result : Convert(result, target);
    }

    private Type Type(string? name) => name is not null && types.Find(name) is { } type ? type : throw new InvalidOperationException($"The type {name} wasn't found (build the project?).");

    private static object? Literal(DesignerValue value)
    {
        var text = value.Value ?? string.Empty;
        var invariant = CultureInfo.InvariantCulture;
        return value.Type switch
        {
            "null" => null,
            "string" => text,
            "bool" => text == "true",
            "char" => text.Length > 0 ? text[0] : '\0',
            "int" => int.Parse(text, invariant),
            "uint" => uint.Parse(text, invariant),
            "long" => long.Parse(text, invariant),
            "ulong" => ulong.Parse(text, invariant),
            "float" => float.Parse(text, invariant),
            "double" => double.Parse(text, invariant),
            "decimal" => decimal.Parse(text, invariant),
            "byte" => byte.Parse(text, invariant),
            "sbyte" => sbyte.Parse(text, invariant),
            "short" => short.Parse(text, invariant),
            "ushort" => ushort.Parse(text, invariant),
            _ => text,
        };
    }

    private object? Enum(DesignerValue value)
    {
        var type = Type(value.Type);
        if (!type.IsEnum)
        {
            return Member(value with { Kind = DesignerValueKinds.Member, Value = value.Members?.FirstOrDefault() });
        }

        return value.Members is { Length: > 0 } members
            ? System.Enum.Parse(type, string.Join(", ", members))
            : System.Enum.ToObject(type, long.Parse(value.Value ?? "0", CultureInfo.InvariantCulture));
    }

    private object? Member(DesignerValue value)
    {
        var type = Type(value.Type);
        var name = value.Value ?? string.Empty;
        if (type.IsEnum)
        {
            return System.Enum.Parse(type, name);
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        if (type.GetProperty(name, flags) is { } property)
        {
            return property.GetValue(null);
        }

        if (type.GetField(name, flags) is { } field)
        {
            return field.GetValue(null);
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(type)} has no static member {name}.");
    }

    private object New(DesignerValue value)
    {
        var type = Type(value.Type);
        var arguments = (value.Arguments ?? []).Select(a => Evaluate(a)).ToArray();
        foreach (var constructor in type.GetConstructors().Where(c => c.GetParameters().Length == arguments.Length))
        {
            if (TryConvert(arguments, constructor.GetParameters(), out var converted))
            {
                return constructor.Invoke(converted);
            }
        }

        if (arguments.Length == 0 && type.IsValueType)
        {
            return Activator.CreateInstance(type)!;
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(type)} has no constructor for ({string.Join(", ", arguments.Select(a => a?.GetType().Name ?? "null"))}).");
    }

    private object? Call(DesignerValue value)
    {
        var type = Type(value.Type);
        var arguments = (value.Arguments ?? []).Select(a => Evaluate(a)).ToArray();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == value.Value && m.GetParameters().Length == arguments.Length))
        {
            if (TryConvert(arguments, method.GetParameters(), out var converted))
            {
                return method.Invoke(null, converted);
            }
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(type)}.{value.Value} wasn't found for these arguments.");
    }

    private System.Array Array(DesignerValue value)
    {
        var type = Type(value.Type);
        var items = value.Arguments ?? [];
        var array = System.Array.CreateInstance(type, items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            array.SetValue(Evaluate(items[i], type), i);
        }

        return array;
    }

    /// <summary>Invokes <paramref name="name"/> on <paramref name="target"/> with the model's arguments (an instance method, by arity and convertible arguments).</summary>
    public void Invoke(object target, string name, DesignerValue[] arguments)
    {
        var values = arguments.Select(a => Evaluate(a)).ToArray();
        foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name && m.GetParameters().Length == values.Length))
        {
            if (TryConvert(values, method.GetParameters(), out var converted))
            {
                method.Invoke(target, converted);
                return;
            }
        }

        throw new InvalidOperationException($"{DesignerTypes.NameOf(target.GetType())}.{name} wasn't found for these arguments.");
    }

    private static bool TryConvert(object?[] arguments, ParameterInfo[] parameters, out object?[] converted)
    {
        converted = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            try
            {
                converted[i] = Convert(arguments[i], parameters[i].ParameterType);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A value as <paramref name="target"/>: numbers between numeric types, numbers to enums, else as it is when assignable.</summary>
    public static object? Convert(object? value, Type target)
    {
        if (value is null)
        {
            return !target.IsValueType || Nullable.GetUnderlyingType(target) is not null ? null : throw new InvalidCastException($"null isn't a {target.Name}.");
        }

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (underlying.IsEnum && value is IConvertible)
        {
            return System.Enum.ToObject(underlying, value);
        }

        if (value is IConvertible && underlying.IsPrimitive || underlying == typeof(decimal))
        {
            return System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
        }

        throw new InvalidCastException($"A {value.GetType().Name} isn't a {target.Name}.");
    }

    // ------------------------------------------------------------------ objects → values

    /// <summary>The code for <paramref name="value"/>, or null when code can't say it (an image: Visual Studio puts those in the resources).</summary>
    public DesignerValue? Serialize(object? value)
    {
        switch (value)
        {
            case null:
                return DesignerValue.Null;
            case string s:
                return DesignerValue.String(s);
            case bool b:
                return DesignerValue.Bool(b);
            case char c:
                return new DesignerValue { Kind = DesignerValueKinds.Literal, Type = "char", Value = c.ToString() };
            case int or long or short or byte or sbyte or uint or ulong or ushort or float or double:
                return new DesignerValue
                {
                    Kind = DesignerValueKinds.Literal,
                    Type = value switch { int => "int", long => "long", short => "short", byte => "byte", sbyte => "sbyte", uint => "uint", ulong => "ulong", ushort => "ushort", float => "float", _ => "double" },
                    Value = value switch { float f => f.ToString("R", CultureInfo.InvariantCulture), double d => d.ToString("R", CultureInfo.InvariantCulture), _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) },
                };
            case Enum e:
                return EnumValue(e);
            case Type type:
                return new DesignerValue { Kind = DesignerValueKinds.TypeOf, Type = DesignerTypes.NameOf(type) };
            case Color color:
                return ColorValue(color);
            case System.Array array when array.Rank == 1:
            {
                var items = new List<DesignerValue>();
                foreach (var item in array)
                {
                    if (Serialize(item) is not { } serialized)
                    {
                        return null;
                    }

                    items.Add(serialized);
                }

                return DesignerValue.ArrayOf(DesignerTypes.NameOf(array.GetType().GetElementType()!), [.. items]);
            }
        }

        if (value is IComponent && nameOf(value) is { } name)
        {
            return DesignerValue.ReferenceTo(name);
        }

        var converter = TypeDescriptor.GetConverter(value);
        if (converter.CanConvertTo(typeof(InstanceDescriptor)) && converter.ConvertTo(value, typeof(InstanceDescriptor)) is InstanceDescriptor { MemberInfo: { } member } descriptor)
        {
            var arguments = new List<DesignerValue>();
            foreach (var argument in descriptor.Arguments)
            {
                if (Serialize(argument) is not { } serialized)
                {
                    return null;
                }

                arguments.Add(serialized);
            }

            return member switch
            {
                ConstructorInfo constructor => DesignerValue.NewOf(DesignerTypes.NameOf(constructor.DeclaringType!), [.. arguments]),
                MethodInfo { IsStatic: true } method => DesignerValue.CallOf(DesignerTypes.NameOf(method.DeclaringType!), method.Name, [.. arguments]),
                FieldInfo { IsStatic: true } field => DesignerValue.MemberOf(DesignerTypes.NameOf(field.DeclaringType!), field.Name),
                PropertyInfo property when property.GetGetMethod()?.IsStatic == true => DesignerValue.MemberOf(DesignerTypes.NameOf(property.DeclaringType!), property.Name),
                _ => null,
            };
        }

        return null;
    }

    private static DesignerValue EnumValue(Enum value)
    {
        var type = value.GetType();
        var name = DesignerTypes.NameOf(type);
        if (System.Enum.GetName(type, value) is { } single)
        {
            return DesignerValue.EnumOf(name, single);
        }

        if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            var remaining = System.Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            var members = new List<string>();
            foreach (var member in System.Enum.GetValues(type).Cast<Enum>())
            {
                var bits = System.Convert.ToUInt64(member, CultureInfo.InvariantCulture);
                if (bits != 0 && (bits & (bits - 1)) == 0 && (remaining & bits) == bits)
                {
                    members.Add(member.ToString());
                    remaining &= ~bits;
                }
            }

            if (remaining == 0 && members.Count > 0)
            {
                return DesignerValue.EnumOf(name, [.. members]);
            }
        }

        return new DesignerValue { Kind = DesignerValueKinds.Enum, Type = name, Value = System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) };
    }

    private static DesignerValue ColorValue(Color color)
    {
        if (color.IsEmpty)
        {
            return DesignerValue.MemberOf("System.Drawing.Color", "Empty");
        }

        if (color.IsKnownColor)
        {
            return color.IsSystemColor ? DesignerValue.MemberOf("System.Drawing.SystemColors", color.Name) : DesignerValue.MemberOf("System.Drawing.Color", color.Name);
        }

        DesignerValue[] components = color.A == 255
            ? [DesignerValue.Int(color.R), DesignerValue.Int(color.G), DesignerValue.Int(color.B)]
            : [DesignerValue.Int(color.A), DesignerValue.Int(color.R), DesignerValue.Int(color.G), DesignerValue.Int(color.B)];
        return DesignerValue.CallOf("System.Drawing.Color", "FromArgb", components);
    }
}
