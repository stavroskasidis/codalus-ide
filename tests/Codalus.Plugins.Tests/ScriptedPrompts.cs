using Codalus.Plugins.Protocol;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>
/// Answers a plugin's quick picks and input boxes from a script, through the real plugin host: a pick chooses the item
/// whose description is the next answer, an input box returns it. What was asked, and the Browse kind of each input box,
/// are kept.
/// </summary>
internal sealed class ScriptedPrompts(Queue<string> answers) : IPluginPrompts
{
    public List<string> Asked { get; } = [];

    /// <summary>The quick picks' items as they reached the shell.</summary>
    public List<IReadOnlyList<QuickPickItemDto>> Picks { get; } = [];

    /// <summary>The inputs that had a Browse button, and what it browses for.</summary>
    public Dictionary<string, string> Browse { get; } = [];

    public Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken)
    {
        Asked.Add(placeholder ?? "pick");
        Picks.Add(items);
        var wanted = answers.Dequeue();
        return Task.FromResult<int?>(items.ToList().FindIndex(i => i.Description == wanted));
    }

    public Task<string?> InputAsync(string title, string? prompt, string? value, string? browse, string source, CancellationToken cancellationToken)
    {
        Asked.Add(title);
        if (browse is not null)
        {
            Browse[title] = browse;
        }

        return Task.FromResult<string?>(answers.Dequeue());
    }
}
