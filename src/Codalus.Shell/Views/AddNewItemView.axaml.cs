using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// Add New Item: the search box has the focus first; Enter adds. The preview is a read-only code view in the editor's
/// colors (M34h).
/// </summary>
public sealed partial class AddNewItemView : LoadingView
{
    private AddNewItemViewModel? _vm;
    private CodeViewer? _preview;

    public AddNewItemView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AddNewItemViewModel);
    }

    /// <summary>The preview's code view (tests).</summary>
    public CodeViewer? Preview => _preview;

    public override void FocusPrimary() => SearchBox.Focus();

    private void Attach(AddNewItemViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnPropertyChanged;
        }

        _vm = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnPropertyChanged;
        ShowPreview();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddNewItemViewModel.Preview))
        {
            ShowPreview();
        }
    }

    private void ShowPreview()
    {
        if (_vm is null || !this.IsAttachedToVisualTree())
        {
            return;
        }

        if (_preview is null)
        {
            _preview = new CodeViewer(_vm.Services, _vm.Font) { EditorColors = true };
            _preview.Editor.ShowLineNumbers = false;
            AutomationProperties.SetName(_preview.Editor, Localizer.Get("addNewItem.preview", "Preview"));
            PreviewHost.Child = _preview;
        }

        var language = _vm.PreviewLanguage;
        // What dotnet new will be asked to do is prose: it wraps.
        _preview.Editor.WordWrap = language is null;
        _preview.Show(_vm.Preview, language, null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnPropertyChanged;
            _vm.PropertyChanged += OnPropertyChanged;
            ShowPreview();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnPropertyChanged;
        }

        // The code view follows the editor's theme through the shell's event: released with the view, made again when
        // the document shows again.
        _preview?.Release();
        _preview = null;
        PreviewHost.Child = null;
    }
}
