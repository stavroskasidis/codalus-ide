using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The NuGet Package Manager. Enter in the search box searches at once; the README renders as Markdown; the first time
/// the view shows, it loads the sources and the first page.
/// </summary>
public sealed partial class PackageManagerView : UserControl, IPaneView
{
    private PackageManagerViewModel? _viewModel;
    private PackageDetailsViewModel? _details;
    private bool _activated;

    public PackageManagerView()
    {
        InitializeComponent();
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
    }

    public void FocusPrimary() => SearchBox.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel = DataContext as PackageManagerViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelChanged;
        Watch(_viewModel.Details);
        if (!_activated)
        {
            _activated = true;
            _ = _viewModel.ActivateAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        Watch(null);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageManagerViewModel.Details))
        {
            Watch(_viewModel?.Details);
            DetailsScroller.Offset = default;
        }
    }

    private void Watch(PackageDetailsViewModel? details)
    {
        if (_details is not null)
        {
            _details.PropertyChanged -= OnDetailsChanged;
        }

        _details = details;
        if (details is not null)
        {
            details.PropertyChanged += OnDetailsChanged;
        }

        RenderReadme();
    }

    private void OnDetailsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageDetailsViewModel.Readme))
        {
            RenderReadme();
        }
    }

    private void RenderReadme() =>
        ReadmeHost.Content = _details?.Readme is { Length: > 0 } readme
            ? MarkdownRenderer.Render(readme, url => _viewModel?.OpenUrl(url))
            : null;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is { } vm)
        {
            _ = vm.SearchNowAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && List.ItemCount > 0)
        {
            List.SelectedIndex = Math.Max(0, List.SelectedIndex);
            List.ContainerFromIndex(List.SelectedIndex)?.Focus();
            e.Handled = true;
        }
    }
}
