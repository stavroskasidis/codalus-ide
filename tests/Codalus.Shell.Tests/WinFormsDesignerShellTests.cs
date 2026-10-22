using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.Tests;

/// <summary>
/// M41a (ADR 0073, D31) in the headless shell on samples/WindowsDesktop's Windows Forms app. Off Windows a form opens as
/// code and View Designer says the designer is Windows only. With a fake designer host (any OS): the form's code file
/// opens in the designer; the Toolbox has the framework's controls and the active designer's project's own (also when
/// the form opened before the restore brought what they derive from, or before the C# service had the project); a
/// Toolbox item created on the surface, a property set in the Properties window, an event handler from the Events view
/// and a delete become code in the designer file (and the code file), undo goes back through the designer file's own
/// undo stack, F7 shows the code; code the designer can't read opens as code with the reason.
/// </summary>
public sealed class WinFormsDesignerShellTests
{
    internal static async Task<(ShellFixture Fixture, string App)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        return (f, Path.Combine(folder, "WinFormsApp"));
    }

    /// <summary>
    /// M34h: the selection adorner is a designer's: a one-pixel accent frame on the pixels just outside the control's
    /// bounds, and grab handles of whole pixels centered on its corners and edge middles. As drawn, the frame's line
    /// between two handles is the accent and the control's own first row and column aren't (the gallery showed the
    /// frame blurred over two pixels and the handles a pixel inside it).
    /// </summary>
    private static async Task AssertSelectionAdornerAsync(ShellFixture f, WinFormsDesignerDocument designer, string name)
    {
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().Any(s => s.IsEffectivelyVisible));
        var surface = f.Window.GetVisualDescendants().OfType<Views.DesignerSurface>().First(s => s.IsEffectivelyVisible);
        ShellFixture.Pump();
        var view = designer.Find(name)!;
        var model = WinFormsDesignerDocument.Bounds(view);
        var bounds = new Rect(model.X, model.Y, model.Width, model.Height);
        Assert.Equal(1, f.Window.RenderScaling);
        Assert.Equal(bounds.Inflate(1), surface.Frame(bounds));
        var handles = surface.Handles(bounds);
        Assert.Equal(8, handles.Length);
        Assert.All(handles, h => Assert.True(h is { Width: 7, Height: 7 } && h.X == Math.Round(h.X) && h.Y == Math.Round(h.Y), $"a handle at {h} isn't whole pixels"));
        Assert.Equal(new Point(bounds.Left - 0.5, bounds.Top - 0.5), handles[0].Center); // on the frame's top-left pixel
        Assert.Equal(new Point(bounds.Right + 0.5, bounds.Bottom + 0.5), handles[4].Center); // and its bottom-right

        var accent = surface.TryFindResource("Shell.Accent", surface.ActualThemeVariant, out var found) && found is ISolidColorBrush brush ? brush.Color : default;
        // The surface drawn alone (the document's pane may be fading in, which would blend every color).
        Assert.True(surface.TranslatePoint(default, f.Window) is { } at && at.X == Math.Round(at.X) && at.Y == Math.Round(at.Y));
        var origin = default(Point);
        using var rendered = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)surface.Bounds.Width, (int)surface.Bounds.Height));
        rendered.Render(surface);
        using var png = new MemoryStream();
        rendered.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        png.Position = 0;
        using var frame = Avalonia.Media.Imaging.WriteableBitmap.Decode(png);
        var x = (int)(origin.X + bounds.Left + (bounds.Width / 4)); // between the corner's handle and the middle's
        var top = (int)(origin.Y + bounds.Top);
        Assert.Equal(accent, Pixel(frame, x, top - 1));
        Assert.NotEqual(accent, Pixel(frame, x, top - 2)); // one pixel wide, not two
        Assert.NotEqual(accent, Pixel(frame, x, top));
        Assert.True(handles[7].Y - handles[0].Bottom >= 1, "no frame between the left handles to look at");
        var y = (int)(origin.Y + handles[0].Bottom); // between the top-left handle and the left middle's
        var left = (int)(origin.X + bounds.Left);
        Assert.Equal(accent, Pixel(frame, left - 1, y));
        Assert.NotEqual(accent, Pixel(frame, left, y));

        // A handle: an accent edge around the panel's color.
        var panel = surface.TryFindResource("Shell.Panel", surface.ActualThemeVariant, out var fill) && fill is ISolidColorBrush fillBrush ? fillBrush.Color : default;
        var corner = handles[0];
        Assert.Equal(accent, Pixel(frame, (int)(origin.X + corner.X), (int)(origin.Y + corner.Center.Y)));
        Assert.Equal(panel, Pixel(frame, (int)(origin.X + corner.Center.X), (int)(origin.Y + corner.Center.Y)));
    }

    private const string CounterControlType = "WinFormsApp.Controls.CounterControl";

    /// <summary>
    /// The fake designer host (any OS: the real one is Windows only, D31) for the forms opened from now on; each host it
    /// starts goes to <paramref name="hosts"/>, after <paramref name="starting"/> (a slow start, or one that fails) when
    /// there is one. <paramref name="build"/> is the designer's own build when the output needs one (by default it
    /// succeeds at once). Whether a build runs (#66: the designer waits for it) is the shell's own answer, the build
    /// service's.
    /// </summary>
    internal static void UseFakeDesignerHost(ShellFixture f, List<FakeWinFormsDesignerHost> hosts, Func<Task>? starting = null, Func<Task<bool>>? build = null)
    {
        f.Shell.WinFormsDesigner = new WinFormsDesignerService(f.Shell.Solutions, _ => true, (_, _) => build?.Invoke() ?? Task.FromResult(true), async (project, names, ct) =>
        {
            var properties = await f.Shell.ProjectManagement.GetPropertiesAsync(project, names, null, ct);
            return properties.Properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        })
        {
            IsWindows = () => true,
            IsBuilding = f.Shell.WinFormsDesigner.IsBuilding,
            DesignerHost = () => typeof(WinFormsDesignerShellTests).Assembly.Location,
            HostFactory = async (_, _) =>
            {
                if (starting is not null)
                {
                    await starting();
                }

                var host = new FakeWinFormsDesignerHost();
                hosts.Add(host);
                return host;
            },
        };
    }

    /// <summary>Whether a design-time build's compiler inputs reference Windows Forms (off Windows, its reference pack comes with the restore).</summary>
    private static bool ReferencesWindowsForms(CompilerInputsInfo inputs) =>
        inputs.Arguments.Any(a => a.Contains("System.Windows.Forms.dll", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a wait for <paramref name="waitingFor"/> (by default, the project's own control in the Toolbox) found: the
    /// restore, whether the compiler inputs have Windows Forms, the C# service, the Toolbox's categories and the
    /// designer's log. The C# host finds the project's controls only in a compilation with Windows Forms, so this says
    /// whether the C# service hadn't loaded the restored inputs or the Toolbox hadn't asked again since (macOS CI, PR
    /// #100 and #55).
    /// </summary>
    internal static string DescribeToolbox(ShellFixture f, string? waitingFor = null)
    {
        var inputs = f.Shell.Solutions.CompilerInputs.Values.Select(i => $"{Path.GetFileName(i.Path)} {i.State} {(ReferencesWindowsForms(i) ? "with" : "without")} System.Windows.Forms.dll").ToList();
        var status = f.Shell.Languages.Status(ShellHost.CSharpLanguageId);
        var categories = f.Shell.Toolbox.Categories.Select(c => $"{c.Title} ({c.Items.Count(i => !i.IsPointer)})");
        return $"{waitingFor ?? $"the Toolbox to list {CounterControlType}"} (restore {f.Shell.Solutions.RestoreState ?? "not run"}; compiler inputs: {(inputs.Count == 0 ? "none" : string.Join(", ", inputs))}; "
            + $"C# {status?.State ?? "not running"} with {status?.Projects ?? 0} project(s); Toolbox: {string.Join(", ", categories)}). Windows Forms Designer log:\n{f.Shell.WinFormsDesignerLog.GetText()}";
    }

    /// <summary>
    /// Waits until the designer's form was read with all of its project's types and whatever that read loads is done
    /// (#76): off Windows, a form opened before the restore is read without Windows Forms, then read again and loaded
    /// again on its host once the C# service has loaded the restored inputs. A test that counts loads counts from here.
    /// </summary>
    internal static async Task WhenReadCompleteAsync(ShellFixture f, WinFormsDesignerDocument designer)
    {
        await ShellFixture.PumpUntil(() => designer.LastRead is { IsPartial: false }, seconds: 300,
            () => DescribeToolbox(f, $"a complete read of {Path.GetFileName(designer.CodePath)} (the last one {(designer.LastRead is { } read ? $"partial: {read.IsPartial}" : "not there")})"));
        await designer.WhenIdleAsync();
    }

    /// <summary>
    /// Restores the solution and waits for the C# service to load the restored inputs: loading, then ready, once the
    /// restore succeeded and the compiler inputs reference Windows Forms (the turn on which a designer's read from before
    /// the restore is read again, and the Toolbox asks again).
    /// </summary>
    private static async Task RestoreAndWaitForReloadAsync(ShellFixture f, Func<string, string> describe)
    {
        var reload = 0; // 1: loading with the restored inputs, 2: ready after it
        void OnStatus(object? sender, (string LanguageId, WorkspaceStatusParams Status) e)
        {
            if (e.LanguageId == ShellHost.CSharpLanguageId && f.Shell.Solutions.RestoreState == "succeeded" && f.Shell.Solutions.CompilerInputs.Values.Any(ReferencesWindowsForms))
            {
                if (e.Status.State == WorkspaceStates.Loading)
                {
                    Interlocked.CompareExchange(ref reload, 1, 0);
                }
                else if (e.Status.State == WorkspaceStates.Ready)
                {
                    Interlocked.CompareExchange(ref reload, 2, 1);
                }
            }
        }

        f.Shell.Languages.StatusChanged += OnStatus;
        try
        {
            await f.Shell.Solutions.RestoreAsync(null, CancellationToken.None);
            await ShellFixture.PumpUntil(() => Volatile.Read(ref reload) == 2, seconds: 300, () => describe("the C# service to load the restored inputs (loading, then ready)"));
        }
        finally
        {
            f.Shell.Languages.StatusChanged -= OnStatus;
        }
    }

    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var offset = (y * buffer.RowBytes) + (x * 4);
        var (b0, b1, b2, b3) = (Marshal.ReadByte(buffer.Address, offset), Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset + 2), Marshal.ReadByte(buffer.Address, offset + 3));
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromArgb(b3, b0, b1, b2) : Color.FromArgb(b3, b2, b1, b0);
    }

    [AvaloniaFact]
    public async Task Off_windows_a_form_opens_as_code_and_view_designer_says_windows_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "On Windows the form opens in the designer (the Windows-only tests and smoke step).");
        var (f, app) = await OpenAsync();
        await using var _ = f;
        var form = Path.Combine(app, "OrderForm.cs");
        var editor = await f.Shell.OpenFileAsync(form);
        Assert.NotNull(editor);
        Assert.Equal(form, editor.Model.Path);
        Assert.Empty(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Contains("Windows only", f.Shell.StatusBar.Message, StringComparison.Ordinal);

        // View Designer (Shift+F7): says so, and the code stays.
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("editorHasWinFormsDesigner") is true);
        await f.RunAsync("winforms.viewDesigner");
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("Windows Forms designer runs on Windows only", StringComparison.Ordinal)));
        Assert.Empty(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Same(editor, f.Shell.ActiveEditor);
        Assert.False(f.Shell.Toolbox.IsAvailable);
        Assert.NotNull(f.Shell.Toolbox.Message);
    }

    [AvaloniaFact]
    public async Task With_a_designer_host_forms_open_in_the_designer_and_changes_become_code()
    {
        var (f, app) = await OpenAsync();
        await using var _ = f;
        var hosts = new List<FakeWinFormsDesignerHost>();
        UseFakeDesignerHost(f, hosts);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300);

        // The code file opens in the designer: the model from the C# host, loaded in the designer host, drawn.
        var form = Path.Combine(app, "OrderForm.cs");
        Assert.Null(await f.Shell.OpenFileAsync(form));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Same(designer, f.Shell.Layout.ActiveDocument);
        Assert.Equal("OrderForm.cs [Design]", designer.Title);
        try
        {
            await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120);
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        }

        var host = Assert.Single(hosts);
        Assert.Equal("OrderForm", host.Model!.ClassName);
        Assert.Equal(CounterControlType, host.Model.Components.Single(c => c.Name == "counterControl").Type);
        Assert.Contains(designer.Components, c => c.Name == "placeOrderButton" && c.Parent == "buttonPanel");
        Assert.Contains(designer.Tray, t => t.Name == "orderToolTip");
        Assert.Equal((400d, 450d), (designer.FrameWidth, designer.FrameHeight));

        // The Toolbox: available, the framework's categories and the project's own control.
        Assert.True(f.Shell.Toolbox.IsAvailable);
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "Common Controls");
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "Containers");
        // When the C# service was ready before the restore, the control comes after the restore, the design-time build
        // after it and the C# service's reload (the same budget as the other waits on the restore).
        await ShellFixture.PumpUntil(() => f.Shell.Toolbox.Items.Any(i => i.Type == CounterControlType), seconds: 300, () => DescribeToolbox(f));
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "WinFormsApp Components");
        var buttonItem = f.Shell.Toolbox.Items.First(i => i.Type == "System.Windows.Forms.Button");

        // A form opened before the restore was read without Windows Forms (#76): once restored, it's read again and loads
        // again on its host. The loads below are counted from there.
        await WhenReadCompleteAsync(f, designer);
        var loads = host.Loads;
        f.Shell.Toolbox.Filter = "Combo";
        Assert.All(f.Shell.Toolbox.Items, i => Assert.Contains("Combo", i.Name, StringComparison.Ordinal));
        f.Shell.Toolbox.Filter = string.Empty;

        // A click on the surface with the Button armed: button1, in the designer file, at the front of the form's controls.
        var code = designer.DesignerCode!;
        f.Shell.Toolbox.Armed = buttonItem;
        await designer.CreateAsync(f.Shell.Toolbox.Armed!.Type, 8 + 290, 31 + 300);
        Assert.Null(f.Shell.Toolbox.Armed);
        Assert.Contains("            button1 = new Button();\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            button1.Location = new Point(290, 300);\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("            Controls.Add(button1);\n            Controls.Add(counterControl);\n", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("        private Button button1;\n", code.Text.Text, StringComparison.Ordinal);
        Assert.True(designer.IsModified);
        Assert.Equal(["button1"], designer.Selection);
        Assert.Equal(loads, host.Loads); // the designer host already shows it: no reload for its own edit

        // The Properties window follows the designer's selection: Text set there is code.
        f.Shell.Layout.ShowToolWindow(PropertiesWindowViewModel.ToolWindowId, focus: false);
        var properties = f.Shell.PropertiesWindow;
        await properties.RefreshAsync();
        Assert.True(properties.HasDesignerSource);
        Assert.Equal("button1 System.Windows.Forms.Button", properties.Title);
        var text = properties.Categories.SelectMany(c => c.Rows).Single(r => r.Name == "Text");
        Assert.Equal("button1", text.Value);
        Assert.Contains(properties.Categories.SelectMany(c => c.Rows), r => r is { Name: "Anchor", IsAnchor: true } && r.AnchorTop!.IsChecked && !r.AnchorRight!.IsChecked);
        Assert.Contains(properties.Categories.SelectMany(c => c.Rows), r => r is { Name: "Enabled", IsChoice: true });
        Assert.Contains(properties.Categories.SelectMany(c => c.Rows), r => r is { Name: "BackColor", IsColor: true });
        text.Text = "Hello";
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("button1.Text = \"Hello\";", StringComparison.Ordinal));
        properties.Alphabetical = true;
        await properties.RefreshAsync();
        Assert.Single(properties.Categories);

        // Undo: the designer file's own undo stack, and the form loaded again from the code.
        designer.Undo();
        await ShellFixture.PumpUntil(() => !code.Text.Text.Contains("\"Hello\"", StringComparison.Ordinal) && host.Loads == loads + 1, seconds: 30);
        Assert.Contains("button1.Text = \"button1\";", code.Text.Text, StringComparison.Ordinal);
        designer.Redo();
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("\"Hello\"", StringComparison.Ordinal) && host.Loads == loads + 2, seconds: 30);

        // The Events view: the button's events; its action writes the handler in the code file and shows it.
        properties.ShowEvents = true;
        await properties.RefreshAsync();
        var click = properties.Categories.SelectMany(c => c.Rows).Single(r => r.Name == "Click");
        Assert.True(click.HasAction);
        click.Action!.Execute(null);
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("button1.Click += button1_Click;", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form);
        Assert.Contains("private void button1_Click(object? sender, EventArgs e)", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
        properties.ShowEvents = false;

        // F7 from the designer shows the code; double-clicking a control with a handler goes to it.
        f.Shell.Layout.OpenDocument(designer, focus: true);
        await f.RunAsync("winforms.viewCode");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form);
        f.Shell.Layout.OpenDocument(designer, focus: true);
        var place = designer.Find("placeOrderButton")!;
        await designer.DefaultEventAsync(place.X + 5, place.Y + 5);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form && f.Shell.ActiveEditor.Controller?.Editor.TextArea.Caret.Line > 1);
        Assert.DoesNotContain("OnPlaceOrderClicked_1", code.Text.Text, StringComparison.Ordinal);

        // A move, a nudge and a delete.
        f.Shell.Layout.OpenDocument(designer, focus: true);
        designer.SetSelection(["giftCheckBox"], notify: true);
        await designer.MoveSelectionAsync(10, 5, 120, 120);
        Assert.Contains("giftCheckBox.Location = new Point(106, 75);", code.Text.Text, StringComparison.Ordinal);
        await designer.NudgeAsync(1, 0, resize: false);
        Assert.Contains("giftCheckBox.Location = new Point(107, 75);", code.Text.Text, StringComparison.Ordinal);
        await AssertSelectionAdornerAsync(f, designer, "giftCheckBox");
        designer.SetSelection(["button1"], notify: true);
        await designer.DeleteSelectionAsync();
        Assert.DoesNotContain("button1 =", code.Text.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("button1.Click", code.Text.Text, StringComparison.Ordinal);

        // Copy and paste: a new name, a little lower and to the right, the form loaded again.
        designer.SetSelection(["cancelButton"], notify: true);
        designer.Copy();
        designer.SetSelection(["buttonPanel"], notify: true);
        await designer.PasteAsync();
        await ShellFixture.PumpUntil(() => code.Text.Text.Contains("cancelButton1 = new Button();", StringComparison.Ordinal));
        Assert.Contains("buttonPanel.Controls.Add(cancelButton1);", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("cancelButton1.Location = new Point(313, 24);", code.Text.Text, StringComparison.Ordinal);
        Assert.Contains("cancelButton1.Name = \"cancelButton1\";", code.Text.Text, StringComparison.Ordinal);

        // A localizable form's components are refused (their properties are in the .resx: M41b).
        // Save writes the designer file.
        await f.Shell.SaveAllAsync();
        Assert.Contains("cancelButton1", await File.ReadAllTextAsync(Path.Combine(app, "OrderForm.Designer.cs"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(designer.IsModified);

        // Code the designer can't read: the designer closes and the code opens at the statement, with the reason.
        var unreadable = Path.Combine(app, "UnreadableForm.cs");
        await f.Shell.OpenFileAsync(unreadable);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("lambda", StringComparison.Ordinal)), seconds: 60);
        Assert.DoesNotContain(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>(), d => d.CodePath == unreadable);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == Path.Combine(app, "UnreadableForm.Designer.cs"));

        // Closing the designer stops its host and lets the text go.
        f.Shell.Layout.CloseDocument(designer);
        await ShellFixture.PumpUntil(() => f.Shell.PropertySource is null);
    }

    /// <summary>
    /// A form opened in the designer before the restore (macOS CI, PR #100 and #55: the Toolbox never listed
    /// CounterControl). Off Windows the Windows Desktop reference pack comes with the restore, so until then the
    /// project's compilation has no Windows Forms and the C# host finds no control in it, while the C# service is ready
    /// (it doesn't wait for the restore). The Toolbox asked once, when the designer opened, and kept that empty answer;
    /// the active designer now asks again when the C# service has loaded the projects again. Here the restore waits
    /// for the test (no restore on open).
    /// </summary>
    [AvaloniaFact]
    public async Task A_form_opened_before_the_restore_lists_the_projects_controls_once_restored()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, false, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        UseFakeDesignerHost(f, []);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300,
            () => DescribeToolbox(f, "the C# service to be ready before the restore"));
        Assert.Null(f.Shell.Solutions.RestoreState);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ReferencesWindowsForms(Assert.Single(f.Shell.Solutions.CompilerInputs.Values)), "Windows Forms is referenced before the restore");
        }

        // The form opens in the designer; the Toolbox's answer is from the compilation before the restore.
        var form = Path.Combine(folder, "WinFormsApp", "OrderForm.cs");
        Assert.Null(await f.Shell.OpenFileAsync(form));
        var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120, () => $"a frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        await designer.ToolboxLoad;
        Assert.Contains(f.Shell.Toolbox.Items, i => i.Type == "System.Windows.Forms.Button");
        if (!OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(f.Shell.Toolbox.Items, i => i.Type == CounterControlType);
        }

        // The restore brings the reference pack: the design-time build after it references Windows Forms, and the C#
        // service loads it (loading, then ready).
        await RestoreAndWaitForReloadAsync(f, waitingFor => DescribeToolbox(f, waitingFor));

        await ShellFixture.PumpUntil(() => f.Shell.Toolbox.Items.Any(i => i.Type == CounterControlType), seconds: 60, () => DescribeToolbox(f));
        Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "WinFormsApp Components");

        // Ready without loading first (the pass over the diagnostics after an edit) asks nothing; loading, then ready,
        // asks again, and the same answer leaves the Toolbox as it is.
        await designer.ToolboxLoad;
        var categories = f.Shell.Toolbox.Categories.ToList();
        f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0)); // ready, whatever came last
        var asked = designer.ToolboxRequests;
        f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
        Assert.Equal(asked, designer.ToolboxRequests);
        f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Loading, 1, 0));
        f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
        Assert.Equal(asked + 1, designer.ToolboxRequests);
        await designer.ToolboxLoad;
        Assert.Equal(categories, f.Shell.Toolbox.Categories);

        // Back to the designer from its code (F7): it asks too, since the Toolbox has the active designer's project's controls.
        await f.RunAsync("winforms.viewCode");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == form);
        asked = designer.ToolboxRequests;
        f.Shell.Layout.OpenDocument(designer, focus: true);
        Assert.Equal(asked + 1, designer.ToolboxRequests);
        await designer.ToolboxLoad;
        Assert.Equal(categories, f.Shell.Toolbox.Categories);
    }

    /// <summary>
    /// A form opened in the designer, with its code open, before the C# service has its project (the design-time build's
    /// inputs haven't reached the C# host yet: held here, as a busy machine does). The C# host reads the form from
    /// Miscellaneous Files, so its answer names no project, and the Toolbox never asked (until the designer was
    /// reopened). It asks with the build host's project, and again once the C# service has loaded it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_form_opened_before_the_CSharp_service_has_its_project_lists_the_projects_controls_once_loaded()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        UseFakeDesignerHost(f, []);
        var gate = f.Shell.Languages.FlushGate;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var held = true;
        try
        {
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);

            // The form's code is open (as a session restores it) and View Designer opens the designer while the C# service
            // has no project: the C# host reads both files from Miscellaneous Files, where the base type's name decides.
            var form = Path.Combine(folder, "WinFormsApp", "OrderForm.cs");
            Assert.NotNull(await f.Shell.OpenFileAsync(form, textEditor: true));
            await f.Shell.OpenWinFormsDesignerAsync(form);
            var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
            await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120, () => $"a frame: {designer.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
            await designer.ToolboxLoad;
            Assert.Equal(0, f.Shell.Languages.Status(ShellHost.CSharpLanguageId)?.Projects ?? 0);
            Assert.DoesNotContain(f.Shell.Toolbox.Items, i => i.Type == CounterControlType);

            // The restore (on open) and the design-time build after it, then the C# host gets the project (loading, then ready).
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.RestoreState == "succeeded" && f.Shell.Solutions.DesignTimeProgress is { Completed: true }
                && f.Shell.Solutions.CompilerInputs.Values.Any(ReferencesWindowsForms), seconds: 300, () => DescribeToolbox(f, "the restore and the design-time build after it"));
            held = false;
            gate.Release();
            await ShellFixture.PumpUntil(() => f.Shell.Toolbox.Items.Any(i => i.Type == CounterControlType), seconds: 300, () => DescribeToolbox(f));
            Assert.Contains(f.Shell.Toolbox.Categories, c => c.Title == "WinFormsApp Components");
        }
        finally
        {
            if (held)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// #76: a form opened in the designer before the C# service has its project, with its code closed. The C# host had
    /// only the designer file, alone in Miscellaneous Files with no base type, so the form "isn't a form": the designer
    /// closed and the code opened. The host now reads it with the code file's part, says the read was partial, and the
    /// designer reads it again on each loading → ready of the C# service (the project arrives before the restore, and
    /// again after it), also when that comes while its first read is still on its way, holding its gate behind a start
    /// still running, without ever starting a host of its own.
    /// </summary>
    [AvaloniaFact]
    public async Task A_form_opened_before_the_CSharp_service_has_its_project_with_its_code_closed_opens_in_the_designer_and_reads_again_once_loaded()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, false, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        var hosts = new List<FakeWinFormsDesignerHost>();
        var startHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        UseFakeDesignerHost(f, hosts, () =>
        {
            Interlocked.Increment(ref starts);
            return startHeld.Task;
        });
        var gate = f.Shell.Languages.FlushGate;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var held = true;
        try
        {
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);

            // The C# service loads (loading, then ready) while the designer's first read is on its way: the read was made
            // in Miscellaneous Files, so it's read again once it has landed. Sent when the designer says it's reading,
            // before the C# host's answer.
            var inFlight = false;
            void OnDesignerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (!inFlight && sender is WinFormsDesignerDocument { LastRead: null } reading && e.PropertyName == nameof(WinFormsDesignerDocument.Status)
                    && reading.Status.StartsWith("Reading ", StringComparison.Ordinal))
                {
                    inFlight = true;
                    f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Loading, 1, 0));
                    f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
                }
            }

            void OnActiveDocument(object? sender, Docking.ShellDocument? document)
            {
                if (document is WinFormsDesignerDocument opened)
                {
                    opened.PropertyChanged -= OnDesignerChanged;
                    opened.PropertyChanged += OnDesignerChanged;
                }
            }

            f.Shell.Layout.ActiveDocumentChanged += OnActiveDocument;

            // The form opens (from Solution Explorer, with its code closed) while the C# host has no project: it reads
            // the designer file from Miscellaneous Files, with the code file's part, and the designer stays.
            var form = Path.Combine(folder, "WinFormsApp", "OrderForm.cs");
            var opening = f.Shell.OpenFileAsync(form);
            string Describe(string waitingFor)
            {
                var designer = f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().SingleOrDefault();
                return $"{waitingFor}: designer {(designer is null ? "closed" : $"\"{designer.Status}\", last read {designer.LastRead}")}; notifications: "
                    + $"{string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message))}; starts {Volatile.Read(ref starts)}, hosts {hosts.Count}. {DescribeToolbox(f, waitingFor)}";
            }

            await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>().SingleOrDefault()?.LastRead is not null && Volatile.Read(ref starts) == 1, seconds: 120,
                () => Describe("the form read and the designer host starting"));
            f.Shell.Layout.ActiveDocumentChanged -= OnActiveDocument;
            var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
            designer.PropertyChanged -= OnDesignerChanged;
            Assert.True(inFlight, Describe("loading, then ready, sent while the first read was on its way"));
            Assert.Equal(form, designer.CodePath);
            Assert.False(f.Shell.HasEditorFor(form));
            Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message.Contains("isn't a form", StringComparison.Ordinal));
            Assert.True(designer.LastRead is { IsDesignable: true, IsPartial: true, Project: null, Model.BaseType: "System.Windows.Forms.Form" }, Describe("a partial read"));
            Assert.Equal(0, f.Shell.Languages.Status(ShellHost.CSharpLanguageId)?.Projects ?? 0);

            // The C# service loads again while the host still starts. Each read again waits for the start (the designer's
            // gate) and loads on that host: the first read, then one load for each read again; no second host.
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Loading, 1, 0));
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
            startHeld.SetResult();
            Assert.Null(await opening);
            await ShellFixture.PumpUntil(() => designer.FrameCount > 0, seconds: 120, () => Describe("a frame"));
            await ShellFixture.PumpUntil(() => hosts.Count == 1 && hosts[0].Loads == 3, seconds: 60, () => Describe("both reads again loaded on the first host"));
            await designer.WhenIdleAsync();
            Assert.Equal(3, hosts[0].Loads);
            Assert.Single(hosts);
            Assert.Equal(1, Volatile.Read(ref starts));
            Assert.Equal("System.Windows.Forms.Form", hosts[0].Model!.BaseType);
            Assert.Contains("reading OrderForm.cs again", f.Shell.WinFormsDesignerLog.GetText(), StringComparison.Ordinal);

            // The C# host gets the project (before the restore): the form is read again, now in its project. It's still
            // partial when the design-time build's compilation lacks Windows Forms (off Windows its reference pack comes
            // with the restore).
            held = false;
            gate.Release();
            await ShellFixture.PumpUntil(() => designer.LastRead?.Project is { } read && Path.GetFileName(read) == "WinFormsApp.csproj", seconds: 300, () => Describe("a read in the project"));
            var withForms = f.Shell.Solutions.CompilerInputs.Values.Any(ReferencesWindowsForms);
            Assert.True(designer.LastRead!.IsPartial == !withForms, Describe($"a read that's {(withForms ? "complete (the compilation has Windows Forms)" : "still partial (no Windows Forms before the restore)")}"));

            // The restore: the C# service loads the restored inputs (loading, then ready). A partial read is read again,
            // complete, through the compilation, and loads again; a complete read isn't read again.
            await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300, () => Describe("the C# service ready"));
            await designer.WhenIdleAsync();
            var loads = hosts[0].Loads;
            await RestoreAndWaitForReloadAsync(f, Describe);
            if (withForms)
            {
                await designer.WhenIdleAsync();
                Assert.Equal(loads, hosts[0].Loads);
            }
            else
            {
                await ShellFixture.PumpUntil(() => designer.LastRead is { IsPartial: false } && hosts[0].Loads > loads, seconds: 60, () => Describe("a complete read loaded again"));
            }

            Assert.True(designer.LastRead is { IsPartial: false, IsDesignable: true }, Describe("a complete read"));
            Assert.Equal(CounterControlType, designer.LastRead!.Model!.Components.Single(c => c.Name == "counterControl").Type);

            // Loading, then ready, again: a complete read returns at once, nothing loads.
            await designer.WhenIdleAsync();
            loads = hosts[0].Loads;
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Loading, 1, 0));
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
            Assert.True(designer.ReadAgainIfPartialAsync().IsCompleted);
            await ShellFixture.PumpUntil(() => f.Shell.Toolbox.Items.Any(i => i.Type == CounterControlType), seconds: 60, () => DescribeToolbox(f));
            await designer.ToolboxLoad;
            await designer.WhenIdleAsync();
            Assert.Equal(loads, hosts[0].Loads);
            Assert.Single(hosts);
        }
        finally
        {
            startHeld.TrySetResult();
            if (held)
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// #76: a read again never starts a host and never closes the designer. A designer whose host failed to start is read
    /// again when the C# service has loaded the projects, keeps the failure's reason and starts nothing (the next build
    /// starts it, #66); a read again that finds the form no longer designable (its code changed meanwhile) says why and
    /// the designer stays open, without opening the code.
    /// </summary>
    [AvaloniaFact]
    public async Task A_form_read_again_starts_no_host_and_stays_open_when_no_longer_designable()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.RestoreOnOpenSetting, false, SettingScope.User, CancellationToken.None);
        var folder = f.CopySample("WindowsDesktop");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        var hosts = new List<FakeWinFormsDesignerHost>();
        var attempts = 0;
        const string Failure = "The designer didn't start: copying WinFormsApp.dll failed: The process cannot access the file because it is being used by another process.";
        UseFakeDesignerHost(f, hosts, starting: () =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new Codalus.Workbench.Previewer.PreviewerException(Failure));
        });
        var gate = f.Shell.Languages.FlushGate;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinFormsApp.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);

            // The form opens with its code closed while the C# host has no project (a partial read), and its host fails to start.
            var form = Path.Combine(folder, "WinFormsApp", "OrderForm.cs");
            Assert.Null(await f.Shell.OpenFileAsync(form));
            var designer = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
            await designer.WhenIdleAsync();
            string Describe(string what) => $"{what}: \"{designer.Status}\", last read {designer.LastRead}, attempts {Volatile.Read(ref attempts)}, hosts {hosts.Count}\n{f.Shell.WinFormsDesignerLog.GetText()}";
            Assert.True(designer.LastRead is { IsDesignable: true, IsPartial: true }, Describe("a partial read"));
            Assert.Equal(Failure, designer.Status);
            Assert.Null(designer.Host);
            Assert.Equal(1, Volatile.Read(ref attempts));
            var first = designer.LastRead;

            // The C# service loads (loading, then ready): the form is read again, the host isn't started, the reason stays.
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Loading, 1, 0));
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
            Assert.Equal(Failure, designer.Status); // no "Reading…" over the reason while it reads
            await designer.WhenIdleAsync();
            Assert.NotSame(first, designer.LastRead);
            Assert.True(designer.LastRead is { IsDesignable: true, IsPartial: true }, Describe("a partial read again"));
            Assert.Contains("reading OrderForm.cs again", f.Shell.WinFormsDesignerLog.GetText(), StringComparison.Ordinal);
            Assert.Equal(Failure, designer.Status);
            Assert.Null(designer.Host);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Empty(hosts);

            // The code changes on disk (the base is no form now): the next read again says why, and the designer stays open.
            var code = await File.ReadAllTextAsync(form, TestContext.Current.CancellationToken);
            Assert.Contains("partial class OrderForm : Form", code, StringComparison.Ordinal);
            await File.WriteAllTextAsync(form, code.Replace("partial class OrderForm : Form", "partial class OrderForm : System.IO.MemoryStream", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Loading, 1, 0));
            f.Shell.OnWinFormsLanguageStatus(ShellHost.CSharpLanguageId, new WorkspaceStatusParams(WorkspaceStates.Ready, 1, 0));
            await designer.WhenIdleAsync();
            Assert.True(designer.LastRead is { Model: null, Problem: not null }, Describe("a read that isn't designable"));
            Assert.Equal(designer.LastRead!.Problem, designer.Status);
            Assert.Contains(designer, f.Shell.Layout.Documents);
            Assert.False(f.Shell.HasEditorFor(form));
            Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message == designer.LastRead.Problem);
            Assert.Null(designer.Host);
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Empty(hosts);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The Toolbox shows the active designer's project: a designer that finishes starting after another became the
    /// active document doesn't ask (its answer, then the newest request, replaced the active designer's).
    /// </summary>
    [AvaloniaFact]
    public async Task A_designer_that_starts_behind_the_active_one_leaves_it_the_toolbox()
    {
        var (f, app) = await OpenAsync();
        await using var _ = f;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        UseFakeDesignerHost(f, [], () => Interlocked.Increment(ref starts) == 1 ? first.Task : Task.CompletedTask);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 300,
            () => DescribeToolbox(f, "the C# service to be ready"));

        // OrderForm's designer waits for its host (a build for the designer) while MainForm's opens and becomes active.
        var opening = f.Shell.OpenFileAsync(Path.Combine(app, "OrderForm.cs"));
        await ShellFixture.PumpUntil(() => Volatile.Read(ref starts) == 1, seconds: 60);
        var order = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>());
        Assert.Null(await f.Shell.OpenFileAsync(Path.Combine(app, "MainForm.cs")));
        var main = Assert.Single(f.Shell.Layout.Documents.OfType<WinFormsDesignerDocument>(), d => d != order);
        Assert.Same(main, f.Shell.Layout.ActiveDocument);
        await ShellFixture.PumpUntil(() => main.FrameCount > 0, seconds: 120, () => $"a frame: {main.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        await main.ToolboxLoad;

        // OrderForm's designer starts behind it: it doesn't ask.
        var asked = order.ToolboxRequests;
        first.SetResult();
        Assert.Null(await opening);
        await ShellFixture.PumpUntil(() => order.FrameCount > 0, seconds: 120, () => $"a frame: {order.Status}\n{f.Shell.WinFormsDesignerLog.GetText()}");
        Assert.Same(main, f.Shell.Layout.ActiveDocument);
        Assert.Equal(asked, order.ToolboxRequests);

        // It asks once it's the active document. Its requests are counted: its ask's task doesn't tell, since an ask whose
        // answer had already come in when the C# client's request returned ends at once, as Task.CompletedTask, as did
        // its ask before (macOS CI, run 39796640525; about one run in ten on Linux).
        var activated = new List<string>();
        void OnActivated(object? sender, Docking.ShellDocument? document) => activated.Add(document?.Title ?? "none");
        f.Shell.Layout.ActiveDocumentChanged += OnActivated;
        f.Shell.Layout.OpenDocument(order, focus: true);
        f.Shell.Layout.ActiveDocumentChanged -= OnActivated;
        if (order.ToolboxRequests != asked + 1)
        {
            Assert.Fail($"OrderForm's designer made {order.ToolboxRequests - asked} request(s) for the Toolbox's items once active: "
                + $"activations [{string.Join(", ", activated)}], active {f.Shell.Layout.ActiveDocument?.Title ?? "none"}, "
                + $"its tab group's active tab {(order.Owner is global::Dock.Model.Core.IDock owner ? (owner.ActiveDockable as Docking.ShellDocument)?.Title ?? "none" : "(no tab group)")}, "
                + $"Properties' source {(ReferenceEquals(f.Shell.PropertySource, order) ? "OrderForm's designer" : f.Shell.PropertySource?.ToString() ?? "none")}, "
                + $"its project {order.ToolboxProject ?? "not known"}, {(order.IsClosed ? "closed" : "open")}, C# client {(f.Shell.Languages.RunningClient(ShellHost.CSharpLanguageId) is null ? "not running" : "running")}.\n"
                + DescribeToolbox(f, "the ask"));
        }

        await order.ToolboxLoad;
    }

    /// <summary>
    /// The Toolbox's project items asked again: the newest request's answer is shown (a slower earlier one, such as an
    /// answer from before the restore, doesn't replace it), and the same items again change nothing: a collapsed
    /// category stays collapsed and the armed item stays one of the Toolbox's.
    /// </summary>
    [AvaloniaFact]
    public void The_toolbox_shows_the_newest_answer_and_the_same_items_change_nothing()
    {
        var toolbox = new ToolboxViewModel { IsAvailable = true };
        var counter = new WinFormsToolboxItem(CounterControlType, "CounterControl", "WinFormsApp", true);
        var earlier = toolbox.BeginProjectItemsRequest();
        var newer = toolbox.BeginProjectItemsRequest();
        toolbox.SetProjectItems(newer, [counter]);
        toolbox.SetProjectItems(earlier, []);
        Assert.Contains(toolbox.Items, i => i.Type == CounterControlType);
        Assert.Equal("WinFormsApp Components", toolbox.Categories[0].Title);

        var categories = toolbox.Categories.ToList();
        categories[0].IsExpanded = false;
        var armed = toolbox.Armed = toolbox.Items.Single(i => i.Type == CounterControlType);
        toolbox.SetProjectItems(toolbox.BeginProjectItemsRequest(), [counter with { }]);
        Assert.Equal(categories, toolbox.Categories);
        Assert.False(toolbox.Categories[0].IsExpanded);
        Assert.Same(armed, toolbox.Armed);
        Assert.Contains(toolbox.Items, i => ReferenceEquals(i, armed));

        // Other items: the Toolbox shows them.
        toolbox.SetProjectItems(toolbox.BeginProjectItemsRequest(), []);
        Assert.DoesNotContain(toolbox.Items, i => i.Type == CounterControlType);
        Assert.DoesNotContain(toolbox.Categories, c => c.Title == "WinFormsApp Components");
    }
}
