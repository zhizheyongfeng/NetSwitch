using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;

namespace NetSwitch.App.ViewModels;

public sealed class CheckRowViewModel
{
    public CheckRowViewModel(TestCheck check)
    {
        Check = check;
    }

    public TestCheck Check { get; }

    public TestStatus Status => Check.Status;

    public string Name => Check.Name;

    public string Message => Check.Message;

    public string StatusText => Status switch
    {
        TestStatus.Pass => "通过",
        TestStatus.Warn => "警告",
        TestStatus.Fail => "失败",
        TestStatus.Skipped => "跳过",
        TestStatus.Aborted => "中止",
        _ => string.Empty
    };

    public string LatencyText => Check.Latency is null
        ? string.Empty
        : Check.Latency.Value.TotalMilliseconds >= 1000
            ? $"{Check.Latency.Value.TotalSeconds:0.0} s"
            : $"{Check.Latency.Value.TotalMilliseconds:0} ms";

    public bool HasLatency => Check.Latency is not null;
}

public sealed partial class TestWindowViewModel : ObservableObject
{
    private readonly AdapterInfo _adapter;
    private readonly DesiredState _desired;
    private readonly IPresetTester _tester;
    private readonly TestOptions _options;
    private readonly IAppLog _log;
    private readonly string _presetName;

    public TestWindowViewModel(
        AdapterInfo adapter,
        string presetName,
        DesiredState desired,
        IPresetTester tester,
        TestOptions? options = null,
        IAppLog? log = null)
    {
        _adapter = adapter;
        _desired = desired;
        _tester = tester;
        _options = options ?? TestOptions.Default;
        _log = log ?? NullAppLog.Instance;
        _presetName = presetName;

        Title = $"测试：{presetName}";
        Subtitle = $"{adapter.Key.FriendlyName} · {adapter.Description}";
        PromptMessage =
            "测试会临时应用这套配置，可能短暂断网或 DNS 中断 10–30 秒，结束后自动恢复原配置。" +
            "只有点击“继续”才会修改网络，静态检查本身不会改动任何设置。";
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string PromptMessage { get; }

    public ObservableCollection<CheckRowViewModel> Checks { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "正在执行静态检查…";

    [ObservableProperty]
    private bool _showStatic = true;

    [ObservableProperty]
    private bool _showTrial;

    [ObservableProperty]
    private bool _showPrompt;

    [ObservableProperty]
    private bool _canContinue;

    [ObservableProperty]
    private string _bannerTitle = "正在检查…";

    [ObservableProperty]
    private string _bannerMessage = string.Empty;

    [ObservableProperty]
    private string _bannerTone = "info";

    [ObservableProperty]
    private string _stageText = "1 / 2 · 静态检查";

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _durationText = string.Empty;

    [ObservableProperty]
    private string _restoreText = string.Empty;

    [ObservableProperty]
    private bool _restored;

    [ObservableProperty]
    private bool _trialApplied;

    public bool ShowBlocking => ShowStatic && !IsBusy && !CanContinue;

    public event Action? CloseRequested;

    partial void OnShowStaticChanged(bool value) => OnPropertyChanged(nameof(ShowBlocking));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowBlocking));

    partial void OnCanContinueChanged(bool value) => OnPropertyChanged(nameof(ShowBlocking));

    public async Task InitializeAsync()
    {
        await RunStaticAsync();
    }

    [RelayCommand]
    private async Task ContinueTrialAsync()
    {
        if (!CanContinue || IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在临时应用配置并检查连通性…";
        ShowStatic = false;
        ShowPrompt = false;
        ShowTrial = true;
        StageText = "2 / 2 · 连通性测试";
        BannerTitle = "正在执行连通性测试";
        BannerMessage = "测试结束后会自动恢复原配置，请稍候。";
        BannerTone = "info";
        Checks.Clear();
        await LogAsync($"连通性测试开始：适配器「{_adapter.Key.FriendlyName}」，预设「{_presetName}」");

        try
        {
            var report = await _tester.RunTrialAsync(_adapter.Key, _desired, _options);
            await ApplyTrialReportAsync(report);
        }
        catch (Exception ex)
        {
            BannerTitle = "连通性测试失败";
            BannerMessage = ex.Message;
            BannerTone = "error";
            Checks.Add(new CheckRowViewModel(TestCheck.Fail("测试事务", ex.Message)));
            await LogAsync($"连通性测试异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ShowTrial = false;
        ShowStatic = true;
        await RunStaticAsync();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private async Task RunStaticAsync()
    {
        IsBusy = true;
        BusyText = "正在执行静态检查…";
        ShowStatic = true;
        ShowTrial = false;
        ShowPrompt = false;
        CanContinue = false;
        StageText = "1 / 2 · 静态检查";
        Checks.Clear();

        try
        {
            var report = await _tester.ValidateAsync(_adapter.Key, _desired);
            foreach (var check in report.Checks)
            {
                Checks.Add(new CheckRowViewModel(check));
            }

            var warnings = report.Checks.Count(c => c.Status == TestStatus.Warn);
            var failures = report.Checks.Count(c => c.Status == TestStatus.Fail);
            CanContinue = report.CanApply;
            await LogAsync(
                $"静态检查：适配器「{_adapter.Key.FriendlyName}」，预设「{_presetName}」：" +
                $"{(failures > 0 ? "存在阻断性问题" : warnings > 0 ? "有警告" : "通过")}" +
                $"（通过 {report.Checks.Count(c => c.Status == TestStatus.Pass)}，" +
                $"警告 {warnings}，错误 {failures}）");
            foreach (var check in report.Checks.Where(c => c.Status != TestStatus.Pass))
            {
                await LogAsync($"  静态检查 {StatusText(check.Status)}：{check.Name} - {check.Message}");
            }

            if (failures > 0)
            {
                BannerTitle = $"静态检查有 {failures} 项错误";
                BannerMessage = "存在阻断性问题，修正预设后才能继续。";
                BannerTone = "error";
            }
            else if (warnings > 0)
            {
                BannerTitle = $"静态检查有 {warnings} 项警告";
                BannerMessage = "没有阻断性错误，可以继续做连通性测试，但静态检查通过不代表应用后一定连通。";
                BannerTone = "warning";
            }
            else
            {
                BannerTitle = "静态检查通过";
                BannerMessage = "没有发现问题。你可以继续执行会短暂断网的连通性测试。";
                BannerTone = "success";
            }

            ShowPrompt = report.CanApply;
        }
        catch (Exception ex)
        {
            BannerTitle = "静态检查失败";
            BannerMessage = ex.Message;
            BannerTone = "error";
            Checks.Add(new CheckRowViewModel(TestCheck.Fail("静态检查", ex.Message)));
            await LogAsync($"静态检查异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyTrialReportAsync(ConnectivityTestReport report)
    {
        Checks.Clear();
        foreach (var check in report.Checks)
        {
            Checks.Add(new CheckRowViewModel(check));
        }

        foreach (var check in report.Checks)
        {
            await LogAsync($"  连通性测试 {StatusText(check.Status)}：{check.Name} - {check.Message}");
        }

        TrialApplied = report.TrialApplied;
        Restored = report.Restored;
        DurationText = report.Duration.TotalSeconds < 1
            ? $"{report.Duration.TotalMilliseconds:0} ms"
            : $"{report.Duration.TotalSeconds:0.0} 秒";
        RestoreText = report.Restored
            ? "原网络配置已恢复"
            : "原网络配置恢复失败，请立即手动检查";
        SummaryText = BuildSummary(report.Checks);

        switch (report.Overall)
        {
            case TestOverallStatus.Pass:
                BannerTitle = "连通性测试通过";
                BannerMessage = "临时配置下网络工作正常，原配置已恢复。";
                BannerTone = "success";
                break;
            case TestOverallStatus.Warn:
                BannerTitle = "连通性测试通过，但有警告";
                BannerMessage = "网络基本可用，仍建议查看下列警示项。原配置已恢复。";
                BannerTone = "warning";
                break;
            case TestOverallStatus.RestoreFailed:
                BannerTitle = "恢复原配置失败";
                BannerMessage = "请立即手动检查网络设置，确认当前配置是否符合预期。";
                BannerTone = "error";
                break;
            case TestOverallStatus.Fail:
                BannerTitle = "连通性测试失败";
                BannerMessage = "临时配置下存在连通性问题，原配置已恢复。";
                BannerTone = "error";
                break;
            default:
                BannerTitle = "连通性测试已中止";
                BannerMessage = report.Restored
                    ? "测试未能完成，原配置已恢复。"
                    : "测试未能完成，且原配置可能未恢复，请手动检查。";
                BannerTone = "warning";
                break;
        }

        await LogAsync(
            $"连通性测试结束：{BannerTitle}；{SummaryText}；{RestoreText}（耗时 {DurationText}）");
    }

    private async Task LogAsync(string message)
    {
        try
        {
            await _log.WriteAsync(message);
        }
        catch
        {
            // Logging must never break a test.
        }
    }

    private static string StatusText(TestStatus status) => status switch
    {
        TestStatus.Pass => "通过",
        TestStatus.Warn => "警告",
        TestStatus.Fail => "失败",
        TestStatus.Skipped => "跳过",
        TestStatus.Aborted => "中止",
        _ => "未知"
    };

    private static string BuildSummary(IReadOnlyList<TestCheck> checks)
    {
        var pass = checks.Count(c => c.Status == TestStatus.Pass);
        var warn = checks.Count(c => c.Status == TestStatus.Warn);
        var fail = checks.Count(c => c.Status == TestStatus.Fail);
        var skip = checks.Count(c => c.Status == TestStatus.Skipped);

        var parts = new List<string>();
        if (pass > 0)
        {
            parts.Add($"{pass} 项通过");
        }

        if (warn > 0)
        {
            parts.Add($"{warn} 项警告");
        }

        if (fail > 0)
        {
            parts.Add($"{fail} 项失败");
        }

        if (skip > 0)
        {
            parts.Add($"{skip} 项跳过");
        }

        return string.Join("，", parts);
    }
}
