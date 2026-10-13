using System.Text.Json;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codalus-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string UserFile => Path.Combine(_dir, "settings.json");

    private string WorkspaceFile => Path.Combine(_dir, ".codalus", "settings.json");

    private static SettingDescriptor[] Schema() =>
    [
        SettingDescriptor.Create("workbench.colorTheme", "\"dark\"", SettingType.String, ContributionSource.Shell) with { Enum = ["light", "dark", "highContrast", "system"] },
        SettingDescriptor.Create("editor.fontSize", "13", SettingType.Integer, ContributionSource.Shell) with { Minimum = 6, Maximum = 72 },
        SettingDescriptor.Create("telemetry.enabled", "false", SettingType.Boolean, ContributionSource.Shell) with { Scope = SettingScope.User },
    ];

    [Fact]
    public void Layers_defaults_user_and_workspace()
    {
        File.WriteAllText(UserFile, """
            {
              // user comment
              "workbench.colorTheme": "light",
              "editor.fontSize": 14,
            }
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(WorkspaceFile)!);
        File.WriteAllText(WorkspaceFile, """{ "editor.fontSize": 16, "telemetry.enabled": true }""");
        using var settings = new SettingsService(UserFile, WorkspaceFile);
        settings.Register(Schema());

        Assert.Equal("light", settings.GetString("workbench.colorTheme"));
        Assert.Equal(16, settings.GetInt32("editor.fontSize"));
        Assert.False(settings.GetBoolean("telemetry.enabled")); // user-only setting ignored in the workspace layer
        Assert.Contains(settings.Diagnostics, d => d.Contains("telemetry.enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_values_fall_back_to_defaults_with_a_diagnostic()
    {
        File.WriteAllText(UserFile, """{ "workbench.colorTheme": "neon", "editor.fontSize": 200 }""");
        using var settings = new SettingsService(UserFile);
        settings.Register(Schema());

        Assert.Equal("dark", settings.GetString("workbench.colorTheme"));
        Assert.Equal(13, settings.GetInt32("editor.fontSize"));
        Assert.Equal(2, settings.Diagnostics.Count);
    }

    [Fact]
    public async Task Writes_preserve_comments_and_raise_change_events()
    {
        File.WriteAllText(UserFile, """
            {
                // keep me
                "editor.fontSize": 14 /* and me */
            }
            """);
        using var settings = new SettingsService(UserFile);
        settings.Register(Schema());
        var changes = new List<string>();
        settings.Changed += (_, e) => changes.AddRange(e.Keys);

        await settings.SetAsync("workbench.colorTheme", "highContrast", SettingScope.User, TestContext.Current.CancellationToken);
        await settings.SetAsync("editor.fontSize", 20, SettingScope.User, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(UserFile, TestContext.Current.CancellationToken);
        Assert.Contains("// keep me", text, StringComparison.Ordinal);
        Assert.Contains("/* and me */", text, StringComparison.Ordinal);
        Assert.Equal("highContrast", settings.GetString("workbench.colorTheme"));
        Assert.Equal(20, settings.GetInt32("editor.fontSize"));
        Assert.Equal(["workbench.colorTheme", "editor.fontSize"], changes);

        await settings.SetAsync("editor.fontSize", (JsonElement?)null, SettingScope.User, TestContext.Current.CancellationToken);
        Assert.Equal(13, settings.GetInt32("editor.fontSize"));
        Assert.False(settings.IsModified("editor.fontSize"));
    }

    [Fact]
    public async Task Registering_settings_is_reported_apart_from_value_changes()
    {
        using var settings = new SettingsService(UserFile);
        settings.Register([Schema()[0]]);
        var events = new List<SettingsChangedEventArgs>();
        settings.Changed += (_, e) => events.Add(e);

        settings.Register([Schema()[1]]);
        await settings.SetAsync("editor.fontSize", 20, SettingScope.User, TestContext.Current.CancellationToken);

        Assert.Equal(["editor.fontSize"], events[0].Registered);
        Assert.Empty(events[1].Registered);
        Assert.Equal(["editor.fontSize"], events[1].Keys);
    }

    [Fact]
    public async Task Written_values_stay_readable()
    {
        using var settings = new SettingsService(UserFile);
        settings.Register([SettingDescriptor.Create("editor.fontFamily", "\"\"", SettingType.String, ContributionSource.Shell)]);

        await settings.SetAsync("editor.fontFamily", "Fira Code+, <Mono> & \"Sans\"", SettingScope.User, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(UserFile, TestContext.Current.CancellationToken);
        Assert.Contains("\"Fira Code+, <Mono> & \\\"Sans\\\"\"", text, StringComparison.Ordinal);
        Assert.Equal("Fira Code+, <Mono> & \"Sans\"", settings.GetString("editor.fontFamily"));
    }

    [Fact]
    public async Task Rejects_invalid_writes()
    {
        using var settings = new SettingsService(UserFile);
        settings.Register(Schema());
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetAsync("editor.fontSize", 1, SettingScope.User, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Values_for_unregistered_keys_apply_once_registered()
    {
        File.WriteAllText(UserFile, """{ "hello.greeting": "Hi" }""");
        using var settings = new SettingsService(UserFile);
        var changes = new List<string>();
        settings.Changed += (_, e) => changes.AddRange(e.Keys);

        Assert.Equal(JsonValueKind.Undefined, settings.GetValue("hello.greeting").ValueKind);
        settings.Register([SettingDescriptor.Create("hello.greeting", "\"Hello\"", SettingType.String, ContributionSource.Plugin("x.hello", builtIn: false))]);

        Assert.Equal("Hi", settings.GetString("hello.greeting"));
        Assert.Contains("hello.greeting", changes);
    }

    [Fact]
    public async Task External_edits_are_picked_up_when_watching()
    {
        File.WriteAllText(UserFile, "{}");
        using var settings = new SettingsService(UserFile, watch: true);
        settings.Register(Schema());
        var changed = new TaskCompletionSource();
        settings.Changed += (_, e) =>
        {
            if (e.Affects("editor"))
            {
                changed.TrySetResult();
            }
        };

        await File.WriteAllTextAsync(UserFile, """{ "editor.fontSize": 18 }""", TestContext.Current.CancellationToken);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(18, settings.GetInt32("editor.fontSize"));
    }
}

public class JsoncEditorTests
{
    [Fact]
    public void Creates_a_document_when_empty() =>
        Assert.Equal("{\n    \"a\": 1\n}\n", JsoncEditor.SetProperty("", "a", "1"));

    [Fact]
    public void Replaces_only_the_value()
    {
        const string Text = "{\n  // c\n  \"a\": 1, // after\n  \"b\": [1, 2]\n}";
        Assert.Equal("{\n  // c\n  \"a\": 2, // after\n  \"b\": [1, 2]\n}", JsoncEditor.SetProperty(Text, "a", "2"));
        Assert.Equal("{\n  // c\n  \"a\": 1, // after\n  \"b\": \"x\"\n}", JsoncEditor.SetProperty(Text, "b", "\"x\""));
    }

    [Fact]
    public void Appends_after_the_last_member()
    {
        Assert.Equal("{\n  \"a\": 1,\n    \"b\": true\n}", JsoncEditor.SetProperty("{\n  \"a\": 1\n}", "b", "true"));
        Assert.Equal("{ \"a\": 1,\n    \"b\": true, }", JsoncEditor.SetProperty("{ \"a\": 1, }", "b", "true"));
        Assert.Equal("{ // only a comment\n    \"b\": true\n}", JsoncEditor.SetProperty("{ // only a comment\n}", "b", "true"));
    }

    [Fact]
    public void Removes_members_and_fixes_commas()
    {
        const string Text = "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3\n}";
        Assert.Equal("{\n  \"b\": 2,\n  \"c\": 3\n}", JsoncEditor.SetProperty(Text, "a", null));
        Assert.Equal("{\n  \"a\": 1,\n  \"c\": 3\n}", JsoncEditor.SetProperty(Text, "b", null));
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2\n}", JsoncEditor.SetProperty(Text, "c", null));
        Assert.Equal(Text, JsoncEditor.SetProperty(Text, "missing", null));
    }

    [Fact]
    public void Handles_strings_containing_structural_characters()
    {
        const string Text = "{ \"a\": \"{,}//\", \"b\": { \"x\": [\"]\"] } }";
        Assert.Equal("{ \"a\": \"{,}//\", \"b\": 5 }", JsoncEditor.SetProperty(Text, "b", "5"));
    }
}
