using System.Text.Json.Serialization;

public sealed record Person(string Name);

/// <summary>Serialization code comes from the System.Text.Json source generator.</summary>
[JsonSerializable(typeof(Person))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
