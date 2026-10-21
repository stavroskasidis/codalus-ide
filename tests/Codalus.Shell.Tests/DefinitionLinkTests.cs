using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Location = Codalus.LanguageServices.Protocol.Location;
using Position = Codalus.LanguageServices.Protocol.Position;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Ctrl+hover link on a language service that doesn't know the symbol at first (no definition, an error, or no service
/// yet), as while design-time builds load the projects again: the pointer stays on the symbol with Ctrl held, and the link
/// shows once the service knows it (develop's macOS run of 0.5.0).
/// </summary>
public class DefinitionLinkTests
{
    private const string Text = "var shape = Geometry.Scale(circle, 3);";

    private static readonly Location s_definition = new("file:///src/Lib/Shapes.cs", new Range(new Position(10, 18), new Position(10, 23)));

    private static readonly (int Start, int End) s_scale = (Text.IndexOf("Scale(", StringComparison.Ordinal), Text.IndexOf("Scale(", StringComparison.Ordinal) + "Scale".Length);

    /// <summary>A language service whose answers the test gives, one request at a time.</summary>
    private sealed class Service
    {
        private readonly List<(int Offset, TaskCompletionSource<Location[]?> Answer)> _requests = [];

        public int Asked => _requests.Count;

        public int LastOffset => _requests[^1].Offset;

        public Task<Location[]?> DefinitionsAsync(int offset, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<Location[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add((offset, answer));
            return answer.Task;
        }

        public void Answer(Location[]? locations) => _requests[^1].Answer.SetResult(locations);

        public void Fail(Exception error) => _requests[^1].Answer.SetException(error);
    }

    /// <summary>A clock that moves only when the test moves it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed record Setup(Window Window, TextEditor Editor, DefinitionLink Link, Service Service, ManualTime Time) : IDisposable
    {
        /// <summary>A point (text view coordinates) on "Scale", <paramref name="into"/> characters in.</summary>
        public Avalonia.Point OnScale(int into)
        {
            var view = Editor.TextArea.TextView;
            var location = Editor.Document.GetLocation(Text.IndexOf("Scale(", StringComparison.Ordinal) + into);
            var visual = view.GetVisualPosition(new TextViewPosition(location), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset;
            return new Avalonia.Point(visual.X + 2, visual.Y);
        }

        public void Dispose()
        {
            Link.Dispose();
            Window.Close();
        }
    }

    private static Setup Show()
    {
        var editor = new TextEditor { Document = new TextDocument(Text), FontFamily = MarkdownRenderer.Monospace };
        var window = new Window { Width = 900, Height = 600, Content = editor };
        window.Show();
        ShellFixture.Pump();
        var service = new Service();
        var time = new ManualTime();
        var link = new DefinitionLink(editor, service.DefinitionsAsync, time);
        editor.TextArea.TextView.LineTransformers.Add(link);
        return new Setup(window, editor, link, service, time);
    }

    [AvaloniaTheory]
    [InlineData("no definition")]
    [InlineData("an error")]
    [InlineData("no service")]
    public void A_symbol_the_service_did_not_know_becomes_a_link_on_a_later_move_within_it(string first)
    {
        using var s = Show();
        s.Link.Update(s.OnScale(1), KeyModifiers.Control);
        ShellFixture.Pump();
        Assert.Equal(1, s.Service.Asked);
        Assert.Equal(s_scale.Start, s.Service.LastOffset);
        switch (first)
        {
            case "no definition":
                s.Service.Answer([]);
                break;
            case "an error":
                s.Service.Fail(new Codalus.Rpc.JsonRpcRemoteException(-32603, "The projects are loading."));
                break;
            default:
                s.Service.Answer(null);
                break;
        }

        ShellFixture.Pump();
        Assert.Null(s.Link.Link);

        // Moves within the symbol right after don't ask again: not a request per pixel.
        s.Link.Update(s.OnScale(2), KeyModifiers.Control);
        s.Link.Update(s.OnScale(3), KeyModifiers.Control);
        ShellFixture.Pump();
        Assert.Equal(1, s.Service.Asked);

        // A move a moment later asks again, without the pointer leaving the symbol or Ctrl going; now the service knows it.
        s.Time.Advance(DefinitionLink.AskAgainAfter);
        s.Link.Update(s.OnScale(2), KeyModifiers.Control);
        ShellFixture.Pump();
        Assert.Equal(2, s.Service.Asked);
        s.Service.Answer([s_definition]);
        ShellFixture.Pump();
        Assert.Equal(s_scale, s.Link.Link);
        Assert.Equal("Hand", s.Editor.TextArea.TextView.Cursor?.ToString());

        // A link is asked for once.
        s.Time.Advance(DefinitionLink.AskAgainAfter);
        s.Link.Update(s.OnScale(1), KeyModifiers.Control);
        ShellFixture.Pump();
        Assert.Equal(2, s.Service.Asked);
        Assert.Equal(s_scale, s.Link.Link);
    }

    [AvaloniaFact]
    public void The_symbol_under_the_pointer_is_asked_again_when_the_service_loaded_the_projects()
    {
        using var s = Show();
        s.Link.Update(s.OnScale(2), KeyModifiers.Control);
        ShellFixture.Pump();
        s.Service.Answer([]);
        ShellFixture.Pump();
        Assert.Null(s.Link.Link);

        // The service's view of the projects changed (the shell's refresh after "ready"): asked again at once, the pointer
        // still.
        s.Link.Recheck();
        ShellFixture.Pump();
        Assert.Equal(2, s.Service.Asked);

        // Again while that answer is coming: it may be from before the change, so the symbol is asked anew.
        s.Link.Recheck();
        ShellFixture.Pump();
        Assert.Equal(3, s.Service.Asked);
        s.Service.Answer([s_definition]);
        ShellFixture.Pump();
        Assert.Equal(s_scale, s.Link.Link);

        // A link, or no Ctrl: nothing to ask.
        s.Link.Recheck();
        s.Link.Update(null, KeyModifiers.None);
        Assert.Null(s.Link.Link);
        s.Link.Recheck();
        ShellFixture.Pump();
        Assert.Equal(3, s.Service.Asked);
    }
}
