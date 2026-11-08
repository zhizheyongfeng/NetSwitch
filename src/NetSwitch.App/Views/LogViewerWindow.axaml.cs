using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetSwitch.App.Services;

namespace NetSwitch.App.Views;

public sealed record LogFileItem(string Name, string Path, string SizeText)
{
    public override string ToString() => $"{Name}  ({SizeText})";
}

/// <summary>
/// In-app log viewer. Deliberately does not launch Explorer or a text editor: the project
/// only uses OS APIs and never starts external processes.
/// </summary>
public partial class LogViewerWindow : Window
{
    private const int MaxDisplayedBytes = 512 * 1024;
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = AutoRefreshInterval };
    private string? _currentPath;
    private long _lastLength = -1;

    public LogViewerWindow()
    {
        InitializeComponent();
        _directory = string.Empty;
        _refreshTimer.Tick += OnAutoRefreshTick;
    }

    public LogViewerWindow(string logDirectory)
        : this()
    {
        _directory = logDirectory;
        DirectoryText.Text = logDirectory;
        Refresh();
        _refreshTimer.Start();
    }

    private void Refresh(string? preferredPath = null)
    {
        preferredPath ??= _currentPath;
        try
        {
            if (!Directory.Exists(_directory))
            {
                FileList.ItemsSource = Array.Empty<LogFileItem>();
                LogText.Text = "日志目录不存在。";
                StatusText.Text = "目录不可用";
                _currentPath = null;
                _lastLength = -1;
                return;
            }

            var files = new DirectoryInfo(_directory)
                .GetFiles("netswitch-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new LogFileItem(
                    f.Name,
                    f.FullName,
                    FormatSize(f.Length)))
                .ToArray();

            FileList.ItemsSource = files;

            var selectedIndex = preferredPath is null
                ? (files.Length > 0 ? 0 : -1)
                : Array.FindIndex(files, f => string.Equals(f.Path, preferredPath, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0 && files.Length > 0)
            {
                selectedIndex = 0;
            }

            FileList.SelectedIndex = selectedIndex;

            StatusText.Text = files.Length == 0
                ? "尚无日志文件"
                : $"{files.Length} 个日志文件 · 目录 {_directory}";
            if (files.Length == 0)
            {
                LogText.Text = "尚无日志文件。应用或恢复操作后会自动写入。";
                _currentPath = null;
                _lastLength = -1;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取日志失败：{ex.Message}";
        }
    }

    private void LoadFile(LogFileItem item)
    {
        try
        {
            using var stream = File.Open(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, MaxDisplayedBytes);
            stream.Seek(-length, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (stream.Length > MaxDisplayedBytes)
            {
                text = $"（仅显示最后 {FormatSize(MaxDisplayedBytes)}）{Environment.NewLine}{text}";
            }

            LogText.Text = text;
            LogText.CaretIndex = text.Length;
            _currentPath = item.Path;
            _lastLength = stream.Length;
            StatusText.Text = $"{item.Name} · {item.SizeText}";
        }
        catch (Exception ex)
        {
            LogText.Text = $"无法读取日志：{ex.Message}";
        }
    }

    private void FileList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is LogFileItem item)
        {
            LoadFile(item);
        }
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (AutoRefreshBox.IsChecked != true)
        {
            return;
        }

        try
        {
            if (_currentPath is null || FileList.SelectedItem is not LogFileItem current)
            {
                Refresh();
                return;
            }

            var info = new FileInfo(current.Path);
            if (!info.Exists)
            {
                // The current file was deleted or rolled over; fall back to the newest file.
                _currentPath = null;
                Refresh();
                return;
            }

            if (info.Length != _lastLength)
            {
                LoadFile(current);
            }
        }
        catch
        {
            // Auto refresh is best effort; a manual refresh is always available.
        }
    }

    private void Refresh_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Refresh();

    private async void CopySelected_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is null)
            {
                return;
            }

            var selected = LogText.SelectedText;
            if (string.IsNullOrEmpty(selected))
            {
                StatusText.Text = "请先在日志内容中拖选要复制的文本。";
                return;
            }

            await Clipboard.SetTextAsync(selected);
            StatusText.Text = $"已复制 {selected.Length} 个字符到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async void Copy_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(LogText.Text ?? string.Empty);
                StatusText.Text = "日志内容已复制到剪贴板";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async void DeleteCurrent_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not LogFileItem item)
        {
            StatusText.Text = "没有选中日志文件。";
            return;
        }

        var confirmed = await ConfirmAsync(
            "删除日志文件",
            $"确定删除「{item.Name}」吗？\n\n文件路径：{item.Path}\n\n" +
            "删除不可撤销。应用继续运行时会重新创建当天的日志文件。",
            "删除");
        if (!confirmed)
        {
            return;
        }

        try
        {
            File.Delete(item.Path);
            if (string.Equals(_currentPath, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                _currentPath = null;
                _lastLength = -1;
            }

            Refresh();
            StatusText.Text = $"已删除日志文件「{item.Name}」";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败：{ex.Message}";
        }
    }

    private async void DeleteAll_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_directory))
        {
            StatusText.Text = "日志目录不存在。";
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "netswitch-*.log");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取日志失败：{ex.Message}";
            return;
        }

        if (files.Length == 0)
        {
            StatusText.Text = "没有可删除的日志文件。";
            return;
        }

        var total = files.Sum(f => new FileInfo(f).Length);
        var confirmed = await ConfirmAsync(
            "删除全部日志",
            $"将删除 {files.Length} 个日志文件，共 {FormatSize(total)}。\n\n" +
            $"目录：{_directory}\n\n" +
            "删除不可撤销。应用继续运行时会重新创建当天日志。",
            "全部删除");
        if (!confirmed)
        {
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        _currentPath = null;
        _lastLength = -1;
        Refresh();
        StatusText.Text = failed == 0
            ? $"已删除 {deleted} 个日志文件"
            : $"已删除 {deleted} 个日志文件，{failed} 个删除失败（可能正被占用）";
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var window = new MessageWindow(
            title,
            message,
            MessageKind.Warning,
            confirmText,
            cancelText: "取消",
            dangerous: true);
        return await window.ShowDialog<bool>(this);
    }

    private void Close_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        base.OnClosed(e);
    }
}
