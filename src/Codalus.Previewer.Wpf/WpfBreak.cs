using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Diagnostics;
using static Codalus.Previewer.Wpf.WpfTree;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// XAML breakpoints in a running WPF app (M64b, ADR 0103; Windows only), with the Avalonia agent's protocol
/// (<c>xbreaks</c>, <c>watch</c>, <c>event xstop …</c>). WPF gives an element its XAML file and line at run time
/// (<see cref="VisualDiagnostics.GetXamlSourceInfo"/>, with <c>ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO</c>, which the IDE
/// sets, and a Debug build's BAML), and tells when an element joins the visual tree
/// (<see cref="VisualDiagnostics.VisualTreeChanged"/>, under a debugger); it has no notice of an element's creation, so:
/// <list type="bullet">
/// <item>an element breakpoint stops when an element of that line joins the visual tree (after XAML set its
/// properties; an element that never shows, a resource's, doesn't stop);</item>
/// <item>an attribute breakpoint stops when a binding or dynamic resource sets the property, from the moment the
/// element joined the visual tree; a plain value XAML sets before then can't be stopped at (its state says so);</item>
/// <item>Break When Value Changes uses the property's <see cref="DependencyPropertyDescriptor"/>;</item>
/// <item>a failed binding stops on the thread that traced it (WPF's UI thread).</item>
/// </list>
/// </summary>
internal static class WpfBreak
{
    public const string BreakpointsVariable = "CODALUS_LIVE_AGENT_BREAKPOINTS";

    private sealed record Breakpoint(string Id, string Kind, string File, int Line, int Column, string Property, bool Binding);

    private static readonly object s_gate = new();
    private static readonly ManualResetEventSlim s_connected = new();
    private static List<Breakpoint> s_breakpoints = [];
    private static bool s_breakOnBindings;
    private static Action<string>? s_send;
    private static string? s_unsupported;
    private static int s_hooked;

    public static bool BreakOnBindings => Volatile.Read(ref s_breakOnBindings);

    // ------------------------------------------------------------------ the breakpoints

    private static void Configure(string text)
    {
        var breakpoints = new List<Breakpoint>();
        var bindings = false;
        foreach (var line in text.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f[0] == "bindings" && f.Length > 1)
            {
                bindings = f[1] == "1";
            }
            else if (f[0] == "bp" && f.Length >= 8)
            {
                breakpoints.Add(new Breakpoint(f[1], f[2], f[3], Int(f[4]), Int(f[5]), f[6], f[7] == "1"));
            }
        }

        lock (s_gate)
        {
            s_breakpoints = breakpoints;
        }

        Volatile.Write(ref s_breakOnBindings, bindings);
    }

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static List<Breakpoint> Breakpoints()
    {
        lock (s_gate)
        {
            return s_breakpoints;
        }
    }

    /// <summary>The launch's breakpoints (the startup hook).</summary>
    public static void ConfigureFromEnvironment()
    {
        var encoded = Environment.GetEnvironmentVariable(BreakpointsVariable);
        Environment.SetEnvironmentVariable(BreakpointsVariable, null);
        if (!string.IsNullOrEmpty(encoded))
        {
            try
            {
                Configure(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            }
            catch (FormatException)
            {
            }
        }
    }

    /// <summary><c>xbreaks [base64]</c>: replaces the breakpoints; each one's id, 1 or 0, why not.</summary>
    public static string Set(string base64)
    {
        if (base64.Length > 0)
        {
            Configure(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }

        return Join(Breakpoints().Select(b =>
        {
            var problem = s_unsupported
                ?? (b.Kind == "attribute" && !b.Binding
                    ? "WPF tells an element when it joins the visual tree, after XAML set its properties: an attribute breakpoint stops only at a binding or dynamic resource. Put the breakpoint on the element."
                    : null);
            return new[] { b.Id, problem is null ? "1" : "0", problem ?? string.Empty };
        }));
    }

    /// <summary>The IDE's connection; the visual tree's notifications from now on (the app's first window comes after).</summary>
    public static void Connect(Action<string> send)
    {
        lock (s_gate)
        {
            s_send = send;
        }

        s_connected.Set();
        Hook();
    }

    /// <summary>Listens to the visual tree's changes (WPF allows it under a debugger, or with its environment variable).</summary>
    public static void Hook()
    {
        if (Interlocked.Exchange(ref s_hooked, 1) == 1)
        {
            return;
        }

        try
        {
            VisualDiagnostics.EnableVisualTreeChanged();
            VisualDiagnostics.VisualTreeChanged += OnVisualTreeChanged;
        }
        catch (InvalidOperationException ex)
        {
            s_unsupported = "WPF's visual tree notifications aren't available in this process: " + ex.Message;
        }
    }

    private static bool SameFile(string a, string b) => string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private static void OnVisualTreeChanged(object? sender, VisualTreeChangeEventArgs e)
    {
        if (e.ChangeType != VisualTreeChangeType.Add || e.Child is not { } child)
        {
            return;
        }

        try
        {
            var breakpoints = Breakpoints();
            if (breakpoints.Count == 0 || VisualDiagnostics.GetXamlSourceInfo(child) is not { SourceUri: { } uri } info)
            {
                return;
            }

            var file = uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString;
            foreach (var breakpoint in breakpoints)
            {
                if (breakpoint.Line != info.LineNumber || !SameFile(breakpoint.File, file))
                {
                    continue;
                }

                if (breakpoint.Kind == "attribute")
                {
                    if (breakpoint.Binding && LiveAgent.PropertiesOf(child).FirstOrDefault(p => p.Name == breakpoint.Property).Property is { } property)
                    {
                        WatchValue(child, property, breakpoint.Property, "attribute", breakpoint.Id, file, info.LineNumber, info.LinePosition);
                    }

                    continue;
                }

                Stop(new[] { "element", breakpoint.Id, file, Text(info.LineNumber), Text(info.LinePosition), child.GetType().FullName ?? TypeName(child), Name(child), string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty }, child);
            }
        }
        catch (InvalidOperationException)
        {
            // A breakpoint never breaks the app.
        }
    }

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ values

    /// <summary>Watches a property's changes on one element; the returned action stops watching.</summary>
    public static Action WatchValue(DependencyObject element, DependencyProperty property, string name, string kind, string id, string file, int line, int column)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
        var old = element.GetValue(property);
        EventHandler handler = (_, _) =>
        {
            var value = element.GetValue(property);
            var previous = old;
            old = value;
            Stop(new[] { kind, id, file, Text(line), Text(column), element.GetType().FullName ?? TypeName(element), Name(element), name, LiveAgent.Format(previous), LiveAgent.Format(value), string.Empty, string.Empty, string.Empty }, element);
        };
        descriptor.AddValueChanged(element, handler);
        return () => descriptor.RemoveValueChanged(element, handler);
    }

    /// <summary>Break When Value Changes on a live element's property (the Live Property Explorer).</summary>
    public static Action ValueWatch(DependencyObject element, DependencyProperty property, string name)
    {
        var info = VisualDiagnostics.GetXamlSourceInfo(element);
        var file = info?.SourceUri is { } uri ? (uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString) : string.Empty;
        return WatchValue(element, property, name, "value", string.Empty, file, info?.LineNumber ?? 0, info?.LinePosition ?? 0);
    }

    // ------------------------------------------------------------------ failed bindings

    /// <summary>A failed binding (its record: property, path, message, type, name), on the thread that traced it.</summary>
    public static void OnBindingFailure(string[] failure)
    {
        if (BreakOnBindings && Debugger.IsAttached)
        {
            Stop(new[] { "binding", string.Empty, string.Empty, "0", "0", failure[3], failure[4], failure[0], string.Empty, string.Empty, failure[2], failure[1], string.Empty }, null);
        }
    }

    // ------------------------------------------------------------------ the stop

    /// <summary>Tells the IDE where and why, then breaks into the debugger on this thread (Locals: the stop and the element).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void Stop(string[] stop, DependencyObject? element)
    {
        if (!Debugger.IsAttached || !s_connected.Wait(TimeSpan.FromSeconds(10)))
        {
            return;
        }

        Action<string>? send;
        lock (s_gate)
        {
            send = s_send;
        }

        try
        {
            send?.Invoke("event\txstop\t" + string.Join(Field.ToString(), stop.Select(Clean)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return;
        }

        GC.KeepAlive(element);
        Debugger.Break();
    }
}
