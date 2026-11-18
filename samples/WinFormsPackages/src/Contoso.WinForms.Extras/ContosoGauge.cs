using System.ComponentModel;

namespace Contoso.WinForms.Extras;

/// <summary>A control of an assembly the app doesn't reference, chosen in Choose Toolbox Items.</summary>
[Description("Shows a value on a dial.")]
public class ContosoGauge : Control
{
    [DefaultValue(0)]
    public int Value { get; set; }
}
