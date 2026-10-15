namespace CsLibrary;

/// <summary>Formats prices; a C# type the Visual Basic projects use.</summary>
public sealed class PriceFormatter(string currency)
{
    /// <summary>The currency code put after the amount.</summary>
    public string Currency { get; } = currency;

    /// <summary>"Tea: 3.00 EUR".</summary>
    public string Format(string name, decimal price) => $"{name}: {price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} {Currency}";
}
