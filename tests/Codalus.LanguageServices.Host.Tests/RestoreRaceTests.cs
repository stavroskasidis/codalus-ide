using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Languages;
using Codalus.Workbench.Projects;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// A solution whose first design-time builds run before the restore has brought the reference pack (the smoke test's VB
/// step on the CI runners, whose SDK 11 gets .NET 10's reference pack from NuGet): the projects load without their
/// framework, and a completion asked then must not leave Roslyn's providers disabled once the restore lands.
/// </summary>
public sealed class RestoreRaceTests
{
    [Fact]
    public async Task Completion_asked_before_the_framework_arrives_works_once_it_does()
    {
        var root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-race-" + Guid.NewGuid().ToString("N"));
        var fixture = RepoPaths.CopyFixture("VisualBasic", root);

        // No targeting packs next to the SDK: the reference pack comes from the restore, as on a fresh CI runner.
        var packs = Path.Combine(root, ".packs");
        Directory.CreateDirectory(packs);
        async Task<IReadOnlyDictionary<string, string>?> Environment(string directory, CancellationToken ct) =>
            new Dictionary<string, string>(await BuildHostRuntime.EnvironmentAsync(directory, ct) ?? new Dictionary<string, string>()) { ["NETCoreTargetingPackRoot"] = packs, ["AllowMissingPrunePackageData"] = "true" };

        var policy = new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) };
        var log = new List<string>();
        await using var solutions = new SolutionService(() => RepoPaths.BuildHostAssembly, policy, Environment);
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("csharp", "C#", [".cs"], "C#", () => ProcessLaunchInfo.FromPath(RepoPaths.HostAssembly)) { Languages = ["csharp", "vb"], MoreProjectLanguages = ["VB"] });
        await using var languages = new LanguageService(registry, solutions, policy);
        languages.Log += (_, line) => { lock (log) { log.Add(line); } };
        solutions.Log += (_, line) => { lock (log) { log.Add("[build] " + line); } };
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await solutions.OpenAsync(Path.Combine(fixture, "VisualBasic.slnx"), trusted: true, restore: false, ct);
            await LanguageFixture.WaitUntilAsync(() => solutions.Progress is { Completed: true } && languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 4 });
            var console = Path.Combine(fixture, "VbConsole", "VbConsole.vbproj");
            Assert.False(VbRuntime(solutions, console), "The first load was meant to be without the framework.");

            var program = Path.Combine(fixture, "VbConsole", "Program.vb");
            var text = await File.ReadAllTextAsync(program, ct);
            await languages.OpenAsync(new LanguageDocument(program, "vb", () => (text, 1)));
            var offset = text.IndexOf("Console.WriteLine(formatter.Format(", StringComparison.Ordinal) + "Console.WriteLine(formatter.".Length;
            var line = text[..offset].Count(c => c == '\n');
            var position = new Position(line, offset - (text.LastIndexOf('\n', offset - 1) + 1));
            async Task<string[]> LabelsAsync()
            {
                var client = await languages.ClientAsync("csharp") ?? throw new InvalidOperationException("The C# service isn't running.");
                var list = await client.CompletionAsync(DocumentUris.FromPath(program), position, new CompletionContext(CompletionTriggerKind.Invoked, null), ct);
                return [.. list?.Items.Select(i => i.Label) ?? []];
            }

            // Asked while the project has no framework: nothing yet (Roslyn's providers would throw here and be disabled).
            Assert.DoesNotContain("Currency", await LabelsAsync());
            var hoverAt = new Position(line, position.Character - 2);
            var client0 = await languages.ClientAsync("csharp");
            Assert.Null(await client0!.HoverAsync(DocumentUris.FromPath(program), hoverAt, ct));

            await solutions.RestoreAsync(null, ct);
            try
            {
                await LanguageFixture.WaitUntilAsync(() => VbRuntime(solutions, console), TimeSpan.FromMinutes(2));
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail($"No framework after the restore ({solutions.RestoreState}):\n" + string.Join('\n', log.TakeLast(30)));
            }

            var labels = Array.Empty<string>();
            for (var attempt = 0; attempt < 60 && !labels.Contains("Currency"); attempt++)
            {
                labels = await LabelsAsync();
                if (!labels.Contains("Currency"))
                {
                    await Task.Delay(1000, ct);
                }
            }

            Assert.True(labels.Contains("Currency"), "formatter. didn't complete PriceFormatter's members:\n" + string.Join('\n', log.TakeLast(20)));
            Assert.Contains("PriceFormatter", (await (await languages.ClientAsync("csharp"))!.HoverAsync(DocumentUris.FromPath(program), hoverAt, ct))?.Contents.Value, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool VbRuntime(SolutionService solutions, string project) =>
        solutions.CompilerInputs.TryGetValue(project, out var inputs) && inputs.Arguments?.Any(a => a.Contains("vbruntime", StringComparison.OrdinalIgnoreCase)) == true;
}
