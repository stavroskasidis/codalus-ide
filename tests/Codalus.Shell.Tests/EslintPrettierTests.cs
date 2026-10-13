using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Node;

namespace Codalus.Shell.Tests;

/// <summary>
/// M16d (ADR 0027): samples/WebLint with its own ESLint and Prettier (installed with <c>npm ci</c>, through a shared npm
/// cache). ESLint lints the open file next to the TypeScript server, with its quick fixes; Format Document runs Prettier.
/// </summary>
public sealed class EslintPrettierTests
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    [AvaloniaFact]
    public async Task Eslint_lints_with_quick_fixes_and_prettier_formats_the_document()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        try
        {
            await using var f = ShellFixture.Create();
            var install = f.Shell.InstallWebLanguagesAsync();
            await ShellFixture.PumpUntil(() => install.IsCompleted, seconds: 600);
            await install;

            // The project's own tools, pinned by its lockfile.
            var folder = f.CopySample("WebLint");
            var output = new List<string>();
            var restore = Npm.RunAsync(Npm.BuiltIn(f.Shell.Node), ["ci", "--no-audit", "--no-fund", "--prefer-offline", "--cache", Path.Combine(SharedCache, "npm-cache")], folder,
                line =>
                {
                    lock (output)
                    {
                        output.Add(line);
                    }
                }, TestContext.Current.CancellationToken);
            await ShellFixture.PumpUntil(() => restore.IsCompleted, seconds: 300);
            Assert.True(await restore == 0, "npm ci failed:\n" + string.Join('\n', output));

            // ESLint: the warning and the error of src/app.js, next to the TypeScript server's diagnostics.
            var app = Path.Combine(folder, "src", "app.js");
            var document = await f.Shell.OpenFileAsync(app, preview: false, null);
            await ShellFixture.PumpUntil(() => document?.Controller is not null);
            try
            {
                await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(app) is var d && d.Any(x => x.Code == "no-unused-vars") && d.Any(x => x.Code == "eqeqeq"), seconds: 180);
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail("No ESLint diagnostics: " + string.Join("; ", f.Shell.Languages.Diagnostics.For(app).Select(d => $"{d.Code} {d.Message}")) + "\nLog:\n" + f.Shell.LanguageLog.GetText());
            }

            var diagnostics = f.Shell.Languages.Diagnostics.For(app);
            var unused = diagnostics.Single(d => d.Code == "no-unused-vars");
            Assert.Equal(DiagnosticSeverity.Warning, unused.Severity);
            Assert.Equal("eslint", unused.Source);
            Assert.Equal(DiagnosticSeverity.Error, diagnostics.Single(d => d.Code == "eqeqeq").Severity);

            // Its quick fixes: the rule's own fix (a command, whose edit the server sends back), and disabling the rule.
            var linter = Assert.Single(f.Shell.Languages.RunningLinters(app));
            var model = document!.Model;
            var eqeqeq = diagnostics.Single(d => d.Code == "eqeqeq");
            var actions = await linter.CodeActionsAsync(model.Uri, eqeqeq.Range, [.. diagnostics], ShellFixture.RequestTimeout());
            Assert.Contains(actions, a => a.Title.Contains("Disable eqeqeq for this line", StringComparison.Ordinal));
            var fix = actions.FirstOrDefault(a => a.Title.Contains("Use '===' instead of '=='", StringComparison.Ordinal) && (a.Command is not null || a.Edit is not null));
            Assert.True(fix is not null, "Actions: " + string.Join(", ", actions.Select(a => a.Title)));
            if (fix.Command is { } command)
            {
                await linter.ExecuteCommandAsync(command, ShellFixture.RequestTimeout());
            }
            else
            {
                await f.Shell.ApplyWorkspaceEditAsync(fix.Edit!);
            }

            await ShellFixture.PumpUntil(() => model.Text.Text.Contains("item.count === 0", StringComparison.Ordinal), seconds: 30);
            await ShellFixture.PumpUntil(() => !f.Shell.Languages.Diagnostics.For(app).Any(d => d.Code == "eqeqeq"), seconds: 60);

            // Prettier: Format Document runs the project's Prettier (its configuration: semicolons, one statement a line).
            f.Shell.Run("editor.action.formatDocument");
            await ShellFixture.PumpUntil(() => model.Text.Text.Contains("let sum = 0;", StringComparison.Ordinal), seconds: 60);
            Assert.Contains("    if (item.count === 0) {", model.Text.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("console.log(total([{ count: 1 }, { count: 2 }]));", model.Text.Text, StringComparison.Ordinal);
            Assert.Contains("[prettier] Formatted app.js", f.Shell.LanguageLog.GetText(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, null);
        }
    }
}
