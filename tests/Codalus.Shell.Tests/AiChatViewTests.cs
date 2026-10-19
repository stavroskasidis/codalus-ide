using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Agent;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// AI Chat as docs/design/controls/AiChat.dc.html draws it (M34h): your messages as bubbles with what went with them, the
/// assistant's turns with an avatar, its steps (the reusable step template), Markdown, code blocks with a header in the
/// editor's colors whose Copy, Insert at Cursor and Apply… still work, the composer's Agent switch and Send or Stop, and
/// the empty states. Answers come from <see cref="MockModelServers"/>.
/// </summary>
public sealed class AiChatViewTests
{
    private const string Question = "Greet should reject an empty name. How do I report that to the client?";

    private const string Answer = """
        ### Throw a typed fault

        Throw a typed fault, as `Divide` does, so the client gets a `CalculationFault` instead of a generic server error:

        ```csharp
        public string Greet(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("The name is empty", nameof(name));
            }

            return $"Hello {name}!";
        }
        ```

        - Then add `[FaultContract(typeof(CalculationFault))]` to `Greet`.
          - Clients can catch it by type.
        1. See [faults in contracts](https://learn.microsoft.com/dotnet/framework/wcf/specifying-and-handling-faults-in-contracts-and-services).
        """;

    private const string Service = "namespace Samples;\n\npublic sealed class CalculatorService\n{\n    public double Add(double a, double b) => a + b;\n\n    public string Greet(string name) =>\n        $\"Hello {name}!\";\n}\n";

    private static async Task EnableAsync(ShellFixture f, MockHttpServer server)
    {
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, CancellationToken.None);
        await f.Shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", MockModelServers.Key, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.AiChat.IsOn && !f.Shell.AiChat.NeedsProvider);
    }

    /// <summary>The design's conversation: a question about a selection, then an agent turn that read a file.</summary>
    private static MockHttpServer DesignServer() => MockModelServers.Script(request =>
    {
        using var body = JsonDocument.Parse(request.Body);
        var last = body.RootElement.GetProperty("messages").EnumerateArray().Last();
        var content = last.GetProperty("content").GetString() ?? "";
        return last.GetProperty("role").GetString() == "tool" ? new MockTurn("Added `GreetRejectsAnEmptyName` to the tests: it expects the fault.")
            : content.Contains("Also add a test", StringComparison.Ordinal) ? new MockTurn("Reading the service first.", "read_file", """{"path":"CalculatorService.cs"}""")
            : new MockTurn(Answer);
    });

    private static async Task<(ShellFixture F, string Folder, CodeEditorDocument Editor)> ConversationAsync(MockHttpServer server)
    {
        var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var folder = Path.Combine(f.UserData, "Calculator");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "CalculatorService.cs");
        await File.WriteAllTextAsync(path, Service, TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenFileAsync(path);
        await ShellFixture.PumpUntil(() => document!.Controller is not null);
        var editor = document!.Controller!;
        var greet = "    public string Greet(string name) =>\n        $\"Hello {name}!\";\n";
        editor.Editor.Select(Service.IndexOf(greet, StringComparison.Ordinal), greet.Length);

        var chat = f.Shell.AiChat;
        chat.IncludeCurrentFile = false;
        chat.IncludeSolution = false;
        chat.Input = Question;
        await chat.SendAsync();
        await ShellFixture.PumpUntil(() => chat.Turns.Count == 1 && !chat.Turns[0].Turn.IsStreaming);

        // An agent turn with a step, as agent mode runs it (the shell's checks of trust and the model aside).
        await chat.Chat.SendAgentAsync("Also add a test that Greet faults on an empty name", [], new AgentTools(f.Shell.Ai, new FileHost(), folder), 5, CancellationToken.None);
        await ShellFixture.PumpUntil(() => chat.Turns.Count == 2 && !chat.Turns[1].Turn.IsStreaming);
        return (f, folder, document);
    }

    private static Window Show(AiChatViewModel chat, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = new AiChatView { DataContext = chat } };
        window.Show();
        for (var i = 0; i < 6; i++)
        {
            ShellFixture.Pump();
        }

        return window;
    }

    private static AiChatTurnView TurnView(Window window, int index) =>
        window.GetVisualDescendants().OfType<AiChatTurnView>().Single(v => v.Turn == ((AiChatViewModel)((AiChatView)window.Content!).DataContext!).Turns[index].Turn);

    private static void Snapshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            frame!.Save(Path.Combine(snapshots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    [AvaloniaFact]
    public async Task Your_message_is_a_bubble_with_what_went_with_it_and_the_answer_has_an_avatar_markdown_and_steps()
    {
        await using var server = DesignServer();
        var (f, _, _) = await ConversationAsync(server);
        await using var fixture = f;
        var chat = f.Shell.AiChat;
        var window = Show(chat, 556, 1600);
        Snapshot(window, "aichat-conversation");

        // The question: a bubble on Shell.Chat.User, its text selectable, what went with it as chips under it.
        var first = TurnView(window, 0);
        var bubble = first.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("userBubble"));
        Assert.Equal(Brush(window, "Shell.Chat.User"), ((ISolidColorBrush)bubble.Background!).Color);
        var question = Assert.IsType<SelectableTextBlock>(bubble.Child);
        Assert.Equal(Question, question.Text);
        Assert.Equal(Brush(window, "Shell.Chat.User.Foreground"), ((ISolidColorBrush)question.Foreground!).Color);
        Assert.True(bubble.TranslatePoint(new Point(bubble.Bounds.Width, 0), first)!.Value.X >= first.Bounds.Width - 1, "the bubble is on the right");
        Assert.True(bubble.Bounds.Width <= first.Bounds.Width * 0.86 + 1, "the bubble is at most 86 % of the width");
        var chips = first.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).Select(b => ((TextBlock)b.Child!).Text).ToList();
        Assert.Contains(chips, c => c!.Contains("CalculatorService.cs", StringComparison.Ordinal));

        // The answer: the avatar, who answered (the model), Markdown with a heading, inline code, lists and a link.
        Assert.Single(first.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("avatar"));
        var texts = first.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.Contains(texts, t => t.Text == "Assistant" && t.FontWeight == FontWeight.SemiBold);
        Assert.Contains(texts, t => t.Text?.StartsWith("gpt-test · ", StringComparison.Ordinal) == true);
        var heading = texts.Single(t => (t.Text ?? t.Inlines?.Text) == "Throw a typed fault");
        Assert.Equal(13, heading.FontSize);
        Assert.Equal(3, AutomationProperties.GetHeadingLevel(heading));
        Assert.IsType<SelectableTextBlock>(heading);
        var code = texts.SelectMany(t => t.Inlines ?? []).OfType<Run>().First(r => r.Text == "Divide");
        Assert.Contains("code", code.Classes);
        Assert.Equal(Brush(window, "Shell.Badge"), ((ISolidColorBrush)code.Background!).Color);
        Assert.Contains(texts, t => t.Text == "•");
        Assert.Contains(texts, t => t.Text == "◦");
        Assert.Contains(first.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("link") && AutomationProperties.GetName(b) == "faults in contracts");

        // The agent's turn: the Agent chip and its step, read_file with the file as a link and its outcome.
        var second = TurnView(window, 1);
        Assert.Contains(second.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip") && ((TextBlock)b.Child!).Text == "Agent");
        var step = Assert.Single(second.GetVisualDescendants().OfType<AiStepView>());
        var item = Assert.IsType<AiStepItem>(step.DataContext);
        Assert.Equal(("read", "read_file", AiStepStatus.Completed), (item.Kind, item.Title, item.Status));
        Assert.Equal("CalculatorService.cs", Assert.Single(item.Locations).Path);
        Assert.Contains(step.GetVisualDescendants().OfType<Icon>(), i => i.Kind == "check");
        Assert.Contains(step.GetVisualDescendants().OfType<Icon>(), i => i.Kind == "document-text");
        Assert.Contains(step.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("link") && AutomationProperties.GetName(b) == "CalculatorService.cs");
        Assert.StartsWith("read_file CalculatorService.cs, done", AutomationProperties.GetName(step), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_code_block_has_a_header_with_its_language_and_its_buttons_in_the_editor_colors_and_they_still_work()
    {
        await using var server = DesignServer();
        var (f, _, document) = await ConversationAsync(server);
        await using var fixture = f;
        var chat = f.Shell.AiChat;
        var window = Show(chat, 556, 1600);
        var block = Assert.Single(TurnView(window, 0).CodeBlocks);
        Assert.Equal("C#", block.HeaderText);
        var header = block.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("codeBlockHeader"));
        Assert.Equal(Brush(window, "Shell.CodeBlock.Header"), ((ISolidColorBrush)header.Background!).Color);

        // The editor's colors (C# through its grammar in the editor theme), no line numbers, as high as the code.
        Assert.True(block.Viewer.EditorColors && block.Viewer.FitsContent);
        Assert.NotNull(block.Viewer.GrammarColors);
        Assert.False(block.Viewer.Editor.ShowLineNumbers);
        var lines = block.Viewer.Editor.Document.LineCount;
        var lineHeight = block.Viewer.Editor.TextArea.TextView.DefaultLineHeight;
        Assert.InRange(block.Viewer.Bounds.Height, lines * lineHeight, (lines * lineHeight) + 40);

        // Copy, Insert at Cursor and Apply… as icon buttons in the header, each named.
        Button Named(string name) => block.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        var copy = Named("Copy the code");
        var insert = Named("Insert the code at the cursor");
        var apply = Named("Apply the code after a preview");
        Assert.Equal(("copy", "insert", "document-check"), (ButtonIcon.GetKind(copy), ButtonIcon.GetKind(insert), ButtonIcon.GetKind(apply)));
        Assert.Contains("icon", copy.Classes);
        Assert.Contains("iconText", apply.Classes);
        Assert.Equal("Apply…", apply.Content);
        Assert.All(new[] { copy, insert, apply }, b => Assert.True(header.IsVisualAncestorOf(b)));

        copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(f.Window.Clipboard!).Result?.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal) == true);

        var editor = document.Controller!;
        editor.Editor.SelectionLength = 0;
        editor.Editor.CaretOffset = editor.Model.Text.Text.Length;
        insert.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => editor.Model.Text.Text.EndsWith("    return $\"Hello {name}!\";\n}\n", StringComparison.Ordinal));
        editor.Editor.Undo();
        await ShellFixture.PumpUntil(() => editor.Model.Text.Text == Service);

        apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => editor.Model.Text.Text.Contains("throw new ArgumentException", StringComparison.Ordinal) && !editor.Model.Text.Text.Contains("=>\n        $\"Hello", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_composer_has_context_chips_the_Agent_switch_and_Send_which_becomes_Stop_while_a_turn_runs()
    {
        using var gate = new ManualResetEventSlim();
        await using var server = MockModelServers.Script(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return new MockTurn("Late.");
        });
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var chat = f.Shell.AiChat;
        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;

        // The context as chips (pill toggles), each named for what it sends.
        var chips = view.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(6, chips.Count);
        Assert.All(chips, c => Assert.Equal(22, c.Bounds.Height));
        var selection = chips.Single(c => AutomationProperties.GetName(c) == "Send the selection");
        Assert.True(selection.IsChecked);
        selection.IsChecked = false;
        Assert.False(chat.IncludeSelection);
        Assert.Equal(Brush(window, "Shell.Toolbar.Checked"), ((ISolidColorBrush)Presenter(chips.Single(c => AutomationProperties.GetName(c) == "Send the errors and warnings of the open files")).Background!).Color);

        // Agent mode is a switch with its own place in the toolbar.
        var agent = Assert.Single(view.GetVisualDescendants().OfType<ToggleSwitch>());
        Assert.Equal("Agent mode", AutomationProperties.GetName(agent));
        Assert.True(agent.IsEffectivelyVisible);
        agent.IsChecked = true;
        Assert.True(chat.IsAgent);
        agent.IsChecked = false;
        Assert.False(chat.IsAgent);

        // Send: the primary icon button, disabled until there is a question; Enter sends it, Shift+Enter is a new line.
        var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Question");
        Assert.Equal("Ask about your code", input.PlaceholderText);
        var send = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Send the question");
        var stop = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopButton" && AutomationProperties.GetName(b) == "Stop the answer");
        Assert.Contains("accent", send.Classes);
        Assert.Equal("send", ButtonIcon.GetKind(send));
        Assert.False(send.IsEffectivelyEnabled);
        Assert.False(stop.IsEffectivelyVisible);
        input.Focus();
        window.KeyTextInput("h");
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        window.KeyTextInput("i");
        ShellFixture.Pump();
        Assert.Contains('\n', chat.Input);
        Assert.True(send.IsEffectivelyEnabled);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        // While the answer runs, Stop takes Send's place, and stops it.
        await ShellFixture.PumpUntil(() => chat.IsAnswering && stop.IsEffectivelyVisible && !send.IsEffectivelyVisible);
        Assert.Equal(string.Empty, chat.Input);
        Assert.Equal("stop", ButtonIcon.GetKind(stop));
        stop.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => !chat.IsAnswering && send.IsEffectivelyVisible);
        Assert.Equal("stopped", chat.Turns[^1].Turn.FinishReason);
        gate.Set();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Enter_or_Send_while_an_agent_step_runs_starts_no_second_turn_and_Stop_still_stops_the_first()
    {
        await using var server = MockModelServers.Script(request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var last = body.RootElement.GetProperty("messages").EnumerateArray().Last();
            return last.GetProperty("role").GetString() == "tool" ? new MockTurn("Built.") : new MockTurn("Building first.", "build", "{}");
        });
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var folder = Path.Combine(f.UserData, "Workspace");
        Directory.CreateDirectory(folder);
        var tools = new BlockingBuildHost();
        var host = new AgentChatHost(f.Shell, folder, tools);
        using var chat = new AiChatViewModel(host) { IsAgent = true };
        host.Chat = chat;
        chat.IncludeCurrentFile = chat.IncludeSelection = chat.IncludeErrors = chat.IncludeSolution = false;
        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;
        var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Question");
        var send = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Send");
        var stop = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopButton");

        input.Focus();
        window.KeyTextInput("Build it");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        // The build step runs: no request is in flight (AI isn't busy), but the turn is still being answered.
        await ShellFixture.PumpUntil(() => tools.Building.Task.IsCompleted && !chat.IsBusy && chat.IsAnswering && stop.IsEffectivelyVisible);
        Assert.False(chat.CanSend);

        // Enter in the input box, Send and SendAsync all leave the running turn alone and keep the next question.
        window.KeyTextInput("And again");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Pump(4);
        Assert.Equal("And again", chat.Input);
        send.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await chat.SendAsync();
        Pump(4);
        Assert.Equal(1, host.Runs);
        Assert.Single(chat.Turns);
        Assert.Single(chat.Chat.Turns);
        Assert.Equal("And again", chat.Input);

        // Stop still reaches the agent's turn: the build is cancelled and the turn says it stopped.
        stop.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => !chat.IsAnswering && send.IsEffectivelyVisible);
        Assert.True(tools.Cancelled);
        Assert.Equal("stopped", chat.Turns[^1].Turn.FinishReason);
        Assert.True(chat.CanSend);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_Send_leaves_the_focus_in_the_input_box_when_Send_becomes_Stop()
    {
        using var gate = new ManualResetEventSlim();
        await using var server = MockModelServers.Script(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return new MockTurn("Late.");
        });
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var chat = f.Shell.AiChat;
        chat.IncludeCurrentFile = chat.IncludeSelection = chat.IncludeErrors = chat.IncludeSolution = false;
        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;
        var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Question");
        var send = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Send");
        var stop = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopButton");
        chat.Input = "A question";
        Pump(2);

        // A real click: the press focuses Send, which then hides as Stop takes its place.
        var center = send.TranslatePoint(new Point(send.Bounds.Width / 2, send.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        await ShellFixture.PumpUntil(() => chat.IsAnswering && stop.IsEffectivelyVisible && !send.IsEffectivelyVisible);
        Pump(2);
        Assert.Same(input, window.FocusManager?.GetFocusedElement());

        // The next question can be typed while the answer runs (Enter doesn't send it yet).
        window.KeyTextInput("Next");
        Pump(2);
        Assert.Equal("Next", chat.Input);
        chat.Stop();
        await ShellFixture.PumpUntil(() => !chat.IsAnswering);
        gate.Set();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Each_empty_state_has_an_icon_a_sentence_and_the_next_step_and_AI_stays_off_until_turned_on()
    {
        await using var server = MockModelServers.OpenAI("Hi.");
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        var chat = f.Shell.AiChat;
        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;
        StackPanel Empty() => view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("emptyState") && p.IsEffectivelyVisible);
        bool Visible(string name) => view.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetName(b) == name && b.IsEffectivelyVisible);

        // Off (the default): the off message and Turn On; no composer, nothing sent.
        Assert.True(chat.IsOff);
        var off = Empty();
        Assert.Contains(off.Children.OfType<TextBlock>(), t => t.Text == chat.OffMessage && t.Text!.StartsWith("AI assistance is off.", StringComparison.Ordinal));
        Assert.Equal("sparkle", off.Children.OfType<Icon>().Single().Kind);
        Assert.True(Visible("Turn On AI Assistance"));
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
        Snapshot(window, "aichat-off");

        // On without a provider: Choose Provider….
        await f.Shell.TurnOnAiAsync();
        await ShellFixture.PumpUntil(() => chat.NeedsProvider);
        ShellFixture.Pump();
        Assert.Contains(Empty().Children.OfType<Button>(), b => b.Content as string == "Choose Provider…");
        Assert.True(view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Question").IsEffectivelyVisible);

        // Ready: what to ask, and what Agent does.
        await EnableAsync(f, server);
        ShellFixture.Pump();
        Assert.Contains(Empty().Children.OfType<TextBlock>(), t => t.Text!.StartsWith("Ask a question about your solution", StringComparison.Ordinal));
        Assert.False(Visible("Turn On AI Assistance"));
        Snapshot(window, "aichat-ready");

        // Turned off with a conversation: the conversation stays, Turn On takes the composer's place.
        chat.Input = "Hello?";
        await chat.SendAsync();
        await ShellFixture.PumpUntil(() => chat.Turns.Count == 1 && !chat.Turns[0].Turn.IsStreaming);
        await f.Shell.Workbench.Settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => chat.IsOff);
        ShellFixture.Pump();
        Assert.True(chat.ShowOffBar);
        Assert.True(Visible("Turn On AI Assistance"));
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<StackPanel>(), p => p.Classes.Contains("emptyState") && p.IsEffectivelyVisible);
        Assert.Single(view.GetVisualDescendants().OfType<AiChatTurnView>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_step_template_shows_kind_status_title_locations_and_details_for_any_agent()
    {
        await using var f = ShellFixture.Create();
        var opened = new List<AiStepLocation>();
        var items = new List<AiStepItem>
        {
            AiStepItem.From(new AiAgentStep("read_file", "src/Calculator/ICalculatorService.cs", "54 lines"), turnRunning: false, opened.Add),
            AiStepItem.From(new AiAgentStep("search_text", "FaultException", "3 matches"), turnRunning: false),
            AiStepItem.From(new AiAgentStep("run_command", "dotnet test", "not allowed", IsError: true), turnRunning: false),
            AiStepItem.From(new AiAgentStep("build", "", null), turnRunning: true),
            AiStepItem.From(new AiAgentStep("github__create_issue", "", null), turnRunning: false),

            // An external agent's tool call: a sentence for a title, locations with lines, and details.
            new AiStepItem("edit", "Editing the service", AiStepStatus.Completed)
            {
                Locations = [new AiStepLocation("src/Calculator/CalculatorService.cs", 21), new AiStepLocation("tests/CalculatorTests.cs")],
                Outcome = "2 files",
                Details = "- public string Greet(Person person) =>\n+ public string Greet(Person person)\n+ {",
                Open = opened.Add,
            },
        };
        var panel = new StackPanel { Spacing = 2, Width = 260 };
        foreach (var item in items)
        {
            panel.Children.Add(new AiStepView { DataContext = item });
        }

        var rail = new Border { Child = panel, Padding = new Thickness(10, 2, 0, 2) };
        rail.Bind(Border.BackgroundProperty, rail.GetResourceObservable("Shell.Panel"));
        TextElement.SetFontSize(rail, 11);
        var window = new Window { Width = 280, Height = 420, Content = rail };
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        var views = panel.Children.OfType<AiStepView>().ToList();
        string[] Icons(AiStepView v) => [.. v.GetVisualDescendants().OfType<Icon>().Where(i => i.IsEffectivelyVisible && i.Kind != "chevron-right").Select(i => i.Kind!)];
        Assert.Equal(["check", "document-text"], Icons(views[0]));
        Assert.Equal(["check", "search"], Icons(views[1]));
        Assert.Equal(["error", "terminal"], Icons(views[2]));
        Assert.Equal(["timer", "build"], Icons(views[3]));
        Assert.Equal(["stop", "extensions"], Icons(views[4]));
        Assert.Equal(["check", "edit"], Icons(views[5]));

        // A tool's name in the code font; a failed outcome in the error text color; statuses in words for screen readers.
        var title = views[0].GetVisualDescendants().OfType<SelectableTextBlock>().First();
        Assert.Equal("read_file", title.Text);
        Assert.Equal(MarkdownRenderer.Monospace, title.FontFamily);
        var failed = views[2].GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == "→ not allowed");
        Assert.Equal(Brush(window, "Shell.Error.Foreground"), ((ISolidColorBrush)failed.Foreground!).Color);
        Assert.Equal("run_command dotnet test, failed, not allowed", AutomationProperties.GetName(views[2]));
        Assert.Equal("build, running", AutomationProperties.GetName(views[3]));
        Assert.Equal("github__create_issue, stopped", AutomationProperties.GetName(views[4]));

        // Locations are links that open the file (with its line).
        var links = views[5].GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("link")).ToList();
        Assert.Equal(["CalculatorService.cs:21", "CalculatorTests.cs"], links.Select(l => ((TextBlock)l.Content!).Text));
        links[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new AiStepLocation("src/Calculator/CalculatorService.cs", 21), Assert.Single(opened));

        // Details open under the step with its chevron, and only a step with details has one.
        var toggles = panel.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible).ToList();
        var toggle = Assert.Single(toggles);
        Assert.Equal("Details of Editing the service", AutomationProperties.GetName(toggle));
        var details = views[5].GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == items[5].Details);
        Assert.False(details.IsEffectivelyVisible);
        toggle.IsChecked = true;
        ShellFixture.Pump();
        Assert.True(details.IsEffectivelyVisible && items[5].IsExpanded);

        // At a tool window's narrowest nothing is cut off, unnamed or uncolored.
        var problems = PolishTests.Clipped(window).Concat(PolishTests.Unnamed(window)).Concat(PolishTests.Uncolored(window)).Concat(PolishTests.Overlapping(window)).ToList();
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        Snapshot(window, "aichat-steps");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_chat_cuts_nothing_off_names_every_control_and_reads_in_every_theme_at_its_narrowest_and_the_design_width()
    {
        await using var server = DesignServer();
        var (f, _, _) = await ConversationAsync(server);
        await using var fixture = f;
        var problems = new List<string>();
        foreach (var size in new[] { new Size(280, 640), new Size(556, 800) })
        {
            var view = new AiChatView { DataContext = f.Shell.AiChat };
            problems.AddRange((await PolishTests.InspectInEveryAppliedThemeAsync(f, view, size)).Select(p => $"{size.Width:0} px: {p}"));
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));

        // The design's three looks, for a review against the mockup's previews (CODALUS_UI_SNAPSHOTS).
        foreach (var theme in new[] { "harbor.light", "dark", "harbor.highContrast", "highContrast" })
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == ThemeService.Resolve(theme, null));
            var window = Show(f.Shell.AiChat, 556, 800);
            Snapshot(window, $"aichat-data-{theme}");
            window.Close();
        }

        // At a tool window's narrowest the answer takes the whole width (the avatar steps aside), so code keeps room.
        var narrow = Show(f.Shell.AiChat, 280, 640);
        var avatar = narrow.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("avatar"));
        Assert.Equal(0, avatar.Opacity);
        var header = narrow.GetVisualDescendants().OfType<AiCodeBlockView>().Last();
        Assert.True(header.Bounds.Width >= 240, $"the code block is {header.Bounds.Width:0} px wide at 280 px");
        Snapshot(narrow, "aichat-data-narrow");
        narrow.Close();
    }

    [AvaloniaFact]
    public async Task Tab_reaches_every_control_in_reading_order_the_toolbar_the_conversation_then_the_composer()
    {
        await using var server = DesignServer();
        var (f, _, _) = await ConversationAsync(server);
        await using var fixture = f;
        f.Shell.AiChat.Input = "Next question";
        var window = Show(f.Shell.AiChat, 556, 1600);
        var problems = UxPassTests.Unreached(window).ToList();
        Assert.True(problems.Count == 0, string.Join('\n', problems));

        // The order: the toolbar, then the answers' links and code blocks, then the chips, the question and Send.
        var names = new List<string>();
        window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Start a new chat").Focus(NavigationMethod.Tab);
        for (var i = 0; i < 80 && !names.Contains("Send the question"); i++)
        {
            if (window.FocusManager?.GetFocusedElement() is Control { } focused && AutomationProperties.GetName(focused) is { Length: > 0 } name)
            {
                names.Add(name);
            }

            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            ShellFixture.Pump();
        }

        int At(string name) => names.IndexOf(name) is >= 0 and var at ? at : throw new Xunit.Sdk.XunitException($"Tab never reached '{name}': {string.Join(", ", names)}");
        Assert.True(At("Start a new chat") < At("Agent mode") && At("Agent mode") < At("Copy the code") && At("Copy the code") < At("faults in contracts")
            && At("faults in contracts") < At("Send the selection") && At("Send the selection") < At("Question") && At("Question") < At("Send the question"), string.Join(", ", names));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_conversation_builds_only_the_turns_in_view()
    {
        await using var server = MockModelServers.OpenAI("Short:\n\n```csharp\nstatic int Add(int a, int b) => a + b;\n```\n");
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var chat = f.Shell.AiChat;
        for (var i = 0; i < 30; i++)
        {
            chat.Input = $"Question {i}";
            await chat.SendAsync();
            await ShellFixture.PumpUntil(() => chat.Turns.Count == i + 1 && !chat.Turns[i].Turn.IsStreaming);
        }

        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;
        await ShellFixture.PumpUntil(() => view.RealizedTurns.Any(t => t.Turn == chat.Turns[^1].Turn));
        Assert.InRange(view.RealizedTurns.Count, 1, 10);
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");
        Assert.True(scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 2, "the chat shows its end");

        // A new chat releases what was built.
        chat.NewChat();
        ShellFixture.Pump();
        Assert.Empty(view.RealizedTurns);
        Assert.Empty(chat.Turns);
        window.Close();
    }

    /// <summary>A conversation of turns of mixed heights (long code answers and short ones), the last one short.</summary>
    private static async Task<(ShellFixture F, MockHttpServer Server)> MixedConversationAsync(int turns)
    {
        var longAnswer = "Here:\n\n```csharp\n" + string.Join('\n', Enumerable.Range(0, 40).Select(i => $"var value{i} = Compute({i});")) + "\n```\n";
        var server = MockModelServers.OpenAI(request =>
        {
            // The question is the last message (the ones before it are the conversation so far).
            using var body = JsonDocument.Parse(request.Body);
            var question = body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
            return question.Contains("long question", StringComparison.OrdinalIgnoreCase) ? longAnswer : "Short answer.";
        });
        var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var chat = f.Shell.AiChat;
        chat.IncludeCurrentFile = chat.IncludeSelection = chat.IncludeErrors = chat.IncludeSolution = false;
        for (var i = 0; i < turns; i++)
        {
            chat.Input = i % 3 == 0 && i < turns - 1 ? $"A long question {i}" : $"A short question {i}";
            await chat.SendAsync();
            await ShellFixture.PumpUntil(() => chat.Turns.Count == i + 1 && !chat.Turns[i].Turn.IsStreaming);
        }

        return (f, server);
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool while the UI thread waits, so what it posts queues up.</summary>
    private static void RunOnAnotherThreadAndWait(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    private static void Pump(int times)
    {
        for (var i = 0; i < times; i++)
        {
            ShellFixture.Pump();
        }
    }

    private static bool AtEnd(ScrollViewer scroller) => scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 2;

    [AvaloniaFact]
    public async Task Page_Up_or_the_wheel_from_the_end_of_a_long_conversation_stays_where_it_went()
    {
        var (f, server) = await MixedConversationAsync(24);
        await using var fixture = f;
        await using var mock = server;
        var window = Show(f.Shell.AiChat, 556, 700);
        var view = (AiChatView)window.Content!;
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");
        await ShellFixture.PumpUntil(() => AtEnd(scroller) && view.RealizedTurns.Any(t => t.Turn == f.Shell.AiChat.Turns[^1].Turn));
        Pump(6);
        Assert.True(AtEnd(scroller) && view.IsFollowing);

        // Page Up (the key the scroll viewer handles): the last turn leaves the view and the list re-estimates its extent
        // in the same layout pass; the view stays a page up instead of snapping back to the end.
        var before = scroller.Offset.Y;
        var extents = new List<(double Offset, double Extent)>();
        scroller.ScrollChanged += (_, e) => extents.Add((e.OffsetDelta.Y, e.ExtentDelta.Y));
        scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp, Source = scroller });
        Pump(10);
        Assert.Contains(extents, d => d.Offset < 0 && d.Extent != 0); // the case: the extent re-estimated with the page up
        var paged = scroller.Offset.Y;
        Assert.True(paged < before - 100, $"Page Up moved from {before:0} to {paged:0}");
        Assert.False(view.IsFollowing);
        Assert.False(AtEnd(scroller), $"the view went back to the end ({scroller.Offset.Y:0} + {scroller.Viewport.Height:0} of {scroller.Extent.Height:0})");
        Pump(10);
        Assert.Equal(paged, scroller.Offset.Y, 1);

        // Back at the end it follows again; the wheel up leaves it too.
        scroller.ScrollToEnd();
        await ShellFixture.PumpUntil(() => AtEnd(scroller) && view.IsFollowing);
        Pump(6);
        var point = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(point, new Vector(0, 3));
        Pump(10);
        Assert.False(view.IsFollowing);
        Assert.False(AtEnd(scroller));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_new_question_brings_the_view_to_it_even_when_its_turn_ended_before_the_view_saw_it()
    {
        var (f, server) = await MixedConversationAsync(12);
        await using var fixture = f;
        await using var mock = server;
        var chat = f.Shell.AiChat;
        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroller");
        await ShellFixture.PumpUntil(() => AtEnd(scroller));
        scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageUp, Source = scroller });
        Pump(10);
        Assert.False(AtEnd(scroller) || view.IsFollowing);

        // The question goes from another thread to a provider that fails at once (none is chosen): its turn has ended
        // with an error before the UI thread hears of it.
        await f.Shell.Workbench.Settings.SetAsync(AiService.ProviderSetting, (JsonElement?)null, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => chat.NeedsProvider);
        RunOnAnotherThreadAndWait(() => chat.Chat.SendAsync("A question that fails", [], CancellationToken.None));
        await ShellFixture.PumpUntil(() => chat.Turns.Count == 13);
        Assert.False(chat.Turns[^1].Turn.IsStreaming);
        Assert.NotNull(chat.Turns[^1].Turn.Error);
        await ShellFixture.PumpUntil(() => AtEnd(scroller) && view.RealizedTurns.Any(t => t.Turn == chat.Turns[^1].Turn));
        Assert.True(view.IsFollowing);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Turning_AI_off_while_an_answer_is_written_stops_it()
    {
        using var gate = new ManualResetEventSlim();
        await using var server = MockModelServers.Script(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return new MockTurn("Late.");
        });
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var chat = f.Shell.AiChat;
        var window = Show(chat, 556, 700);
        var view = (AiChatView)window.Content!;
        chat.Input = "A question";
        _ = chat.SendAsync();
        await ShellFixture.PumpUntil(() => chat.IsAnswering);

        // The composer, and its Stop, go with AI; the turn doesn't keep running without a way to stop it.
        await f.Shell.Workbench.Settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => chat.IsOff && !chat.IsAnswering);
        Assert.Equal("stopped", chat.Turns[^1].Turn.FinishReason);
        Pump(4);
        Assert.True(chat.ShowOffBar);
        var offBarStop = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopOffBar");
        Assert.Equal("Stop the answer", AutomationProperties.GetName(offBarStop));
        Assert.False(offBarStop.IsEffectivelyVisible);
        gate.Set();
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_code_block_being_written_stays_plain_text_that_grows_and_is_colored_once_complete()
    {
        await using var f = ShellFixture.Create();
        var block = new AiCodeBlockView(f.Shell, f.Shell.EditorFont, () => { }, () => { }, () => { });
        var window = new Window { Width = 556, Height = 600, Content = new ScrollViewer { Content = block } };
        window.Show();
        Pump(2);
        var lines = Enumerable.Range(0, 400).Select(i => $"    var value{i} = Compute({i}); // line {i}").ToList();
        AvaloniaEdit.Document.TextDocument? document = null;
        string Code(int count) => "public void Run()\n{\n" + string.Join('\n', lines.Take(count));
        for (var count = 50; count <= 400; count += 50)
        {
            // Each render of the streaming answer: the block grows at its end, uncolored, in the same document.
            block.Show("csharp", null, Code(count), streaming: true, closed: false);
            Pump(2);
            Assert.False(block.IsColored);
            Assert.Null(block.Viewer.GrammarColors);
            Assert.Equal(Code(count), block.Viewer.Editor.Document.Text);
            document ??= block.Viewer.Editor.Document;
            Assert.Same(document, block.Viewer.Editor.Document);
        }

        // Its closing fence came while the answer is still written: colored once, after the layout; the buttons wait.
        var full = Code(400) + "\n}";
        block.Show("csharp", null, full, streaming: true, closed: true);
        Assert.False(block.IsColored);
        Pump(4);
        Assert.True(block.IsColored);
        var colors = Assert.IsType<GrammarColorizer>(block.Viewer.GrammarColors);
        Assert.Same(document, block.Viewer.Editor.Document);
        Assert.All(block.GetVisualDescendants().OfType<Button>(), b => Assert.False(b.IsEnabled));

        // The answer ends: the buttons work, and the block isn't colored again.
        block.Show("csharp", null, full, streaming: false, closed: true);
        Pump(4);
        Assert.Same(colors, block.Viewer.GrammarColors);
        Assert.All(block.GetVisualDescendants().OfType<Button>(), b => Assert.True(b.IsEnabled));
        block.Release();
        window.Close();
    }

    [AvaloniaFact]
    public void A_step_location_must_be_under_the_workspace_to_open_and_is_judged_from_its_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "codalus-workspace");
        Assert.Equal(Path.Combine(root, "src", "Greeter.cs"), ShellHost.ResolveInWorkspace(root, "src/Greeter.cs"));
        Assert.Equal(Path.Combine(root, "src", "Greeter.cs"), ShellHost.ResolveInWorkspace(root + Path.DirectorySeparatorChar, Path.Combine(root, "src", "Greeter.cs")));
        Assert.Null(ShellHost.ResolveInWorkspace(root, "../secret.cs"));
        Assert.Null(ShellHost.ResolveInWorkspace(root, "src/../../secret.cs"));
        Assert.Null(ShellHost.ResolveInWorkspace(root, Path.Combine(Path.GetTempPath(), "elsewhere", "secret.cs")));
        Assert.Null(ShellHost.ResolveInWorkspace(root, root + "-other" + Path.DirectorySeparatorChar + "a.cs"));
        Assert.Null(ShellHost.ResolveInWorkspace(root, @"\\attacker\share\Greeter.cs"));
        Assert.Null(ShellHost.ResolveInWorkspace(root, "//attacker/share/Greeter.cs"));
        Assert.Null(ShellHost.ResolveInWorkspace(root, "."));
        Assert.Null(ShellHost.ResolveInWorkspace(root, " "));

        // A refused or failed call's path is text, not a link.
        Assert.Empty(AiStepItem.From(new AiAgentStep("read_file", "../../etc/passwd", "outside the workspace", IsError: true), turnRunning: false).Locations);
        Assert.Single(AiStepItem.From(new AiAgentStep("read_file", "src/Greeter.cs", "src/Greeter.cs"), turnRunning: false).Locations);
    }

    [AvaloniaFact]
    public async Task A_step_location_opens_its_file_at_its_line_and_paths_outside_the_workspace_do_not_open()
    {
        await using var f = ShellFixture.Create();
        var workspace = Path.Combine(f.UserData, "Workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "Greeter.cs"), "namespace Samples;\n\npublic static class Greeter\n{\n    public static string Greet(string name) => name;\n}\n", TestContext.Current.CancellationToken);
        var outside = Path.Combine(f.UserData, "Outside.cs");
        await File.WriteAllTextAsync(outside, "class Outside { }\n", TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(workspace);
        await f.Shell.OpenFolderAsync(workspace);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is not null);

        foreach (var refused in new[] { "../Outside.cs", outside, @"\\attacker\share\Outside.cs", "src/Missing.cs" })
        {
            await f.Shell.OpenAiLocationAsync(refused, null);
            ShellFixture.Pump();
            Assert.DoesNotContain(f.Shell.Layout.Documents.OfType<CodeEditorDocument>(), d => d.Model.Path.EndsWith("Outside.cs", StringComparison.Ordinal) || d.Model.Path.EndsWith("Missing.cs", StringComparison.Ordinal));
        }

        await f.Shell.OpenAiLocationAsync("src/Greeter.cs", 5);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null && f.Shell.ActiveEditor.Model.Path.EndsWith("Greeter.cs", StringComparison.Ordinal));
        var editor = f.Shell.ActiveEditor!.Controller!.Editor;
        await ShellFixture.PumpUntil(() => editor.TextArea.Caret.Line == 5);
    }

    private static Color Brush(Visual visual, string token) =>
        ((ISolidColorBrush)(visual is StyledElement e && e.TryFindResource(token, e.ActualThemeVariant, out var value) ? value! : throw new InvalidOperationException(token))).Color;

    private static ContentPresenter Presenter(ToggleButton button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    /// <summary>An agent host whose build runs until it is cancelled (a tool step that takes a while).</summary>
    private sealed class BlockingBuildHost : IAgentHost
    {
        public TaskCompletionSource Building { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; private set; }

        public async Task<string> BuildAsync(CancellationToken cancellationToken)
        {
            Building.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return string.Empty;
        }

        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> ProposeChangeAsync(string path, string? oldText, string newText, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ApproveAsync(string title, string message, IReadOnlyList<string> details, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<string> DiagnosticsAsync(string? path, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<string> BuildOutputAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }

    /// <summary>The shell as AI Chat's host, but agent mode runs the loop with <paramref name="tools"/> (no solution or trust checks).</summary>
    private sealed class AgentChatHost(ShellHost shell, string folder, IAgentHost tools) : IAiChatHost
    {
        private int _runs;

        public AiChatViewModel? Chat { get; set; }

        public int Runs => Volatile.Read(ref _runs);

        public AiService Ai => shell.Ai;

        public IEditorServices EditorServices => shell;

        public Infrastructure.EditorFont EditorFont => shell.EditorFont;

        public async Task RunAgentAsync(string question, IReadOnlyList<AiContextItem> items, AiSelectionAnchor? anchor, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            await Chat!.Chat.SendAgentAsync(question, items, new AgentTools(shell.Ai, tools, folder), 5, cancellationToken, anchor).ConfigureAwait(true);
        }

        public (IReadOnlyList<AiContextItem> Items, AiSelectionAnchor? Anchor) GatherAiContext(AiContextChoice choice) => ([], null);

        public Task ApplyAiCodeAsync(AiChatTurn turn, AiCodeBlock block, bool insertOnly) => Task.CompletedTask;

        public Task CopyTextAsync(string text) => Task.CompletedTask;

        public Task ChooseAiProviderAsync() => Task.CompletedTask;

        public Task TurnOnAiAsync() => Task.CompletedTask;

        public Task ConfigureAiProviderAsync() => Task.CompletedTask;

        public void ShowAiLog()
        {
        }

        public void ShowAiChatWindow()
        {
        }

        public void OpenLink(string target)
        {
        }

        public Task OpenAiLocationAsync(string path, int? line) => Task.CompletedTask;
    }

    /// <summary>The agent's view of the files, from the disk.</summary>
    private sealed class FileHost : IAgentHost
    {
        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : null);

        public Task<bool> ProposeChangeAsync(string path, string? oldText, string newText, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ApproveAsync(string title, string message, IReadOnlyList<string> details, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<string> DiagnosticsAsync(string? path, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<string> BuildOutputAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Task<string> BuildAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }
}
