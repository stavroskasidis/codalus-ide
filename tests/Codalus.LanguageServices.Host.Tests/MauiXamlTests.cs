using Codalus.LanguageServices.Host.Xaml;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// .NET MAUI XAML in the XAML engine (M20d): the same engine as Avalonia's, on a compilation with MAUI's shape
/// (stand-in types: the real ones need the MAUI workload, which CI doesn't have): XML namespaces from
/// <c>[XmlnsDefinition]</c>, CLR properties over bindable properties, attached properties as static <c>SetX</c>,
/// <c>[ContentProperty("…")]</c>, named colors, binding modes, and compiled bindings through <c>x:DataType</c>.
/// </summary>
public sealed class MauiXamlTests
{
    private const string Maui = "http://schemas.microsoft.com/dotnet/2021/maui";
    private const string X = "http://schemas.microsoft.com/winfx/2009/xaml";

    private static readonly Lazy<Compilation> s_compilation = new(() => CSharpCompilation.Create(
        "MauiApp",
        [CSharpSyntaxTree.ParseText("""
            [assembly: Microsoft.Maui.Controls.XmlnsDefinition("http://schemas.microsoft.com/dotnet/2021/maui", "Microsoft.Maui.Controls")]
            namespace Microsoft.Maui.Controls
            {
                [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : System.Attribute { }
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class ContentPropertyAttribute(string name) : System.Attribute { }
                public class BindableObject { }
                public sealed class BindableProperty { }
                public enum BindingMode { Default, TwoWay, OneWay, OneWayToSource, OneTime }
                public class Element : BindableObject { }
                public class VisualElement : Element { public double WidthRequest { get; set; } public Microsoft.Maui.Graphics.Color BackgroundColor { get; set; } }
                public class View : VisualElement { }
                public class Page : VisualElement { public string Title { get; set; } }
                [ContentProperty("Content")]
                public class ContentPage : Page { public View Content { get; set; } }
                [ContentProperty("Children")]
                public class Grid : View
                {
                    public static readonly BindableProperty RowProperty = new();
                    public static int GetRow(BindableObject bindable) => 0;
                    public static void SetRow(BindableObject bindable, int value) { }
                    public System.Collections.Generic.IList<View> Children { get; } = new System.Collections.Generic.List<View>();
                }
                public class Label : View { public string Text { get; set; } public Microsoft.Maui.Graphics.Color TextColor { get; set; } }
                public class Button : View { public string Text { get; set; } public event System.EventHandler Clicked; }
            }
            namespace Microsoft.Maui.Graphics
            {
                public class Color { }
                public static class Colors { public static readonly Color Red = new(); public static readonly Color CornflowerBlue = new(); }
            }
            namespace MauiApp
            {
                public sealed class CounterViewModel { public string Text { get; set; } public int Count { get; set; } }
            }
            """)],
        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location)],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static XamlContext Context(string text) => XamlContext.ForText(Path.Combine(Path.GetTempPath(), "MainPage.xaml"), text, null, s_compilation.Value);

    private static async Task<IReadOnlyList<string>> CompleteAsync(string text, string marker, int delta = 0)
    {
        var context = Context(text.Replace(marker, string.Empty, StringComparison.Ordinal));
        var list = await XamlCompletion.CompleteAsync(context, text.IndexOf(marker, StringComparison.Ordinal) + delta, TestContext.Current.CancellationToken);
        return [.. list.Items.Select(i => i.Label)];
    }

    private static string Page(string body) => $"""
        <ContentPage xmlns="{Maui}" xmlns:x="{X}" xmlns:local="clr-namespace:MauiApp" x:Class="MauiApp.MainPage" x:DataType="local:CounterViewModel">
        {body}
        </ContentPage>
        """;

    [Fact]
    public void Maui_is_a_xaml_framework_and_xaml_files_are_xaml()
    {
        Assert.True(XamlTypeSystem.For(s_compilation.Value).IsMaui);
        Assert.True(XamlTypeSystem.For(s_compilation.Value).IsXamlFramework);
        Assert.True(XamlContext.IsXaml("MainPage.xaml"));
        Assert.True(XamlContext.IsXaml("MainWindow.axaml"));
        Assert.False(XamlContext.IsXaml("MainPage.xaml.cs"));
    }

    [Fact]
    public async Task Elements_complete_from_the_maui_namespace()
    {
        var items = await CompleteAsync(Page("<Grid><|</Grid>"), "|");
        Assert.Contains("Label", items);
        Assert.Contains("Button", items);
        Assert.Contains("Grid", items);
    }

    [Fact]
    public async Task Attributes_complete_with_properties_events_and_attached_properties()
    {
        var items = await CompleteAsync(Page("<Grid><Label |/></Grid>"), "|");
        Assert.Contains("Text", items);
        Assert.Contains("WidthRequest", items); // a bindable property's CLR wrapper, from the base class
        Assert.Contains("Grid.Row", items); // attached: a static SetRow next to RowProperty

        var button = await CompleteAsync(Page("<Button |/>"), "|");
        Assert.Contains("Clicked", button);
    }

    [Fact]
    public async Task Values_complete_named_colors_binding_modes_and_compiled_binding_paths()
    {
        Assert.Contains("CornflowerBlue", await CompleteAsync(Page("<Label TextColor=\"|\" />"), "|"));
        Assert.Contains("TwoWay", await CompleteAsync(Page("<Label Text=\"{Binding Text, Mode=|}\" />"), "|"));
        var paths = await CompleteAsync(Page("<Label Text=\"{Binding |}\" />"), "|");
        Assert.Contains("Count", paths); // the view model's, through x:DataType
        Assert.Contains("Text", paths);
    }

    [Fact]
    public void A_correct_page_has_no_diagnostics_and_mistakes_are_reported()
    {
        var clean = XamlAnalysis.Diagnose(Context(Page("""<Grid><Label Grid.Row="1" Text="{Binding Text}" TextColor="Red" /><Button Text="Add" /></Grid>""")));
        Assert.Empty(clean); // the content property (ContentPage.Content, Grid.Children) takes the children

        var wrong = XamlAnalysis.Diagnose(Context(Page("""<Grid><Lable /><Label Txt="x" Grid.Rows="1" /></Grid>""")));
        Assert.Contains(wrong, d => d.Code == "XAML0003" && d.Message.Contains("Lable", StringComparison.Ordinal));
        Assert.Contains(wrong, d => d.Code == "XAML0004" && d.Message.Contains("'Txt'", StringComparison.Ordinal));
        Assert.Contains(wrong, d => d.Code == "XAML0004" && d.Message.Contains("'Rows'", StringComparison.Ordinal));
    }
}
