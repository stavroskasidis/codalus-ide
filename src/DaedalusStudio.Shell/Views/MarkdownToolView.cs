using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>A plugin Markdown view. <c>command:&lt;id&gt;</c> links run commands; web links open in the browser.</summary>
public sealed class MarkdownToolView : UserControl, IPaneView
{
    private readonly ScrollViewer _scroller = new() { Focusable = true };
    private MarkdownToolViewModel? _viewModel;

    public MarkdownToolView()
    {
        Content = _scroller;
        Avalonia.Automation.AutomationProperties.SetName(_scroller, "Markdown view");
    }

    public void FocusPrimary() => _scroller.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel = DataContext as MarkdownToolViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.EnsureActivated();
            Render();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkdownToolViewModel.Markdown))
        {
            Render();
        }
    }

    private void Render()
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        _scroller.Content = vm.Markdown.Length == 0
            ? new TextBlock { Text = "Nothing to show.", Margin = new Thickness(12), Classes = { "muted" } }
            : MarkdownRenderer.Render(vm.Markdown, target => OpenLink(vm, target));
    }

    private static void OpenLink(MarkdownToolViewModel vm, string target)
    {
        if (target.StartsWith("command:", StringComparison.Ordinal))
        {
            vm.Run(target["command:".Length..], []);
        }
        else
        {
            vm.Run("workbench.openExternal", [JsonSerializer.SerializeToElement(target, Docking.ShellJsonContext.Default.String)]);
        }
    }
}
