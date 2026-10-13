using Avalonia.Markup.Xaml;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell.Infrastructure;

/// <summary>
/// A view's text in the display language (M23b): <c>Text="{l:T fontsAndColors.heading}"</c> with
/// <c>xmlns:l="using:DaedalusStudio.Shell.Infrastructure"</c>. The key's English is in <c>Localization/en.json</c>.
/// The value is read when the view loads (the language changes at the next start), so it costs no binding.
/// </summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Localizer.Get(Key);
}
