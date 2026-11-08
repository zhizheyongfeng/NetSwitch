using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NetSwitch.App.ViewModels;

namespace NetSwitch.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel = null!;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += result => Close(result);
        viewModel.BrowseDataRootRequested += () => PickFolderAsync("选择数据目录");
        viewModel.BrowseConfigFileRequested += PickConfigFileAsync;
        viewModel.BrowseDirectoryRequested += () => PickFolderAsync("选择目录");
        viewModel.PickSaveFileRequested += PickSaveFileAsync;
        viewModel.PickOpenFileRequested += PickOpenFileAsync;
        viewModel.PickSaveLogFileRequested += PickSaveLogFileAsync;
        viewModel.ViewLogsRequested += ShowLogs;
        viewModel.CopyTextRequested += CopyText;
        viewModel.ConfirmRequested += ShowConfirmAsync;
        Opened += async (_, _) => await viewModel.LoadStartupStateAsync();
    }

    private async Task<string?> PickSaveFileAsync(string title, string? suggestedName)
    {
        IStorageFolder? start = null;
        if (Directory.Exists(_viewModel.ResolvedExportDirectory))
        {
            start = await StorageProvider.TryGetFolderFromPathAsync(_viewModel.ResolvedExportDirectory);
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON 配置文件")
                {
                    Patterns = new[] { "*.json" }
                }
            },
            SuggestedStartLocation = start
        });

        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveLogFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志",
            SuggestedFileName = suggestedName,
            DefaultExtension = "log",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("日志文件") { Patterns = new[] { "*.log" } }
            }
        });

        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON 配置文件")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        bool dangerous)
    {
        var window = new MessageWindow(
            title,
            message,
            dangerous ? Services.MessageKind.Warning : Services.MessageKind.Info,
            confirmText,
            cancelText: "取消",
            dangerous: dangerous);
        return await window.ShowDialog<bool>(this);
    }

    private async void ShowLogs()
    {
        try
        {
            var window = new LogViewerWindow(_viewModel.ResolvedLogDirectory);
            await window.ShowDialog(this);
        }
        catch (Exception ex)
        {
            await new MessageWindow("无法打开日志", ex.Message, Services.MessageKind.Error)
                .ShowDialog<bool>(this);
        }
    }

    private async void CopyText(string text)
    {
        try
        {
            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(text);
                _viewModel.DataStatusText = "路径已复制到剪贴板。";
            }
        }
        catch (Exception ex)
        {
            _viewModel.DataStatusText = $"复制失败：{ex.Message}";
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickConfigFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择配置文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON 配置文件")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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
            Close(false);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
