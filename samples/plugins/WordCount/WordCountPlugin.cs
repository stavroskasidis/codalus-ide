using System.Globalization;
using System.Text.Json;
using Codalus.Plugins;
using Codalus.Plugins.Proposed;

namespace WordCount;

/// <summary>
/// A tool for AI agents (proposed, ADR 0092): <c>count_words</c>, declared in the manifest's <c>contributes.aiTools</c> and
/// registered when an agent first calls it. The user allows each call; what it returns is redacted before an agent sees it.
/// </summary>
public sealed class WordCountPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Subscriptions.Add(context.Proposed().AiTools.RegisterTool(new CountWords()));
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class CountWords() : AiTool("count_words")
    {
        public override Task<AiToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            // The arguments come from a model: check them.
            if (!arguments.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                throw new AiToolException("count_words needs a text.");
            }

            var words = text.GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var top = words.GroupBy(w => w.ToLowerInvariant()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            return Task.FromResult(new AiToolResult(string.Create(CultureInfo.InvariantCulture,
                $"{words.Length} word(s){(top is null ? "" : $"; the most frequent is '{top.Key}' ({top.Count()})")}.")));
        }
    }
}
