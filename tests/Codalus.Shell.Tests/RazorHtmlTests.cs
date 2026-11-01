using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Node;

namespace Codalus.Shell.Tests;

/// <summary>
/// M16d (ADR 0027): HTML and CSS inside a Razor file (samples/Blazor's Counter.razor) from the HTML server, next to
/// the C# host: element and attribute completion and hover in markup, CSS in a style block, C# alone in @code.
/// </summary>
public sealed class RazorHtmlTests
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    [AvaloniaFact]
    public async Task Html_and_css_inside_razor_come_from_the_html_server()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        try
        {
            await using var f = ShellFixture.Create();
            var install = f.Shell.InstallWebLanguagesAsync();
            await ShellFixture.PumpUntil(() => install.IsCompleted, seconds: 600);
            await install;

            var folder = f.CopySample("Blazor");
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Blazor.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
            await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 180);

            var counter = Path.Combine(folder, "BlazorApp", "Components", "Pages", "Counter.razor");
            var document = await f.Shell.OpenFileAsync(counter, preview: false, null);
            await ShellFixture.PumpUntil(() => document?.Controller is not null);
            var editor = document!.Controller!;
            var model = document.Model;
            var heading = model.Text.Text.IndexOf("<h1>Counter</h1>", StringComparison.Ordinal) + "<h1>Counter</h1>".Length;
            model.Text.Insert(heading, "\n<style>\n    .note { colo }\n</style>\n<blockq");
            var button = model.Text.Text.IndexOf("class=\"btn btn-primary\"", StringComparison.Ordinal) + "class=\"btn btn-primary\"".Length;
            model.Text.Insert(button, " tabi");

            async Task<IReadOnlyList<string>> CompleteAsync(int offset)
            {
                editor.Editor.CaretOffset = offset;
                editor.ShowCompletion();
                try
                {
                    await ShellFixture.PumpUntil(() => editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Count > 0, seconds: 90);
                }
                catch (Xunit.Sdk.XunitException)
                {
                    Assert.Fail($"No completion at '{model.Text.GetText(Math.Max(0, offset - 20), 20)}'. Log:\n{f.Shell.LanguageLog.GetText()}");
                }

                var labels = editor.CompletionWindow!.CompletionList.CompletionData.Select(d => d.Text).ToList();
                editor.CompletionWindow.Close();
                await ShellFixture.PumpUntil(() => editor.CompletionWindow is null);
                return labels;
            }

            // An element only the HTML server knows, an attribute, and a CSS property in a style block. Without the HTML
            // server's items the list is the C# host's alone: the log says why (an embedded completion that failed, the
            // server's start).
            void AssertOffered(string expected, IReadOnlyList<string> labels) =>
                Assert.True(labels.Contains(expected), $"'{expected}' isn't among the {labels.Count} items ({string.Join(", ", labels.Take(30))}…). Log:\n{f.Shell.LanguageLog.GetText()}");
            AssertOffered("blockquote", await CompleteAsync(model.Text.Text.IndexOf("<blockq", StringComparison.Ordinal) + "<blockq".Length));
            AssertOffered("tabindex", await CompleteAsync(model.Text.Text.IndexOf(" tabi", StringComparison.Ordinal) + " tabi".Length));
            AssertOffered("color", await CompleteAsync(model.Text.Text.IndexOf("colo }", StringComparison.Ordinal) + "colo".Length));

            // In @code it is C#: the C# host's members, no HTML.
            var code = await CompleteAsync(model.Text.Text.IndexOf("currentCount++", StringComparison.Ordinal) + "curr".Length);
            Assert.Contains("currentCount", code);
            Assert.DoesNotContain("blockquote", code);

            // Hover on an HTML element: the HTML server's description.
            var embedded = await ((IEditorServices)f.Shell).EmbeddedAsync(model, model.Text.Text.IndexOf("<h1>", StringComparison.Ordinal) + 2);
            Assert.NotNull(embedded);
            var hover = await embedded.Client.HoverAsync(embedded.Uri, model.PositionAt(model.Text.Text.IndexOf("<h1>", StringComparison.Ordinal) + 2), ShellFixture.RequestTimeout());
            Assert.Contains("heading", hover?.Contents.Value, StringComparison.OrdinalIgnoreCase);

            // The HTML document's diagnostics (blanked C# would be false alarms) never reach the Error List.
            Assert.Empty(f.Shell.Languages.Diagnostics.For(counter + RazorHtmlProjection.VirtualSuffix));

            // Scoped CSS: MainLayout.razor.css nests under its component, and its selectors complete the component's
            // class names and ::deep.
            var layout = Path.Combine(folder, "BlazorApp", "Components", "Layout", "MainLayout.razor");
            static Workbench.Projects.ExplorerNode? Find(Workbench.Projects.ExplorerNode node, string path) =>
                string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase) ? node : node.Children.Select(c => Find(c, path)).FirstOrDefault(n => n is not null);
            await ShellFixture.PumpUntil(() => f.Shell.Explorer.Root is { } root && Find(root, layout) is { } node
                && node.Children.Any(c => string.Equals(c.Path, layout + ".css", StringComparison.OrdinalIgnoreCase)), seconds: 60);
            var scoped = await f.Shell.OpenFileAsync(layout + ".css", preview: false, null);
            await ShellFixture.PumpUntil(() => scoped?.Controller is not null && f.Shell.Languages.RunningClient("css") is not null, seconds: 120);
            editor = scoped!.Controller!;
            model = scoped.Model;
            model.Text.Insert(model.Text.TextLength, "\n.rel\n.card ::de");
            Assert.Contains("reload", await CompleteAsync(model.Text.Text.IndexOf(".rel", StringComparison.Ordinal) + ".rel".Length));
            Assert.Contains("::deep", await CompleteAsync(model.Text.TextLength));

            // Committing it replaces what its edit covers, the "::" included.
            editor.Editor.CaretOffset = model.Text.TextLength;
            editor.ShowCompletion();
            await ShellFixture.PumpUntil(() => editor.CompletionWindow?.CompletionList.CompletionData.Any(d => d.Text == "::deep") == true, seconds: 60);
            editor.CompletionWindow!.CompletionList.SelectedItem = editor.CompletionWindow.CompletionList.CompletionData.First(d => d.Text == "::deep");
            editor.CompletionWindow.CompletionList.RequestInsertion(EventArgs.Empty);
            await ShellFixture.PumpUntil(() => editor.CompletionWindow is null);
            Assert.EndsWith(".card ::deep", model.Text.Text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, null);
        }
    }

    // ------------------------------------------------------------------ the HTML's sync, off the UI thread (M57)

    /// <summary>A shell whose HTML server is the conformance server, logging to <c>html.log</c>, with a Razor file open.</summary>
    private static async Task<(ShellFixture F, CodeEditorDocument Document, string Log)> RazorWithLoggingHtmlServerAsync(string text)
    {
        var f = ShellFixture.Create();
        var log = Path.Combine(f.UserData, "html.log");
        f.Shell.Workbench.LanguageServers.Register(new LanguageServiceDescriptor("html", "HTML (conformance)", [], null, () => Rpc.ProcessLaunchInfo.FromPath(EditorLargeTextTests.ConformanceServer, "--log", log))
        {
            Languages = ["html"],
        });
        var document = await OpenRazorAsync(f, text);
        return (f, document, log);
    }

    private static async Task<CodeEditorDocument> OpenRazorAsync(ShellFixture f, string text)
    {
        var path = Path.Combine(f.UserData, "Page.razor");
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        }

        var document = (CodeEditorDocument)(await f.Shell.OpenFileAsync(path, preview: false, null))!;
        Assert.Equal(ShellHost.RazorLanguageId, document.Model.LanguageId);
        return document;
    }

    /// <summary>What the HTML server has for <paramref name="embedded"/>'s document, once a hover after everything queued came back.</summary>
    private static async Task<string> ServerHtmlAsync(EmbeddedRequest embedded, string log)
    {
        var hover = embedded.Client.HoverAsync(embedded.Uri, new Position(0, 1), TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => hover.IsCompleted);
        await hover;
        return await File.ReadAllTextAsync(log + ".text", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Two projections overlap, the older one ending last: it is dropped, and the HTML server keeps the newer HTML at the
    /// Razor file's version (M57: projected on the pool, each checked against the version already sent, ADR 0078's rule).
    /// </summary>
    [AvaloniaFact]
    public async Task A_projection_older_than_the_one_sent_is_dropped()
    {
        var (f, document, log) = await RazorWithLoggingHtmlServerAsync("<h1>One</h1>\n<p>text</p>\n");
        await using var _ = f;
        var model = document.Model;
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Shell.ProjectRazorHtml = razor =>
        {
            if (razor.Contains("One", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            return RazorHtmlProjection.Project(razor);
        };

        var services = (IEditorServices)f.Shell;
        var older = services.EmbeddedAsync(model, 2);
        await ShellFixture.PumpUntil(() => entered.Task.IsCompleted);
        model.Text.Replace(model.Text.Text.IndexOf("One", StringComparison.Ordinal), 3, "Two");
        var newer = services.EmbeddedAsync(model, 2);
        await ShellFixture.PumpUntil(() => newer.IsCompleted, seconds: 60);
        gate.Set();
        await ShellFixture.PumpUntil(() => older.IsCompleted);
        Assert.NotNull(await older);

        var embedded = (await newer)!;
        Assert.Equal(RazorHtmlProjection.Project(model.Text.Text), await ServerHtmlAsync(embedded, log));
        Assert.Equal([$"didOpen {model.Version} {model.Text.TextLength}", "hover"], File.ReadAllLines(log));
    }

    /// <summary>
    /// A Razor file closed while its HTML is projected: the late projection opens nothing, and the file opened again gets
    /// its own HTML (M57 review: the closed file's HTML was opened after its close, at a version the new model, counting
    /// from 1 again, stayed below, so the server answered from the old text).
    /// </summary>
    [AvaloniaFact]
    public async Task A_projection_finishing_after_its_file_closed_opens_nothing()
    {
        var (f, document, log) = await RazorWithLoggingHtmlServerAsync("<h1>First</h1>\n<p>text</p>\n");
        await using var _ = f;
        var model = document.Model;
        for (var i = 0; i < 5; i++)
        {
            model.Text.Insert(0, "<b>Edited</b>");
        }

        await f.Shell.SaveActiveDocumentAsync(); // so it closes without asking

        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Shell.ProjectRazorHtml = razor =>
        {
            if (razor.Contains("Edited", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            return RazorHtmlProjection.Project(razor);
        };

        var services = (IEditorServices)f.Shell;
        var late = services.EmbeddedAsync(model, 2);
        await ShellFixture.PumpUntil(() => entered.Task.IsCompleted);
        f.Shell.Layout.CloseDocument(document);
        await ShellFixture.PumpUntil(() => !f.Shell.Layout.Documents.Contains(document));
        gate.Set();
        await ShellFixture.PumpUntil(() => late.IsCompleted);
        Assert.Null(await late);

        // Changed on disk while closed (another tool, a checkout), then opened again: a new model, from version 1.
        const string Reopened = "<h1>Reopened</h1>\n<p>text</p>\n";
        await File.WriteAllTextAsync(model.Path, Reopened, TestContext.Current.CancellationToken);
        var reopened = await OpenRazorAsync(f, Reopened);
        Assert.NotSame(model, reopened.Model);
        Assert.Equal(Reopened, reopened.Model.Text.Text);
        var embedded = await services.EmbeddedAsync(reopened.Model, 2);
        Assert.NotNull(embedded);
        Assert.Equal(RazorHtmlProjection.Project(Reopened), await ServerHtmlAsync(embedded, log));
        Assert.Equal([$"didOpen {reopened.Model.Version} {Reopened.Length}", "hover"], File.ReadAllLines(log));
    }
}
