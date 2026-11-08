using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NetSwitch.App.Services;
using NetSwitch.App.ViewModels;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;

namespace NetSwitch.App.Views;

public partial class PresetEditorWindow : Window
{
    private readonly IPresetTester _tester;
    private readonly IAppLog _log;
    private readonly PresetEditorViewModel _viewModel;
    private bool _saved;
    private bool _closingConfirmed;

    public PresetEditorWindow()
    {
        InitializeComponent();
        _tester = null!;
        _log = NullAppLog.Instance;
        _viewModel = null!;
    }

    public PresetEditorWindow(PresetEditorViewModel viewModel, IPresetTester tester, IAppLog? log = null)
        : this()
    {
        _viewModel = viewModel;
        _tester = tester;
        _log = log ?? NullAppLog.Instance;
        DataContext = viewModel;

        viewModel.SaveRequested += preset =>
        {
            _saved = true;
            Close(preset);
        };

        viewModel.CancelRequested += OnCancelRequestedAsync;

        viewModel.TestRequested += async desired =>
        {
            var name = string.IsNullOrWhiteSpace(viewModel.Name) ? "未命名预设" : viewModel.Name;
            var testViewModel = new TestWindowViewModel(
                viewModel.Adapter,
                name,
                desired,
                _tester,
                log: _log);
            await new TestWindow(testViewModel).ShowDialog(this);
        };
    }

    private async void OnCancelRequestedAsync()
    {
        if (_closingConfirmed)
        {
            Close(null);
            return;
        }

        if (!_saved && _viewModel.IsDirty)
        {
            var result = await new MessageWindow(
                    "放弃修改？",
                    "当前预设还有未保存的修改。关闭后这些修改会丢失。",
                    MessageKind.Warning,
                    confirmText: "放弃修改",
                    cancelText: "继续编辑",
                    dangerous: true)
                .ShowDialog<bool>(this);

            if (!result)
            {
                return;
            }
        }

        _closingConfirmed = true;
        Close(null);
    }

    private void Header_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCancelRequestedAsync();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
