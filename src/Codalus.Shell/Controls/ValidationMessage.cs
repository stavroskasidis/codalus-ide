using Avalonia.Data.Converters;

namespace Codalus.Shell.Controls;

/// <summary>A validation error as the message under its field (M34h): an exception's message, anything else as text.</summary>
public static class ValidationMessage
{
    public static IValueConverter Converter { get; } =
        new FuncValueConverter<object?, string?>(error => error is Exception exception ? exception.Message : error?.ToString());
}
