using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Codalus.Shell.Tests;

/// <summary>Paste with automatic usings (deferred from M12b): pasted C# gets the usings it needs; undo takes them back.</summary>
public sealed class PasteImportsTests
{
    [Fact]
    public void The_namespaces_in_scope_are_the_usings_and_the_declared_namespaces()
    {
        const string CSharp = "global using System;\nusing System.Text;\nusing static System.Math;\nusing Alias = System.IO;\n\nnamespace App.Tools;\n";
        Assert.Equal(["System", "System.Text", "App.Tools"], ShellHost.NamespacesInScope(CSharp));
        Assert.Equal(["System.Text", "App"], ShellHost.NamespacesInScope("Imports System.Text\n\nNamespace App\nEnd Namespace\n"));
    }

    [AvaloniaFact]
    public async Task Pasted_code_gets_its_usings_and_undo_removes_them()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var path = Path.Combine(folder, "src", "App", "Pasted.cs");
        await File.WriteAllTextAsync(path, "namespace App;\n\ninternal static class Pasted\n{\n}\n", TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path);
        await ShellFixture.PumpUntil(() => document?.Controller is not null && f.Shell.Languages.RunningClient("csharp") is not null, seconds: 120);
        var controller = document!.Controller!;
        var text = controller.Model.Text;

        const string Code = "    public static string Escape(string value) => new StringBuilder(Regex.Escape(value)).ToString();\n";
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(f.Window.Clipboard!, Code);
        controller.Editor.CaretOffset = text.Text.IndexOf("{\n}", StringComparison.Ordinal) + 2;
        controller.Editor.Focus();
        f.Press(Key.V, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
        Assert.Contains(Code, text.Text, StringComparison.Ordinal);

        await ShellFixture.PumpUntil(() => text.Text.Contains("using System.Text.RegularExpressions;", StringComparison.Ordinal), seconds: 120);
        Assert.Contains("using System.Text;", text.Text, StringComparison.Ordinal);
        Assert.Contains(Code, text.Text, StringComparison.Ordinal);

        // Undo takes the usings back, leaving the paste.
        while (text.Text.Contains("using ", StringComparison.Ordinal))
        {
            controller.Editor.Undo();
        }

        Assert.Contains(Code, text.Text, StringComparison.Ordinal);

        // Turned off, a paste is just a paste.
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.PasteAddsImportsSetting, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        controller.Editor.CaretOffset = text.Text.IndexOf(Code, StringComparison.Ordinal);
        f.Press(Key.V, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => true);
        Assert.DoesNotContain("using ", text.Text, StringComparison.Ordinal);
    }
}
