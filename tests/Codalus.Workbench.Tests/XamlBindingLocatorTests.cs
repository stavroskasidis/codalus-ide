using Codalus.Workbench.Previewer;

namespace Codalus.Workbench.Tests;

/// <summary>M64: a binding that failed in the running app, found in the XAML that declares it.</summary>
public sealed class XamlBindingLocatorTests
{
    private const string Window = """
        <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="App.Views.MainWindow">
          <StackPanel>
            <TextBlock Text="{Binding Missing}" />
            <TextBlock x:Name="Status" Text="{ReflectionBinding Path=Missing, Mode=OneWay}" />
            <TextBox Text="{Binding Title}" />
          </StackPanel>
        </Window>
        """;

    private const string View = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="App.Views.ItemView">
          <TextBlock Text="{Binding Missing}" />
        </UserControl>
        """;

    private static readonly (string, string)[] s_files = [("/app/Views/MainWindow.axaml", Window), ("/app/Views/ItemView.axaml", View)];

    [Fact]
    public void The_file_of_the_nearest_class_and_the_named_element_win()
    {
        // In the item view (its class is the nearest), at the path's first character.
        var inView = XamlBindingLocator.Find(new LiveBindingFailure("Text", "Missing", "m", "Avalonia.Controls.TextBlock", string.Empty, ["App.Views.ItemView", "App.Views.MainWindow"]), s_files);
        Assert.Equal(new XamlBindingLocation("/app/Views/ItemView.axaml", 2, 28), inView);

        // In the window: the named element's binding (Path=), not the first one with the same path.
        var named = XamlBindingLocator.Find(new LiveBindingFailure("Text", "Missing", "m", "Avalonia.Controls.TextBlock", "Status", ["Avalonia.Controls.StackPanel", "App.Views.MainWindow"]), s_files);
        Assert.Equal(new XamlBindingLocation("/app/Views/MainWindow.axaml", 4, 61), named);

        // No ancestors (WPF's log names none): any file that has it.
        Assert.NotNull(XamlBindingLocator.Find(new LiveBindingFailure("Text", "Title", "m", "TextBox", string.Empty, []), s_files));
        Assert.Null(XamlBindingLocator.Find(new LiveBindingFailure("Text", "Nowhere", "m", "TextBox", string.Empty, []), s_files));
        Assert.Null(XamlBindingLocator.Find(new LiveBindingFailure("Foreground", "Missing", "m", "TextBlock", string.Empty, []), s_files));
    }

    [Theory]
    [InlineData("{Binding Count}", "Count", 9)]
    [InlineData("{Binding Path=Items[0].Title, Mode=OneWay}", "Items[0].Title", 14)]
    [InlineData("{Binding Mode=OneWay, Path=Name}", "Name", 27)]
    [InlineData("{CompiledBinding Greeting, StringFormat={}{0}!}", "Greeting", 17)]
    [InlineData("{Binding}", "", 0)]
    public void Binding_paths_and_their_offsets(string value, string path, int offset) =>
        Assert.Equal((path, offset), XamlBindingLocator.BindingPath(value));
}
