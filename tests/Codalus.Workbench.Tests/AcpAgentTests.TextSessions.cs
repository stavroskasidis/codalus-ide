using System.Text.Json;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The IDE's AI features through AI Chat's external agent (ADR 0097): a text session of their own on the agent's connection,
/// with no tools and every request refused; a small model for suggestions; the gate of ADR 0079.
/// </summary>
public sealed partial class AcpAgentTests
{
    /// <summary>The stand-in, picked in AI Chat, with suggestions on; the features may start it through the host.</summary>
    private async Task PickAgentForFeaturesAsync(string extraEnv = "")
    {
        await ConfigureAsync(extraEnv);
        await _workbench.Settings.SetAsync(AcpAgents.ChatAgentSetting, "stand-in", SettingScope.User, Ct);
        await _workbench.Settings.SetAsync(AiCompletions.EnabledSetting, true, SettingScope.User, Ct);
        _agents.Host = _host;
    }

    private List<JsonElement> PromptsTo(string sessionId) =>
        [.. Received("session/prompt").Where(m => m.GetProperty("params").GetProperty("sessionId").GetString() == sessionId)];

    private static string PromptText(JsonElement prompt) =>
        string.Concat(prompt.GetProperty("params").GetProperty("prompt").EnumerateArray().Select(b => b.GetProperty("text").GetString()));

    [Fact]
    public async Task A_completion_goes_to_the_chats_agent_in_a_text_session_with_no_tools_and_the_chat_sees_nothing()
    {
        await PickAgentForFeaturesAsync("ACP_FAKE_TEXT_REPLY=return 42");
        Assert.Equal("stand-in", _ai.ExternalAgent);
        Assert.Equal(AiState.NoProvider, _ai.State); // no provider at all: the agent answers
        Assert.True(_ai.CanAnswer);
        var completions = new AiCompletions(_ai, _workbench.Settings);
        Assert.True(completions.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(AiCompletions.DefaultAgentDelay), completions.Delay);

        var path = Path.Combine(_root, "src", "A.cs");
        var suggestion = await completions.CompleteAsync(new AiCompletionRequest(path, "csharp", "class A\n{\n    int X() => ", ";\n}\n"), Ct);
        Assert.Equal("return 42", suggestion);

        // AI Chat's session (with codalus mcp), then the text session: the workspace, and no MCP server.
        var sessions = Received("session/new").Select(m => m.GetProperty("params")).ToList();
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions[0].GetProperty("mcpServers").EnumerateArray());
        Assert.Equal(_root, sessions[1].GetProperty("cwd").GetString());
        Assert.Empty(sessions[1].GetProperty("mcpServers").EnumerateArray());
        Assert.Equal(1, _host.McpStarted);

        // The prompt: the preamble, the completion's instructions and the file with the cursor; the chat got none.
        var prompt = PromptText(Assert.Single(PromptsTo("sess_text_1")));
        Assert.StartsWith(AiService.ExternalAgentPreamble, prompt, StringComparison.Ordinal);
        Assert.Contains("Instructions:\n" + AiCompletions.SystemPrompt, prompt, StringComparison.Ordinal);
        Assert.Contains("int X() => " + AiCompletions.CursorMarker + ";", prompt, StringComparison.Ordinal);
        Assert.Empty(PromptsTo("sess_stand_in"));
        Assert.Contains("completion: external agent stand-in (its own provider)", AiLog, StringComparison.Ordinal);
        Assert.Contains("(suggestions, no tools", AiLog, StringComparison.Ordinal);

        // The next one reuses the session.
        await completions.CompleteAsync(new AiCompletionRequest(path, "csharp", "x", "y"), Ct);
        Assert.Equal(2, Received("session/new").Count);
        Assert.Equal(2, PromptsTo("sess_text_1").Count);
    }

    [Fact]
    public async Task Suggestions_use_a_small_model_or_the_users_pick_and_actions_the_chats_model()
    {
        await PickAgentForFeaturesAsync("ACP_FAKE_CHOICES=config;ACP_FAKE_FAST_MODEL=1;ACP_FAKE_TEXT_REPLY={model}");
        var completions = new AiCompletions(_ai, _workbench.Settings);
        var path = Path.Combine(_root, "src", "A.cs");

        // Guessed by its name: haiku, while the chat keeps sonnet.
        Assert.Equal("haiku", await completions.CompleteAsync(new AiCompletionRequest(path, "csharp", "a", "b"), Ct));
        Assert.Equal("sonnet", _agents.Current!.Choices.Single(c => c.Category == AcpSessionChoices.ModelCategory).CurrentValue);
        Assert.Contains("model haiku, guessed small by its name", AiLog, StringComparison.Ordinal);

        // An action uses the chat's model, in a session of its own.
        var (reply, _) = await _ai.AskAsync("commit message", AiActions.CommitMessagePrompt, "the diff", [], Ct);
        Assert.Equal("sonnet", reply.Text);
        Assert.Single(PromptsTo("sess_text_2"));

        // The user's pick is kept in the entry, and the next suggestion uses it.
        await _agents.SaveSuggestionsModelAsync("stand-in", "opus", Ct);
        Assert.Equal("opus", _agents.Configured["stand-in"].SuggestionsModel);
        Assert.Equal("opus", await completions.CompleteAsync(new AiCompletionRequest(path, "csharp", "a", "b"), Ct));
        Assert.Contains("model opus, picked for suggestions", AiLog, StringComparison.Ordinal);

        // Automatic again: the pick is removed, the rest of the entry kept.
        await _agents.SaveSuggestionsModelAsync("stand-in", null, Ct);
        Assert.Null(_agents.Configured["stand-in"].SuggestionsModel);
        Assert.Equal(Dotnet, _agents.Configured["stand-in"].Command);
    }

    [Fact]
    public async Task A_text_session_refuses_every_file_terminal_and_permission_request_without_asking_the_user()
    {
        await PickAgentForFeaturesAsync("ACP_FAKE_TEXT_PROBE=1");
        var (reply, _) = await _ai.AskAsync("fix", AiActions.FixPrompt, "the code", [], Ct);
        Assert.Equal("text reply", reply.Text);

        // Nothing reached the user: no read through the editor, no command, no approval.
        Assert.Empty(_host.Commands);
        Assert.Empty(_host.Permissions);
        Assert.Empty(_host.Proposed);
        var answers = LogLines().Where(m => m.TryGetProperty("id", out _) && !m.TryGetProperty("method", out _)).ToList();
        Assert.Equal(2, answers.Count(a => a.TryGetProperty("error", out _)));
        Assert.Contains(answers, a => a.TryGetProperty("result", out var result) && result.TryGetProperty("outcome", out var outcome) && outcome.GetProperty("outcome").GetString() == "cancelled");
        Assert.Contains("fs/read_text_file → refused (AI actions ask for text only)", AiLog, StringComparison.Ordinal);
        Assert.Contains("terminal/create → refused (AI actions ask for text only)", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_features_follow_the_gate_and_without_a_picked_agent_need_a_provider()
    {
        await PickAgentForFeaturesAsync();
        var completions = new AiCompletions(_ai, _workbench.Settings);
        var path = Path.Combine(_root, "src", "A.cs");

        // An untrusted folder: no suggestion, and an action says why; nothing starts.
        _trusted = false;
        Assert.Null(await completions.CompleteAsync(new AiCompletionRequest(path, "csharp", "a", "b"), Ct));
        var refused = await Assert.ThrowsAsync<AiUnavailableException>(() => _ai.AskAsync("fix", AiActions.FixPrompt, "the code", [], Ct));
        Assert.Contains("trusted folders", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Received("initialize"));

        // The built-in assistant picked again: the provider answers, and there is none.
        _trusted = true;
        await _workbench.Settings.SetAsync(AcpAgents.ChatAgentSetting, "", SettingScope.User, Ct);
        Assert.Null(_ai.ExternalAgent);
        Assert.False(_ai.CanAnswer);
        Assert.False(completions.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(AiCompletions.DefaultDelay), completions.Delay);
        var noProvider = await Assert.ThrowsAsync<AiUnavailableException>(() => _ai.AskAsync("fix", AiActions.FixPrompt, "the code", [], Ct));
        Assert.True(noProvider.NeedsConfiguration);

        // A picked agent that isn't in ai.agents answers nothing; AI off, nothing either.
        await _workbench.Settings.SetAsync(AcpAgents.ChatAgentSetting, "gone", SettingScope.User, Ct);
        Assert.Null(_ai.ExternalAgent);
        await _workbench.Settings.SetAsync(AcpAgents.ChatAgentSetting, "stand-in", SettingScope.User, Ct);
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, Ct);
        Assert.Null(_ai.ExternalAgent);
        Assert.False(_ai.CanAnswer);

        // A delay the user set holds with an agent too.
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        await _workbench.Settings.SetAsync(AiCompletions.DelaySetting, 250, SettingScope.User, Ct);
        Assert.Equal(TimeSpan.FromMilliseconds(250), completions.Delay);
    }

    [Fact]
    public async Task A_cancelled_suggestion_sends_session_cancel_to_its_text_session_only()
    {
        await PickAgentForFeaturesAsync("ACP_FAKE_TEXT_SLOW=1");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var asked = _ai.ChatAsync("completion", [new Codalus.Plugins.Protocol.ChatMessageDto("user", "x", null, null, false)], AiPreparedContext.None, _ => { }, cts.Token, quiet: true, route: AiRoute.Suggestion);
        for (var i = 0; i < 400 && PromptsTo("sess_text_1").Count == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
        for (var i = 0; i < 400 && Received("session/cancel").Count == 0; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal("sess_text_1", Assert.Single(Received("session/cancel")).GetProperty("params").GetProperty("sessionId").GetString());
        Assert.True(_agents.Current!.IsAlive); // the chat's session goes on
    }

    [Theory]
    [InlineData("sonnet,claude-haiku-4-5,opus", "claude-haiku-4-5")]
    [InlineData("gemini-2.5-flash-lite,gemini-2.5-flash", "gemini-2.5-flash")]
    [InlineData("gpt-5,gpt-5-mini,gpt-5-nano", "gpt-5-mini")]
    [InlineData("gemini-2.5-pro,gemini-2.5-flash,gemini-2.5-flash-lite", "gemini-2.5-flash")]
    [InlineData("haiku-3,haiku-4", "haiku-4")]
    [InlineData("sonnet,opus,minimax", null)]
    [InlineData("", null)]
    public void A_small_model_is_guessed_by_the_words_of_its_name(string models, string? expected)
    {
        var values = models.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => new AcpChoiceValue(m, m, null)).ToList();
        Assert.Equal(expected, AcpTextSession.GuessFastModel(values));
    }

    [Fact]
    public void A_text_sessions_model_is_the_pick_then_the_guess_for_suggestions_and_the_chats_for_actions()
    {
        var choice = new AcpSessionChoice("model", "Model", AcpSessionChoices.ModelCategory, "sonnet", [new("sonnet", "Sonnet", null), new("haiku", "Haiku", null), new("opus", "Opus", null)], AcpChoiceKind.ConfigOption);
        Assert.Equal("opus", AcpTextSession.ChooseModel(choice, AcpTextPurpose.Suggestions, "opus", "sonnet").Value);
        Assert.Equal("haiku", AcpTextSession.ChooseModel(choice, AcpTextPurpose.Suggestions, "removed", "sonnet").Value);
        Assert.Equal("opus", AcpTextSession.ChooseModel(choice, AcpTextPurpose.Actions, "haiku", "opus").Value);
        Assert.Null(AcpTextSession.ChooseModel(choice, AcpTextPurpose.Actions, null, null).Value);
        Assert.Null(AcpTextSession.ChooseModel(null, AcpTextPurpose.Suggestions, "opus", "sonnet").Value);
    }
}
