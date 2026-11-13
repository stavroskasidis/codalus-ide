using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// M64 (ADR 0103) on samples/Avalonia: rename across C# and XAML both ways (binding paths under <c>x:DataType</c>,
/// <c>x:Class</c>, element and type names, <c>{x:Static}</c>, resource keys, command methods), Extract Resource, Extract
/// Style and generate from use. Each result is checked against a golden file (<see cref="XamlGolden"/>).
/// </summary>
public sealed partial class XamlLanguageTests
{
    private string ViewModel => avalonia.File("AvaloniaApp", "ViewModels", "MainWindowViewModel.cs");

    private string TodoViewModel => avalonia.File("AvaloniaApp", "ViewModels", "TodoItemViewModel.cs");

    private string Render(WorkspaceEdit? edit)
    {
        Assert.NotNull(edit);
        return XamlGolden.Render(edit, avalonia.Fixture, path =>
        {
            try
            {
                return avalonia.Text(path).Text;
            }
            catch (KeyNotFoundException)
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
        });
    }

    private async Task<WorkspaceEdit?> RenameAtAsync(string path, string marker, int delta, string newName, string placeholder)
    {
        var prepare = await avalonia.Client.PrepareRenameAsync(Uri(path), avalonia.At(path, marker, delta), Ct);
        Assert.Equal(placeholder, prepare?.Placeholder);
        return await avalonia.Client.RenameAsync(Uri(path), avalonia.At(path, marker, delta), newName, Ct);
    }

    private async Task<CodeAction[]> ActionsAsync(string path, string marker, int delta)
    {
        var at = avalonia.At(path, marker, delta);
        return await avalonia.Client.CodeActionsAsync(Uri(path), new Protocol.Range(at, at), [], Ct);
    }

    [Fact]
    public async Task Renaming_a_view_model_property_in_csharp_renames_its_compiled_bindings()
    {
        await FreshAsync(ViewModel);
        await FreshAsync(MainWindow);
        var edit = await RenameAtAsync(ViewModel, "public string Greeting", "public string ".Length + 2, "Welcome", "Greeting");
        XamlGolden.Check("avalonia-rename-property-from-csharp", Render(edit));
    }

    [Fact]
    public async Task Renaming_a_binding_path_in_xaml_renames_the_property_and_its_uses()
    {
        await FreshAsync(TodoItemView);
        var edit = await RenameAtAsync(TodoItemView, "{Binding Title}", "{Binding Ti".Length, "Caption", "Title");
        XamlGolden.Check("avalonia-rename-binding-path-from-xaml", Render(edit));
    }

    [Fact]
    public async Task Renaming_a_type_from_x_DataType_renames_the_class_and_its_xaml_uses()
    {
        await FreshAsync(TodoItemView);
        await FreshAsync(MainWindow);
        var edit = await RenameAtAsync(TodoItemView, "x:DataType=\"vm:TodoItemViewModel\"", "x:DataType=\"vm:Todo".Length, "TaskViewModel", "TodoItemViewModel");
        XamlGolden.Check("avalonia-rename-type-from-xaml", Render(edit));
    }

    [Fact]
    public async Task Renaming_a_user_control_class_in_csharp_renames_x_Class_and_its_elements()
    {
        var codeBehind = avalonia.File("AvaloniaApp", "Views", "TodoItemView.axaml.cs");
        await FreshAsync(codeBehind);
        var edit = await RenameAtAsync(codeBehind, "class TodoItemView", "class Todo".Length, "TaskView", "TodoItemView");
        XamlGolden.Check("avalonia-rename-x-class-from-csharp", Render(edit));
    }

    [Fact]
    public async Task Renaming_a_static_member_and_a_command_method_reach_x_Static_and_the_command_binding()
    {
        await FreshAsync(MainWindow);
        var design = avalonia.File("AvaloniaApp", "ViewModels", "DesignData.cs");
        await FreshAsync(design);
        var fromCSharp = await RenameAtAsync(design, "MainWindowViewModel Main", "MainWindowViewModel Ma".Length, "Sample", "Main");
        XamlGolden.Check("avalonia-rename-static-from-csharp", Render(fromCSharp));

        var fromXaml = await RenameAtAsync(MainWindow, "{Binding Increment}", "{Binding Inc".Length, "Add", "Increment");
        XamlGolden.Check("avalonia-rename-command-from-xaml", Render(fromXaml));
    }

    [Fact]
    public async Task Renaming_a_resource_key_renames_its_definition_and_lookups()
    {
        await FreshAsync(MainWindow);
        var codeBehind = avalonia.File("AvaloniaApp", "Views", "MainWindow.axaml.cs");
        await FreshAsync(codeBehind);
        await avalonia.EditAsync(codeBehind, "IncrementButton.Focus();", "IncrementButton.Focus();\n        _ = this.FindResource(\"CardSpacing\");\n        _ = \"CardSpacing\";");
        var edit = await RenameAtAsync(MainWindow, "{StaticResource CardSpacing}", "{StaticResource Ca".Length, "Gap", "CardSpacing");
        XamlGolden.Check("avalonia-rename-resource-key", Render(edit)); // the lookup's key in C#, not the other string
        await FreshAsync(codeBehind);

        // Not a symbol of the solution's source: Avalonia's own type isn't renamed.
        Assert.Null(await avalonia.Client.PrepareRenameAsync(Uri(MainWindow), avalonia.At(MainWindow, "<DockPanel", 2), Ct));
    }

    [Fact]
    public async Task Extract_resource_moves_a_value_to_the_window_or_the_app()
    {
        await FreshAsync(MainWindow);
        await avalonia.EditAsync(MainWindow, "<ListBox x:Name=\"Tasks\"", "<Border Margin=\"12\" />\n    <ListBox x:Name=\"Tasks\"");
        var actions = await ActionsAsync(MainWindow, "<DockPanel Margin=\"12\"", "<DockPanel Margin=\"1".Length);
        var here = Assert.Single(actions, a => a.Title == "Extract Resource 'DockPanelMargin'");
        XamlGolden.Check("avalonia-extract-resource", Render(here.Edit));
        var all = Assert.Single(actions, a => a.Title == "Extract Resource 'DockPanelMargin' and use it for 1 equal value");
        XamlGolden.Check("avalonia-extract-resource-equal-values", Render(all.Edit));
        var app = Assert.Single(actions, a => a.Title == "Extract Resource 'DockPanelMargin' to App.axaml");
        XamlGolden.Check("avalonia-extract-resource-to-app", Render(app.Edit));
        await FreshAsync(MainWindow);
    }

    [Fact]
    public async Task Extract_style_moves_an_elements_properties_to_a_class_style()
    {
        await FreshAsync(TodoItemView);
        var actions = await ActionsAsync(TodoItemView, "<TextBlock VerticalAlignment=\"Center\" Foreground", 3);
        var style = Assert.Single(actions, a => a.Title.StartsWith("Extract Style", StringComparison.Ordinal));
        Assert.Equal("Extract Style '.textBlockStyle'", style.Title);
        XamlGolden.Check("avalonia-extract-style", Render(style.Edit));
    }

    [Fact]
    public async Task Generate_from_use_adds_a_property_a_relay_command_and_a_converter()
    {
        await FreshAsync(MainWindow);
        await FreshAsync(ViewModel);
        await avalonia.EditAsync(MainWindow, "Text=\"{Binding Count}\"", "Text=\"{Binding Status}\"");
        await avalonia.EditAsync(MainWindow, "Command=\"{Binding Increment}\"", "Command=\"{Binding ResetCommand}\"");
        var property = Assert.Single(await ActionsAsync(MainWindow, "{Binding Status}", "{Binding St".Length), a => a.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Generate property 'Status' in MainWindowViewModel", property.Title);
        XamlGolden.Check("avalonia-generate-property", Render(property.Edit));

        // With CommunityToolkit.Mvvm (the fixture's stand-in for its attribute): a [RelayCommand] method.
        var command = Assert.Single(await ActionsAsync(MainWindow, "{Binding ResetCommand}", "{Binding Re".Length), a => a.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Generate [RelayCommand] method 'Reset' in MainWindowViewModel", command.Title);
        XamlGolden.Check("avalonia-generate-relay-command", Render(command.Edit));
        await FreshAsync(MainWindow);

        // A converter resource that doesn't exist: its class in a new file, its resource and namespace in the XAML.
        await FreshAsync(TodoItemView);
        await avalonia.EditAsync(TodoItemView, "Text=\"{Binding Priority}\"", "Text=\"{Binding Priority, Converter={StaticResource PriorityTextConverter}}\"");
        var converter = Assert.Single(await ActionsAsync(TodoItemView, "{StaticResource PriorityTextConverter}", "{StaticResource Pri".Length), a => a.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Generate converter 'PriorityTextConverter'", converter.Title);
        XamlGolden.Check("avalonia-generate-converter", Render(converter.Edit));
        await FreshAsync(TodoItemView);

        // Nothing to generate for a path the view model has.
        Assert.DoesNotContain(await ActionsAsync(TodoItemView, "{Binding Title}", "{Binding Ti".Length), a => a.Kind == CodeActionKind.QuickFix);
    }
}
