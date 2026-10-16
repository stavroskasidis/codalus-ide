namespace CsLibrary;

/// <summary>A C# class the F# console app uses.</summary>
public sealed class Greeter(string greeting)
{
    /// <summary>The greeting followed by the name.</summary>
    public string Greet(string name) => $"{greeting}, {name}!";
}
