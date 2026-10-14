using Codalus.Platform;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>AI assistance's data boundary (ADR 0062): what may be sent, what is redacted, and where Apply puts code.</summary>
public sealed class AiBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-ai-boundary-" + Guid.NewGuid().ToString("N"));

    public AiBoundaryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Models with no provider plugins (no host is ever started).</summary>
    private static LanguageModelService Models(WorkbenchServices workbench) =>
        new(new PluginManager(workbench, _ => Codalus.Rpc.ProcessLaunchInfo.FromPath("codalus-pluginhost"), "test", new Codalus.Rpc.SupervisionPolicy()));

    private string Write(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Ignore_rules_follow_gitignore_syntax()
    {
        var rules = IgnoreRules.Parse(
            """
            # build output
            bin/
            *.log
            !keep.log
            /rooted.txt
            docs/**/draft.md
            **/generated
            a?c.txt
            data[0-9].csv
            """);

        Assert.True(rules.IsIgnored("bin/Debug/app.dll"));
        Assert.True(rules.IsIgnored("src/App/bin/app.dll"));
        Assert.False(rules.IsIgnored("src/binary.cs"));
        Assert.True(rules.IsIgnored("trace.log"));
        Assert.True(rules.IsIgnored("src/trace.log"));
        Assert.False(rules.IsIgnored("keep.log"));
        Assert.True(rules.IsIgnored("rooted.txt"));
        Assert.False(rules.IsIgnored("src/rooted.txt"));
        Assert.True(rules.IsIgnored("docs/draft.md"));
        Assert.True(rules.IsIgnored("docs/a/b/draft.md"));
        Assert.True(rules.IsIgnored("x/generated/file.cs"));
        Assert.True(rules.IsIgnored("abc.txt"));
        Assert.False(rules.IsIgnored("abbc.txt"));
        Assert.True(rules.IsIgnored("data7.csv"));
        Assert.False(rules.IsIgnored("datax.csv"));

        // A folder pattern doesn't match a file of that name.
        Assert.Null(IgnoreRules.Parse("out/").Decide("out", isDirectory: false));
    }

    [Fact]
    public void Exclusions_read_gitignore_files_codalusignore_the_setting_and_secret_names()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));
        File.WriteAllText(Path.Combine(_root, ".git", "info", "exclude"), "local-only.txt\n");
        Write(".gitignore", "*.user\nartifacts/\n");
        Write("src/.gitignore", "!important.user\n");
        Write("src/App/.codalusignore", "Secrets/\n");
        var exclude = new List<string> { "**/*.private.cs" };
        var exclusions = new AiExclusions(() => exclude);
        var solution = Path.Combine(_root, "src");

        Assert.Null(exclusions.ExclusionReason(Write("src/App/Program.cs"), solution));
        Assert.Equal(".gitignore", exclusions.ExclusionReason(Write("src/App/App.csproj.user"), solution));
        Assert.Null(exclusions.ExclusionReason(Write("src/App/important.user"), solution));
        Assert.Equal(".gitignore", exclusions.ExclusionReason(Write("artifacts/log.txt"), _root));
        Assert.Equal(".codalusignore", exclusions.ExclusionReason(Write("src/App/Secrets/Keys.cs"), solution));
        Assert.Equal(".git/info/exclude", exclusions.ExclusionReason(Write("src/local-only.txt"), solution));
        Assert.Equal("ai.exclude", exclusions.ExclusionReason(Write("src/App/Db.private.cs"), solution));
        Assert.Equal("holds secrets", exclusions.ExclusionReason(Write("src/App/.env.local"), solution));
        Assert.Equal("holds secrets", exclusions.ExclusionReason(Write("src/App/secrets.json"), solution));
        Assert.Equal("holds secrets", exclusions.ExclusionReason(Write("certs/dev.pfx"), solution));

        // An ignore file is read again when it changes.
        var program = Path.Combine(_root, "src", "App", "Program.cs");
        File.WriteAllText(Path.Combine(_root, "src", "App", ".codalusignore"), "Secrets/\nProgram.cs\n");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "src", "App", ".codalusignore"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(".codalusignore", exclusions.ExclusionReason(program, solution));
    }

    [Fact]
    public void Secrets_are_redacted_but_code_that_names_them_stays()
    {
        var text =
            """
            {
              "ConnectionStrings": { "Default": "Server=db;User Id=sa;Password=Sup3rS3cret;" },
              "ApiKey": "abc123456",
              "Token": "${TOKEN}"
            }
            DB_PASSWORD=hunter2
            client_secret: shh-value
            var token = GetToken();
            const string password = "literal-secret";
            gh = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
            -----BEGIN RSA PRIVATE KEY-----
            MIIabc
            -----END RSA PRIVATE KEY-----
            """;

        var (redacted, count) = SecretRedactor.Redact(text);

        foreach (var secret in new[] { "Sup3rS3cret", "abc123456", "hunter2", "shh-value", "literal-secret", "ghp_abcdefghij", "MIIabc" })
        {
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        }

        Assert.Contains("Server=db;User Id=sa;Password=[redacted];", redacted, StringComparison.Ordinal);
        Assert.Contains("\"Token\": \"${TOKEN}\"", redacted, StringComparison.Ordinal);
        Assert.Contains("var token = GetToken();", redacted, StringComparison.Ordinal);
        Assert.Equal(7, count);
        Assert.Equal(("plain code", 0), SecretRedactor.Redact("plain code"));
    }

    [Fact]
    public void Prepare_drops_excluded_files_redacts_cuts_and_respects_restricted_mode()
    {
        using var workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user")), PlatformInfo.Current, watchFiles: false);
        workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        var trusted = true;
        var ai = new AiService(workbench.Settings, Models(workbench), workbench.Output, () => _root, () => trusted);
        Write(".gitignore", "obj/\n");
        var items = new[]
        {
            new AiContextItem(AiContextKind.CurrentFile, "Program.cs", "var pwd = \"x1\";") { Path = Write("Program.cs") },
            new AiContextItem(AiContextKind.OpenFile, "obj/gen.cs", "generated") { Path = Write("obj/gen.cs") },
            new AiContextItem(AiContextKind.OpenFile, "Big.cs", new string('a', AiService.MaxItemCharacters + 10)) { Path = Write("Big.cs") },
            new AiContextItem(AiContextKind.Diagnostics, "1 error", "Program.cs(1,1): error CS0001"),
        };

        var prepared = ai.Prepare(items);
        Assert.Equal(["Program.cs", "Big.cs", "1 error"], prepared.Included.Select(i => i.Label));
        Assert.Equal(".gitignore", Assert.Single(prepared.Excluded).Reason);
        Assert.Equal(1, prepared.Redactions);
        Assert.Contains("[redacted]", prepared.Included[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("[… cut: the rest wasn't sent]", prepared.Included[1].Content, StringComparison.Ordinal);

        var message = AiService.ComposeUserMessage("Why?", prepared);
        Assert.StartsWith("<context>", message, StringComparison.Ordinal);
        Assert.Contains("## Current file: Program.cs", message, StringComparison.Ordinal);
        Assert.EndsWith("Why?", message, StringComparison.Ordinal);

        trusted = false;
        var restricted = ai.Prepare(items);
        Assert.Equal(["1 error"], restricted.Included.Select(i => i.Label));
        Assert.All(restricted.Excluded, e => Assert.Equal("restricted mode", e.Reason));
    }

    [Fact]
    public async Task Only_the_users_settings_turn_AI_on_and_a_workspace_can_turn_it_off()
    {
        var workspaceFile = Write(".codalus/settings.json", "{}");
        using var workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user")), PlatformInfo.Current, workspaceFile, watchFiles: false);
        workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        var ai = new AiService(workbench.Settings, Models(workbench), workbench.Output, () => _root, () => true);
        var ct = TestContext.Current.CancellationToken;

        await workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.Workspace, ct);
        Assert.False(ai.IsEnabled);

        await workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, ct);
        Assert.True(ai.IsEnabled);

        await ai.SetOffForWorkspaceAsync(true, ct);
        Assert.False(ai.IsEnabled);
        Assert.True(ai.IsOffForWorkspace);
        Assert.Equal(AiState.Off, ai.State);

        await ai.SetOffForWorkspaceAsync(false, ct);
        Assert.True(ai.IsEnabled);
        Assert.Equal(AiState.NoProvider, ai.State);
    }

    [Fact]
    public void Code_blocks_are_parsed_and_Apply_picks_selection_file_or_caret()
    {
        var answer =
            """
            Here you go:

            ```csharp path=src/App/Program.cs
            using System;
            Console.WriteLine("hi");
            ```

            And a snippet:
            ~~~
            x++;
            ~~~

            ```cs
            unfinished
            """;

        var blocks = AiCodeBlocks.Parse(answer);
        Assert.Equal(3, blocks.Count);
        Assert.Equal(("csharp", "src/App/Program.cs"), (blocks[0].Language, blocks[0].Path));
        Assert.Equal("using System;\nConsole.WriteLine(\"hi\");\n", blocks[0].Code);
        Assert.Equal(("", null, "x++;\n"), (blocks[1].Language, blocks[1].Path, blocks[1].Code));
        Assert.Equal("unfinished\n", blocks[2].Code);

        var file = Path.Combine("repo", "src", "App", "Program.cs");
        Assert.Equal(AiApplyKind.ReplaceFile, AiCodeBlocks.Plan(blocks[0], "using System;\nold();\n", file, hasSelection: true));
        Assert.Equal(AiApplyKind.ReplaceSelection, AiCodeBlocks.Plan(blocks[1], "using System;\n", file, hasSelection: true));
        Assert.Equal(AiApplyKind.Insert, AiCodeBlocks.Plan(blocks[1], "using System;\n", file, hasSelection: false));

        // No path, but it starts like the file and is about as long: the whole file.
        var whole = new AiCodeBlock("csharp", null, "using System;\nnamespace A;\nclass B { }\n");
        Assert.Equal(AiApplyKind.ReplaceFile, AiCodeBlocks.Plan(whole, "using System;\nnamespace A;\nclass C { }\n", file, hasSelection: false));
        Assert.False(AiCodeBlocks.NamesFile("Other.cs", file));
        Assert.True(AiCodeBlocks.NamesFile("./App/Program.cs", file));
    }
}
