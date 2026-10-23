using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;

namespace Codalus.Shell.Tests;

/// <summary>
/// The editor's plain text as bright as Visual Studio 2022's (the maintainer's report: namespaces, identifiers,
/// punctuation and Razor's markup text were gray next to Visual Studio's Dark theme). Each classification's rendered
/// color is read from the editor's visual lines and compared with Visual Studio's value; all differences are reported
/// at once.
/// </summary>
public sealed class EditorTextColorTests
{
    private const string CSharp = """
        using System;
        using System.Collections.Generic;

        namespace Demo.Web;

        /// <summary>Signs the user in.</summary>
        public sealed class LoginService
        {
            // A comment
            public string Name { get; set; } = "admin";

            public string SignIn(IServiceProvider serviceProvider, List<string> userOptions)
            {
                var loginInput = Name;
                loginInput = loginInput.Trim();
                return loginInput + userOptions.Count + serviceProvider + undefinedName;
            }
        }

        """;

    private const string Razor = """
        @page "/login"
        @using Microsoft.AspNetCore.Components.Web
        <h1 class="title">Sign in here</h1>
        <p>Plain text</p>
        <button @onclick="SignIn">Go</button>
        <script>
            const loginForm = document.getElementById("login");
        </script>
        @code {
            private string loginInput = "";

            private void SignIn() => loginInput = loginInput.Trim();
        }

        """;

    /// <summary>The rendered color of the character at <paramref name="marker"/> plus <paramref name="delta"/>.</summary>
    internal static Color? ColorAt(CodeEditorController editor, string marker, int delta = 0)
    {
        var text = editor.Model.Text.Text;
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{marker}' not found");
        var line = editor.Model.Text.GetLineByOffset(offset + delta);
        var column = offset + delta - line.Offset;
        var view = editor.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        var visual = view.GetOrConstructVisualLine(line);
        var brush = visual.Elements.FirstOrDefault(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + Math.Max(1, e.DocumentLength))
            ?.TextRunProperties.ForegroundBrush;
        return (brush ?? editor.Editor.Foreground) is ISolidColorBrush solid ? Color.FromArgb((byte)(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B) : null;
    }

    /// <summary>Every probe whose color differs from Visual Studio's, as one message (empty when all match).</summary>
    private static string Differences(CodeEditorController editor, IEnumerable<(string Name, string Marker, int Delta, uint Expected)> probes) =>
        string.Join(Environment.NewLine, probes
            .Select(p => (p.Name, Expected: Color.FromUInt32(p.Expected), Actual: ColorAt(editor, p.Marker, p.Delta)))
            .Where(p => p.Actual != p.Expected)
            .Select(p => $"{p.Name}: expected {p.Expected}, rendered {p.Actual}"));

    [AvaloniaFact]
    public async Task Classic_dark_and_light_color_csharp_text_as_visual_studio()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "dark", Workbench.Settings.SettingScope.User, CancellationToken.None);
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(f.UserData, "colors")).FullName, "LoginService.cs");
        await File.WriteAllTextAsync(path, CSharp, TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path, preview: false);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var editor = document!.Controller!;
        await ShellFixture.PumpUntil(() => editor.Colorizer.HasTokens && ColorAt(editor, "serviceProvider,") == Color.FromUInt32(0xFF9CDCFE), seconds: 120);

        // Visual Studio 2022's Dark theme: Roslyn's color scheme (ColorSchemes/VisualStudio2019.xml) over the editor's
        // plain text, #DCDCDC; namespaces, fields, properties and unresolved names have no color of their own.
        Assert.Equal(Color.FromUInt32(0xFFDCDCDC), ((ISolidColorBrush)editor.Editor.Foreground!).Color);
        var differences = Differences(editor,
        [
            ("using namespace", "Collections", 0, 0xFFDCDCDC),
            ("namespace declaration", "Demo.Web", 0, 0xFFDCDCDC),
            ("unresolved identifier", "undefinedName", 0, 0xFFDCDCDC),
            ("property", "Name {", 0, 0xFFDCDCDC),
            ("property use", "userOptions.Count", 12, 0xFFDCDCDC),
            ("punctuation ;", "Generic;", 7, 0xFFDCDCDC),
            ("punctuation ,", "serviceProvider,", 15, 0xFFDCDCDC),
            ("parameter", "serviceProvider,", 0, 0xFF9CDCFE),
            ("parameter use", "userOptions.Count", 0, 0xFF9CDCFE),
            ("local", "loginInput = Name", 0, 0xFF9CDCFE),
            ("keyword", "public sealed", 0, 0xFF569CD6),
            ("class", "LoginService", 0, 0xFF4EC9B0),
            ("method", "SignIn(", 0, 0xFFDCDCAA),
            ("xml doc delimiter", "/// <summary>", 0, 0xFF608B4E),
            ("xml doc name", "<summary>", 1, 0xFF608B4E),
            ("xml doc text", "Signs the user", 0, 0xFF57A64A),

            // The classic IDE's measured control keyword, comment and string colors (#83, D47).
            ("control keyword", "return loginInput", 0, 0xFFD8A0DF),
            ("comment", "A comment", 0, 0xFF57A64A),
            ("string", "\"admin\"", 1, 0xFFD69D85),
        ]);
        NavigationDepthTests.Screenshot(f, "editor-text-csharp-dark");
        Assert.True(differences.Length == 0, differences);

        // Classic Light (and Blue, which shares its editor colors): Visual Studio's Light, on black plain text.
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "light", Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => ColorAt(editor, "serviceProvider,") != Color.FromUInt32(0xFF9CDCFE), seconds: 30);
        Assert.Equal(Color.FromUInt32(0xFF000000), ((ISolidColorBrush)editor.Editor.Foreground!).Color);
        differences = Differences(editor,
        [
            ("using namespace", "Collections", 0, 0xFF000000),
            ("namespace declaration", "Demo.Web", 0, 0xFF000000),
            ("unresolved identifier", "undefinedName", 0, 0xFF000000),
            ("property", "Name {", 0, 0xFF000000),
            ("punctuation ;", "Generic;", 7, 0xFF000000),
            ("parameter", "serviceProvider,", 0, 0xFF1F377F),
            ("local", "loginInput = Name", 0, 0xFF1F377F),
            ("xml doc delimiter", "/// <summary>", 0, 0xFF808080),
            ("xml doc name", "<summary>", 1, 0xFF808080),
            ("xml doc text", "Signs the user", 0, 0xFF008000),
            ("control keyword", "return loginInput", 0, 0xFF8F08C4),
            ("comment", "A comment", 0, 0xFF008000),
            ("string", "\"admin\"", 1, 0xFFA31515),
        ]);
        NavigationDepthTests.Screenshot(f, "editor-text-csharp-light");
        Assert.True(differences.Length == 0, differences);
    }

    [AvaloniaFact]
    public async Task Classic_dark_colors_razor_text_as_visual_studio_dark()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Blazor");
        var page = Path.Combine(folder, "BlazorApp", "Components", "Pages", "Login.razor");
        await File.WriteAllTextAsync(page, Razor, TestContext.Current.CancellationToken);
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "dark", Workbench.Settings.SettingScope.User, CancellationToken.None);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Blazor.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 180);
        var document = await f.Shell.OpenFileAsync(page, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var editor = document!.Controller!;
        await ShellFixture.PumpUntil(() => editor.Colorizer.HasTokens && ColorAt(editor, "@using", 1) == Color.FromUInt32(0xFFA699E6), seconds: 120);

        var differences = Differences(editor,
        [
            ("@using namespace", "Microsoft.AspNetCore", 0, 0xFFDCDCDC),
            ("HTML text", "Sign in here", 0, 0xFFDCDCDC),
            ("HTML text in p", "Plain text", 0, 0xFFDCDCDC),
            ("HTML element", "<h1", 1, 0xFF569CD6),
            ("HTML attribute", "class=", 0, 0xFF9CDCFE),
            ("JavaScript punctuation", "(\"login\");", 9, 0xFFDCDCDC),
            ("JavaScript dot", "document.", 8, 0xFFDCDCDC),
            ("C# field", "loginInput.Trim", 0, 0xFFDCDCDC),
            ("C# punctuation", "Trim();", 6, 0xFFDCDCDC),
            ("C# method", "Trim();", 0, 0xFFDCDCAA),
            ("C# keyword", "private void", 0, 0xFF569CD6),
            ("Razor transition", "@code", 0, 0xFFA699E6),
        ]);
        NavigationDepthTests.Screenshot(f, "editor-text-razor-dark");
        Assert.True(differences.Length == 0, differences);
    }
}
