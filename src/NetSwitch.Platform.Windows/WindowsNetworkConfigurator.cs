using System.Diagnostics;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;
using NetSwitch.Core.Storage;
using NetSwitch.Core.Validation;
using NetSwitch.Platform.Windows.Native;

namespace NetSwitch.Platform.Windows;

/// <summary>
/// Applies IPv4 presets on Windows, verifies the result by reading the system back, and
/// rolls back on any failure. A pending snapshot is written before touching the adapter so
/// a crash or power loss can be recovered on the next start.
/// </summary>
public sealed class WindowsNetworkConfigurator : INetworkConfigurator
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VerificationPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAdapterCatalog _catalog;
    private readonly SnapshotStore _store;
    private readonly IAppLog _log;
    private readonly IPrivilegeService _privileges;
    private readonly IPlatformCapabilities _capabilities;
    private readonly IWirelessController _wireless;
    private readonly WindowsIpv6Writer _ipv6;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsNetworkConfigurator(
        IAdapterCatalog catalog,
        SnapshotStore store,
        IAppLog log,
        IPrivilegeService privileges,
        IPlatformCapabilities capabilities,
        IWirelessController wireless)
    {
        _catalog = catalog;
        _store = store;
        _log = log;
        _privileges = privileges;
        _capabilities = capabilities;
        _wireless = wireless;
        _ipv6 = new WindowsIpv6Writer(catalog);
    }

    public async Task<ApplyResult> ApplyAsync(
        AdapterKey key,
        DesiredState desired,
        ApplyOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var guard = CheckEnvironment();
        if (guard is not null)
        {
            return guard;
        }

        var unsupported = UnsupportedReason(desired);
        if (unsupported is not null)
        {
            return ApplyResult.Fail(ApplyErrorCode.NotSupported, unsupported);
        }

        var invalid = InvalidReason(desired);
        if (invalid is not null)
        {
            return ApplyResult.Fail(ApplyErrorCode.ValidationFailed, invalid);
        }

        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pendingError = await PendingErrorAsync(ct).ConfigureAwait(false);
            if (pendingError is not null)
            {
                return pendingError;
            }

            var before = await _catalog.GetStateAsync(key, ct).ConfigureAwait(false);
            var adapter = await _catalog.GetAdapterAsync(key, ct).ConfigureAwait(false);
            if (before is null || adapter is null)
            {
                return ApplyResult.Fail(
                    ApplyErrorCode.AdapterNotFound,
                    $"找不到适配器：{key.FriendlyName}。它可能已被禁用或移除。",
                    duration: stopwatch.Elapsed);
            }

            var transaction = new PendingTransaction(
                PendingTransaction.CurrentVersion,
                string.IsNullOrWhiteSpace(options.TransactionId)
                    ? Guid.NewGuid().ToString("N")
                    : options.TransactionId,
                options.Kind,
                key,
                adapter.Key.FriendlyName,
                null,
                null,
                DateTimeOffset.Now,
                before);

            try
            {
                await _store.WritePendingAsync(transaction, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await LogAsync($"写入事务快照失败：{ex.Message}").ConfigureAwait(false);
                return ApplyResult.Fail(
                    ApplyErrorCode.SnapshotFailed,
                    $"无法写入事务快照，已取消应用：{ex.Message}",
                    before,
                    before,
                    duration: stopwatch.Elapsed);
            }

            await LogAsync(
                $"开始{(options.Kind == TransactionKind.Test ? "试运行" : "应用")} " +
                $"{adapter.Key.FriendlyName} / {adapter.Key.StableId}").ConfigureAwait(false);

            try
            {
                await RunWriteAsync(key, adapter, desired, ct).ConfigureAwait(false);

                var verification = await VerifyAsync(key, desired, ct).ConfigureAwait(false);
                if (!verification.IsMatch)
                {
                    var rollback = await RestoreSnapshotAsync(
                            key, adapter, before, CancellationToken.None)
                        .ConfigureAwait(false);
                    var outcome = rollback.Success
                        ? SnapshotRecord.OutcomeRolledBack
                        : SnapshotRecord.OutcomeRollbackFailed;

                    var message =
                        $"配置写入后校验未通过：{FormatIssues(verification)}。" +
                        (rollback.Success
                            ? "已自动恢复到应用前的配置。"
                            : $"自动恢复也失败了：{rollback.Message}");

                    await CompleteBestEffortAsync(
                            transaction,
                            outcome,
                            message,
                            setLast: false,
                            keepPending: !rollback.Success)
                        .ConfigureAwait(false);
                    await LogAsync(message).ConfigureAwait(false);

                    var after = await SafeStateAsync(key).ConfigureAwait(false);
                    return ApplyResult.Fail(
                        ApplyErrorCode.VerificationFailed,
                        message,
                        before,
                        after ?? rollback.After,
                        duration: stopwatch.Elapsed);
                }

                var applied = await SafeStateAsync(key).ConfigureAwait(false) ?? before;

                await CompleteBestEffortAsync(
                        transaction,
                        SnapshotRecord.OutcomeSucceeded,
                        null,
                        setLast: options.Kind == TransactionKind.Apply)
                    .ConfigureAwait(false);

                var successMessage =
                    $"已应用并校验「{adapter.Key.FriendlyName}」的 {DescribeScope(desired)} 配置。";
                await LogAsync(successMessage).ConfigureAwait(false);

                return ApplyResult.Ok(
                    successMessage,
                    before,
                    applied,
                    snapshotPath: null,
                    stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                var (message, restored) = await RollbackAfterFailureAsync(
                        key,
                        adapter,
                        before,
                        transaction,
                        "操作已取消，正在恢复应用前的配置。")
                    .ConfigureAwait(false);
                return ApplyResult.Fail(
                    restored ? ApplyErrorCode.Cancelled : ApplyErrorCode.RollbackFailed,
                    message,
                    before,
                    await SafeStateAsync(key).ConfigureAwait(false),
                    duration: stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                var (message, restored) = await RollbackAfterFailureAsync(
                        key,
                        adapter,
                        before,
                        transaction,
                        ex.Message)
                    .ConfigureAwait(false);
                return ApplyResult.Fail(
                    restored ? ApplyErrorCode.ApplyFailed : ApplyErrorCode.RollbackFailed,
                    message,
                    before,
                    await SafeStateAsync(key).ConfigureAwait(false),
                    duration: stopwatch.Elapsed);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> RestoreAsync(
        AdapterKey key,
        AdapterState snapshot,
        ApplyOptions options,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var guard = CheckEnvironment();
        if (guard is not null)
        {
            return guard;
        }

        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pendingError = await PendingErrorAsync(ct).ConfigureAwait(false);
            if (pendingError is not null)
            {
                return pendingError;
            }

            var adapter = await _catalog.GetAdapterAsync(key, ct).ConfigureAwait(false);
            if (adapter is null)
            {
                return ApplyResult.Fail(
                    ApplyErrorCode.AdapterNotFound,
                    $"找不到适配器：{key.FriendlyName}。",
                    duration: stopwatch.Elapsed);
            }

            var transaction = new PendingTransaction(
                PendingTransaction.CurrentVersion,
                string.IsNullOrWhiteSpace(options.TransactionId)
                    ? Guid.NewGuid().ToString("N")
                    : options.TransactionId,
                options.Kind,
                key,
                adapter.Key.FriendlyName,
                null,
                null,
                DateTimeOffset.Now,
                snapshot);

            try
            {
                await _store.WritePendingAsync(transaction, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ApplyResult.Fail(
                    ApplyErrorCode.SnapshotFailed,
                    $"无法写入恢复快照：{ex.Message}",
                    duration: stopwatch.Elapsed);
            }

            var before = await SafeStateAsync(key).ConfigureAwait(false);
            var result = await RestoreAndVerifyAsync(key, adapter, snapshot, transaction, before, stopwatch)
                .ConfigureAwait(false);

            if (result.Success)
            {
                await CompleteBestEffortAsync(
                        transaction,
                        SnapshotRecord.OutcomeSucceeded,
                        null,
                        setLast: false)
                    .ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryInfo> GetRecoveryInfoAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return RecoveryInfo.None;
        }

        PendingTransaction? pending = null;
        SnapshotRecord? last = null;

        try
        {
            pending = await _store.ReadPendingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"读取未完成事务失败：{ex.Message}").ConfigureAwait(false);
        }

        try
        {
            last = await _store.ReadLastAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"读取上次配置快照失败：{ex.Message}").ConfigureAwait(false);
        }

        var pendingFileExists = pending is not null || File.Exists(_store.PendingFile);
        return new RecoveryInfo(
            pendingFileExists,
            pending?.Kind,
            pending?.AdapterName ?? (pendingFileExists ? "（快照无法读取）" : null),
            pending?.StartedAt,
            last is not null,
            last?.AdapterName,
            last?.CompletedAt);
    }

    public async Task<ApplyResult> RecoverPendingAsync(CancellationToken ct = default)
    {
        var guard = CheckEnvironment();
        if (guard is not null)
        {
            return guard;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = await _store.ReadPendingAsync(ct).ConfigureAwait(false);
            if (pending is null)
            {
                return ApplyResult.Ok("没有需要恢复的未完成事务。", null, null, null, TimeSpan.Zero);
            }

            var adapter = await _catalog.GetAdapterAsync(pending.AdapterKey, CancellationToken.None)
                .ConfigureAwait(false);
            if (adapter is null)
            {
                await LogAsync(
                    $"未完成事务的适配器不存在：{pending.AdapterName} / {pending.AdapterKey.StableId}")
                    .ConfigureAwait(false);
                return ApplyResult.Fail(
                    ApplyErrorCode.AdapterNotFound,
                    $"无法恢复：适配器「{pending.AdapterName}」已不存在。可手动删除快照文件：{_store.PendingFile}");
            }

            var stopwatch = Stopwatch.StartNew();
            var before = await SafeStateAsync(pending.AdapterKey).ConfigureAwait(false);
            var result = await RestoreAndVerifyAsync(
                    pending.AdapterKey,
                    adapter,
                    pending.Snapshot,
                    pending,
                    before,
                    stopwatch)
                .ConfigureAwait(false);

            if (result.Success)
            {
                await CompleteBestEffortAsync(
                        pending,
                        SnapshotRecord.OutcomeRecovered,
                        "启动时恢复了未完成的事务。",
                        setLast: false)
                    .ConfigureAwait(false);
                await LogAsync($"已恢复未完成事务 {pending.TransactionId}").ConfigureAwait(false);
                return result;
            }

            await LogAsync($"恢复未完成事务失败：{result.Message}").ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            await LogAsync($"恢复未完成事务时异常：{ex.Message}").ConfigureAwait(false);
            return ApplyResult.Fail(
                ApplyErrorCode.RollbackFailed,
                $"恢复未完成事务失败：{ex.Message}。快照保留在：{_store.PendingFile}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> RestoreLastAsync(CancellationToken ct = default)
    {
        var guard = CheckEnvironment();
        if (guard is not null)
        {
            return guard;
        }

        SnapshotRecord? last;
        try
        {
            last = await _store.ReadLastAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail(
                ApplyErrorCode.SnapshotFailed,
                $"上次配置快照无法读取：{ex.Message}");
        }

        if (last is null)
        {
            return ApplyResult.Fail(
                ApplyErrorCode.SnapshotFailed,
                "还没有可恢复的上次配置。");
        }

        return await RestoreAsync(last.AdapterKey, last.Snapshot, ApplyOptions.Default, ct)
            .ConfigureAwait(false);
    }

    private async Task<ApplyResult> RestoreAndVerifyAsync(
        AdapterKey key,
        AdapterInfo adapter,
        AdapterState target,
        PendingTransaction transaction,
        AdapterState? before,
        Stopwatch stopwatch)
    {
        try
        {
            await RestoreNetworkAsync(key, adapter, target).ConfigureAwait(false);

            var desired = DesiredFromSnapshot(target);
            var verification = await VerifyAsync(key, desired, CancellationToken.None)
                .ConfigureAwait(false);

            if (!verification.IsMatch)
            {
                var message =
                    $"恢复写入后校验未通过：{FormatIssues(verification)}。快照保留在：{_store.PendingFile}";
                await CompleteBestEffortAsync(
                        transaction,
                        SnapshotRecord.OutcomeRollbackFailed,
                        message,
                        setLast: false,
                        keepPending: true)
                    .ConfigureAwait(false);
                return ApplyResult.Fail(
                    ApplyErrorCode.VerificationFailed,
                    message,
                    before,
                    await SafeStateAsync(key).ConfigureAwait(false),
                    duration: stopwatch.Elapsed);
            }

            var after = await SafeStateAsync(key).ConfigureAwait(false);
            return ApplyResult.Ok(
                $"已恢复「{adapter.Key.FriendlyName}」的网络配置。",
                before,
                after,
                snapshotPath: null,
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            var message = $"恢复失败：{ex.Message}。快照保留在：{_store.PendingFile}";
            await LogAsync(message).ConfigureAwait(false);
            return ApplyResult.Fail(
                ApplyErrorCode.RollbackFailed,
                message,
                before,
                await SafeStateAsync(key).ConfigureAwait(false),
                duration: stopwatch.Elapsed);
        }
    }

    private async Task RunWriteAsync(
        AdapterKey key,
        AdapterInfo adapter,
        DesiredState desired,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Wi-Fi first: the preset usually joins a network before assigning addresses on it.
        await ApplyWirelessAsync(key, desired.Wireless, ct).ConfigureAwait(false);

        // IPv6 comes next: toggling the ms_tcpip6 binding resets the adapter, and the
        // IPv4 write should happen after it comes back.
        if (desired.Ipv6 is { Mode: not Ipv6Mode.Keep })
        {
            await _ipv6.ApplyAsync(adapter, desired.Ipv6, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        // The WMI calls themselves are synchronous and cannot be aborted safely. The token
        // only prevents starting a write after cancellation.
        await Task.Run(
                () =>
                {
                    using var wmi = WmiNetworkAdapter.Open(key);
                    wmi.ConfigureIpv4(desired.Ipv4);
                    if (desired.Ipv4.MetricMode != MetricMode.Keep)
                    {
                        ApplyMetric(
                            adapter,
                            desired.Ipv4.MetricMode == MetricMode.Automatic,
                            desired.Ipv4.InterfaceMetric ?? 1,
                            NativeConstants.AF_INET);
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task RestoreNetworkAsync(AdapterKey key, AdapterInfo adapter, AdapterState snapshot)
    {
        await RestoreWirelessAsync(key, snapshot.Wireless).ConfigureAwait(false);

        await _ipv6.ApplyAsync(adapter, Ipv6FromSnapshot(snapshot.Ipv6), CancellationToken.None)
            .ConfigureAwait(false);

        await Task.Run(
                () =>
                {
                    using var wmi = WmiNetworkAdapter.Open(key);
                    wmi.RestoreIpv4(snapshot.Ipv4);
                    ApplyMetric(
                        adapter,
                        snapshot.Ipv4.AutomaticMetric,
                        snapshot.Ipv4.InterfaceMetric,
                        NativeConstants.AF_INET);
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static void ApplyMetric(AdapterInfo adapter, bool automatic, int metric, ushort family)
    {
        var ok = IpHelperApi.SetInterfaceMetric(
            family,
            adapter.Luid,
            adapter.InterfaceIndex,
            automatic,
            metric,
            out var error);
        if (!ok)
        {
            throw new WmiOperationException($"设置接口跃点失败：{error}");
        }
    }

    private async Task<(string Message, bool Restored)> RollbackAfterFailureAsync(
        AdapterKey key,
        AdapterInfo adapter,
        AdapterState before,
        PendingTransaction transaction,
        string reason)
    {
        var rollback = await RestoreSnapshotAsync(key, adapter, before, CancellationToken.None)
            .ConfigureAwait(false);
        var outcome = rollback.Success
            ? SnapshotRecord.OutcomeRolledBack
            : SnapshotRecord.OutcomeRollbackFailed;
        var message =
            $"{reason}" +
            (rollback.Success
                ? " 已恢复到应用前的配置。"
                : $" 自动恢复失败：{rollback.Message}");

        await CompleteBestEffortAsync(
                transaction,
                outcome,
                message,
                setLast: false,
                keepPending: !rollback.Success)
            .ConfigureAwait(false);
        await LogAsync(message).ConfigureAwait(false);

        return (message, rollback.Success);
    }

    private async Task<ApplyResult> RestoreSnapshotAsync(
        AdapterKey key,
        AdapterInfo adapter,
        AdapterState snapshot,
        CancellationToken ct)
    {
        try
        {
            await RestoreNetworkAsync(key, adapter, snapshot).ConfigureAwait(false);

            var verification = await VerifyAsync(key, DesiredFromSnapshot(snapshot), CancellationToken.None)
                .ConfigureAwait(false);
            var after = await SafeStateAsync(key).ConfigureAwait(false);
            if (!verification.IsMatch)
            {
                return ApplyResult.Fail(
                    ApplyErrorCode.RollbackFailed,
                    $"恢复到应用前状态后校验未通过：{FormatIssues(verification)}",
                    after: after);
            }

            return ApplyResult.Ok("已恢复。", null, after, null, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            var after = await SafeStateAsync(key).ConfigureAwait(false);
            return ApplyResult.Fail(ApplyErrorCode.RollbackFailed, ex.Message, after: after);
        }
    }

    private async Task<VerificationResult> VerifyAsync(
        AdapterKey key,
        DesiredState desired,
        CancellationToken ct)
    {
        var timeout = desired.Ipv6 is { Mode: not Ipv6Mode.Keep }
            ? TimeSpan.FromSeconds(30)
            : VerificationTimeout;
        var deadline = DateTime.UtcNow + timeout;
        VerificationResult last = new(new[]
        {
            new VerificationIssue("适配器状态", "可读取", "无法读取")
        });

        while (true)
        {
            var state = await _catalog.GetStateAsync(key, ct).ConfigureAwait(false);
            if (state is not null)
            {
                last = DesiredStateVerifier.Verify(desired, state);
                if (last.IsMatch)
                {
                    return last;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return last;
            }

            await Task.Delay(VerificationPollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task<ApplyResult?> PendingErrorAsync(CancellationToken ct)
    {
        try
        {
            var pending = await _store.ReadPendingAsync(ct).ConfigureAwait(false);
            if (pending is null)
            {
                return File.Exists(_store.PendingFile)
                    ? ApplyResult.Fail(
                        ApplyErrorCode.PendingRecoveryRequired,
                        $"存在空的未完成事务快照文件，请先处理：{_store.PendingFile}")
                    : null;
            }

            return ApplyResult.Fail(
                ApplyErrorCode.PendingRecoveryRequired,
                $"检测到未完成的事务（{pending.AdapterName}，{pending.StartedAt:yyyy-MM-dd HH:mm}）。" +
                "请先在主界面点击“恢复未完成事务”，再进行新的操作。");
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail(
                ApplyErrorCode.PendingRecoveryRequired,
                $"存在未完成的事务快照，但读取失败：{ex.Message}。快照文件：{_store.PendingFile}");
        }
    }

    private async Task CompleteBestEffortAsync(
        PendingTransaction transaction,
        string outcome,
        string? message,
        bool setLast,
        bool keepPending = false)
    {
        try
        {
            await _store.CompleteAsync(transaction, outcome, message, setLast, keepPending)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"写入事务历史失败：{ex.Message}").ConfigureAwait(false);
            if (!keepPending)
            {
                _store.DeletePending();
            }
        }
    }

    private async Task<AdapterState?> SafeStateAsync(AdapterKey key)
    {
        try
        {
            return await _catalog.GetStateAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"读取适配器状态失败：{ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private async Task LogAsync(string message)
    {
        try
        {
            await _log.WriteAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // Logging must never break a network operation.
        }
    }

    private ApplyResult? CheckEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ApplyResult.Fail(
                ApplyErrorCode.NotSupported,
                "Windows 网络配置后端只能在 Windows 上运行。");
        }

        if (!_privileges.IsElevated)
        {
            return ApplyResult.Fail(
                ApplyErrorCode.NotElevated,
                "修改网络配置需要管理员权限。请以管理员身份重新启动 NetSwitch。");
        }

        return null;
    }

    private string? UnsupportedReason(DesiredState desired)
    {
        if (desired.Ipv6 is not null && desired.Ipv6.Mode != Ipv6Mode.Keep)
        {
            if (!_capabilities.SupportsIpv6BindingToggle)
            {
                return "当前平台不支持 IPv6 配置，请把预设的 IPv6 设为“保持当前”。";
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                return "IPv6 配置需要 Windows 10 版本 2004（build 19041）或更高版本。";
            }
        }

        if (desired.Wireless is not null &&
            desired.Wireless.Action != WirelessAction.Keep &&
            !_capabilities.SupportsWirelessProfiles)
        {
            return "当前版本尚未实现 Wi-Fi 连接切换，请把 Wi-Fi 动作设为“保持当前”。";
        }

        return null;
    }

    private static string? InvalidReason(DesiredState desired)
    {
        var ipv4 = desired.Ipv4;
        if (ipv4.Mode == Ipv4Mode.Static)
        {
            if (ipv4.Addresses.Count == 0)
            {
                return "静态 IPv4 至少需要一个地址。";
            }

            foreach (var address in ipv4.Addresses)
            {
                if (!System.Net.IPAddress.TryParse(address.Address, out var parsed) ||
                    parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    address.PrefixLength is < 0 or > 32)
                {
                    return $"IPv4 地址无效：{address.Address}/{address.PrefixLength}";
                }
            }

            foreach (var gateway in ipv4.Gateways)
            {
                if (!System.Net.IPAddress.TryParse(gateway.Address, out var parsed) ||
                    parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return $"默认网关无效：{gateway.Address}";
                }
            }
        }

        if (ipv4.DnsMode == DnsMode.Static)
        {
            if (ipv4.DnsServers.Count == 0)
            {
                return "静态 DNS 模式至少需要一个 DNS 服务器。";
            }

            foreach (var server in ipv4.DnsServers)
            {
                if (!System.Net.IPAddress.TryParse(server, out _))
                {
                    return $"DNS 服务器无效：{server}";
                }
            }
        }

        if (ipv4.MetricMode == MetricMode.Manual &&
            ipv4.InterfaceMetric is not (>= 0 and <= 9999))
        {
            return "接口跃点必须在 0 到 9999 之间。";
        }

        var ipv6 = desired.Ipv6;
        if (ipv6 is not null && ipv6.Mode != Ipv6Mode.Keep)
        {
            if (ipv6.Mode == Ipv6Mode.Static)
            {
                if (ipv6.Addresses.Count == 0)
                {
                    return "静态 IPv6 至少需要一个地址。";
                }

                foreach (var address in ipv6.Addresses)
                {
                    if (!System.Net.IPAddress.TryParse(address.Address, out var parsed) ||
                        parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                        address.PrefixLength is < 0 or > 128)
                    {
                        return $"IPv6 地址无效：{address.Address}/{address.PrefixLength}";
                    }
                }

                foreach (var gateway in ipv6.Gateways)
                {
                    if (!System.Net.IPAddress.TryParse(gateway.Address, out var parsed) ||
                        parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        return $"IPv6 默认网关无效：{gateway.Address}";
                    }
                }
            }

            if (ipv6.DnsMode == DnsMode.Static)
            {
                if (ipv6.DnsServers.Count == 0)
                {
                    return "静态 IPv6 DNS 模式至少需要一个 DNS 服务器。";
                }

                foreach (var server in ipv6.DnsServers)
                {
                    if (!System.Net.IPAddress.TryParse(server, out var parsed) ||
                        parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        return $"IPv6 DNS 服务器必须是 IPv6 地址：{server}";
                    }
                }
            }

            if (ipv6.MetricMode == MetricMode.Manual &&
                ipv6.InterfaceMetric is not (>= 0 and <= 9999))
            {
                return "IPv6 接口跃点必须在 0 到 9999 之间。";
            }
        }

        if (desired.Wireless is { Action: WirelessAction.Connect } &&
            string.IsNullOrWhiteSpace(desired.Wireless.SavedProfileName))
        {
            return "连接 Wi-Fi 必须指定一个已保存的 Profile。";
        }

        return null;
    }

    private async Task ApplyWirelessAsync(
        AdapterKey key,
        WirelessDesired? desired,
        CancellationToken ct)
    {
        if (desired is null || desired.Action == WirelessAction.Keep)
        {
            return;
        }

        if (desired.Action == WirelessAction.Connect)
        {
            if (string.IsNullOrWhiteSpace(desired.SavedProfileName))
            {
                throw new InvalidOperationException("连接 Wi-Fi 需要指定一个已保存的 Profile。");
            }

            await _wireless.ConnectAsync(key, desired.SavedProfileName, ct).ConfigureAwait(false);
            return;
        }

        var current = await _wireless.GetStateAsync(key, ct).ConfigureAwait(false);
        if (current is { IsConnected: true })
        {
            await _wireless.DisconnectAsync(key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores the Wi-Fi side of a snapshot. It first compares the current connection so
    /// an operation that never touched Wi-Fi does not cause a needless reconnect.
    /// </summary>
    private async Task RestoreWirelessAsync(AdapterKey key, WirelessState? target)
    {
        if (target is null)
        {
            return;
        }

        var current = await _wireless.GetStateAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (target.IsConnected)
        {
            var profile = target.ActiveProfileName ?? target.Ssid;
            if (string.IsNullOrWhiteSpace(profile))
            {
                return;
            }

            if (current is { IsConnected: true } &&
                (string.Equals(current.ActiveProfileName, profile, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(current.Ssid, profile, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            await _wireless.ConnectAsync(key, profile, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (current is { IsConnected: true })
        {
            await _wireless.DisconnectAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static WirelessDesired WirelessFromSnapshot(WirelessState? state)
    {
        if (state is null)
        {
            return WirelessDesired.Keep();
        }

        if (!state.IsConnected)
        {
            return new WirelessDesired(WirelessAction.Disconnect);
        }

        return string.IsNullOrWhiteSpace(state.ActiveProfileName)
            ? WirelessDesired.Keep()
            : new WirelessDesired(WirelessAction.Connect, state.ActiveProfileName);
    }

    private static DesiredState DesiredFromSnapshot(AdapterState snapshot) => new(
        new Ipv4Desired(
            snapshot.Ipv4.DhcpEnabled ? Ipv4Mode.Dhcp : Ipv4Mode.Static,
            snapshot.Ipv4.Addresses,
            snapshot.Ipv4.Gateways,
            snapshot.Ipv4.DnsMode,
            snapshot.Ipv4.DnsServers,
            snapshot.Ipv4.AutomaticMetric ? MetricMode.Automatic : MetricMode.Manual,
            snapshot.Ipv4.AutomaticMetric ? null : snapshot.Ipv4.InterfaceMetric),
        Ipv6FromSnapshot(snapshot.Ipv6),
        WirelessFromSnapshot(snapshot.Wireless));

    private static Ipv6Desired Ipv6FromSnapshot(Ipv6State state)
    {
        if (!state.BindingEnabled)
        {
            return Ipv6Desired.Disabled();
        }

        var dnsMode = state.DnsMode;
        var dnsServers = dnsMode == DnsMode.Static
            ? state.DnsServers
            : Array.Empty<string>();
        var metricMode = state.AutomaticMetric ? MetricMode.Automatic : MetricMode.Manual;
        int? metric = state.AutomaticMetric ? null : state.InterfaceMetric;

        if (state.Mode == Ipv6Mode.Static)
        {
            var addresses = state.Addresses
                .Where(address => Ipv6AddressUtil.IsGlobalUnicast(address.Address))
                .ToArray();

            if (addresses.Length > 0)
            {
                return new Ipv6Desired(
                    Ipv6Mode.Static,
                    addresses,
                    state.Gateways,
                    dnsMode,
                    dnsServers,
                    metricMode,
                    metric);
            }
        }

        return new Ipv6Desired(
            Ipv6Mode.Auto,
            Array.Empty<Ipv4Address>(),
            Array.Empty<Gateway>(),
            dnsMode,
            dnsServers,
            metricMode,
            metric);
    }

    private static string DescribeScope(DesiredState desired)
    {
        var scopes = new List<string> { "IPv4" };
        if (desired.Ipv6 is { Mode: not Ipv6Mode.Keep })
        {
            scopes.Add("IPv6");
        }

        if (desired.Wireless is { Action: not WirelessAction.Keep })
        {
            scopes.Add("Wi-Fi");
        }

        return string.Join(" / ", scopes);
    }

    private static string FormatIssues(VerificationResult result)
    {
        var issues = result.Issues
            .Take(4)
            .Select(issue => $"{issue.Field}（期望 {issue.Expected}，实际 {issue.Actual}）")
            .ToArray();
        return issues.Length == 0 ? "未知差异" : string.Join("；", issues);
    }
}
