using Codalus.Shell.ViewModels;
using Codalus.Workbench.Search;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M47's smoke step: Code Search in the shipped app on the BuildAndRun solution. The index is built and saved next to
/// the solution; a query finds the Greeter type, the preview shows Greeter.cs at it with the match selected (the
/// website's picture, <c>smoke-test-code-search.png</c>); the prefixes filter; Enter opens the result; Feature Search
/// lists a command with its key.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task CodeSearchStepAsync()
    {
        var search = shell.CodeSearch;
        await WaitAsync(() => shell.CodeSearchIndex.IsComplete && shell.CodeSearchIndex.SymbolCount > 0, "the C# host indexes the solution", 120).ConfigureAwait(true);
        var saved = Path.Combine(shell.SolutionStateDirectory ?? string.Empty, CodeSearchIndex.FileName);
        await WaitAsync(() => File.Exists(saved), "the index is saved next to the solution").ConfigureAwait(true);
        await OpenAsync(Program).ConfigureAwait(true);

        shell.Run("workbench.action.gotoAll");
        await WaitAsync(() => search is { IsFloatingOpen: true, IsCodeTab: true }, "Ctrl+T opens Code Search").ConfigureAwait(true);
        search.ShowPreview = true;
        search.Query = "Greet";
        await WaitAsync(() => search.Rows.Any(r => r.Result is { Name: "Greeter", Kind: CodeSearchKind.Type }), "Code Search finds the Greeter type", 30).ConfigureAwait(true);
        var greeter = search.Rows.First(r => r.Result is { Name: "Greeter", Kind: CodeSearchKind.Type });
        Check(greeter.Detail == "in Greetings (project Greetings)" && greeter.FileLabel == "Greeter.cs", $"the result says where it is ({greeter.Detail}, {greeter.FileLabel})");
        await WaitAsync(() => search.Rows.Any(r => r.Result is { Name: "Greet", Kind: CodeSearchKind.Member }) && search.Rows.Any(r => r.Result?.Kind == CodeSearchKind.File), "members and files are listed too", 30).ConfigureAwait(true);
        search.SelectedIndex = search.Rows.IndexOf(greeter);
        await WaitAsync(() => shell.CodeSearchPreview is { } preview && preview.Editor.SelectedText == "Greeter" && preview.Controller.Model.Path == Greeter, "the preview shows Greeter.cs with the match selected", 30).ConfigureAwait(true);
        await WaitAsync(() => !search.IsIncomplete, "the results are complete").ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-code-search.png");

        search.Query = "f:Program";
        await WaitAsync(() => search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Kind == CodeSearchKind.File), "f: lists files only").ConfigureAwait(true);
        search.Query = "m:Greet";
        await WaitAsync(() => search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Kind == CodeSearchKind.Member), "m: lists members only").ConfigureAwait(true);
        search.SelectedIndex = search.Rows.IndexOf(search.Rows.First(r => r.Result?.Name == "Greet"));
        await search.AcceptAsync().ConfigureAwait(true);
        await WaitAsync(() => !search.IsOpen && shell.ActiveEditor is { Model.Name: "Greeter.cs", Controller: not null }, "Enter opens Greeter.cs and closes the window", 30).ConfigureAwait(true);

        shell.Run("workbench.action.featureSearch");
        await WaitAsync(() => search is { IsFloatingOpen: true, IsFeatureTab: true }, "Ctrl+Q opens Feature Search").ConfigureAwait(true);
        search.Query = "build solution";
        await WaitAsync(() => search.Rows.Any(r => r.Feature is { Kind: FeatureSearchKind.Command } && r.HasKeyLabel), "Feature Search lists a command with its key", 30).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
        Screenshot("smoke-test-feature-search.png");
        search.Close();
        search.TabIndex = CodeSearchViewModel.CodeTab;
    }
}
