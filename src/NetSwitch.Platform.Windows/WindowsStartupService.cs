using System.Runtime.InteropServices;
using System.Security.Principal;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.Platform.Windows;

/// <summary>
/// Autostart through the Task Scheduler COM API (Schedule.Service). A logon task with
/// <c>RunLevel = HighestAvailable</c> starts the elevated app without the UAC prompt that a
/// Run-key entry would cause. No external process (schtasks) is used.
/// </summary>
public sealed class WindowsStartupService : IStartupService
{
    private const string TaskName = "NetSwitch";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const int TaskInstancesIgnoreNew = 3;

    public bool IsSupported => OperatingSystem.IsWindows();

    public Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            EnsureWindows();
            object? service = null;
            object? root = null;
            object? task = null;
            try
            {
                service = CreateService();
                root = GetRootFolder(service);
                task = Invoke(root, "GetTask", TaskName);
                return Get(task, "Enabled") is bool enabled && enabled;
            }
            catch (System.Reflection.TargetInvocationException)
            {
                return false;
            }
            catch (COMException)
            {
                return false;
            }
            finally
            {
                Release(task);
                Release(root);
                Release(service);
            }
        }, ct);

    public Task<StartupChangeResult> SetEnabledAsync(
        bool enabled,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            EnsureWindows();
            object? service = null;
            object? root = null;
            try
            {
                service = CreateService();
                root = GetRootFolder(service);

                if (!enabled)
                {
                    try
                    {
                        Invoke(root, "DeleteTask", TaskName, 0);
                    }
                    catch (System.Reflection.TargetInvocationException ex) when (
                        ex.InnerException is COMException com && com.HResult == unchecked((int)0x80070002))
                    {
                        // Task did not exist; disabling is still successful.
                    }

                    return new StartupChangeResult(true, "已关闭开机自启。");
                }

                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    return new StartupChangeResult(false, "无法确定程序路径，不能注册开机自启。");
                }

                var definition = Invoke(service, "NewTask", 0);
                var registration = Get(definition, "RegistrationInfo");
                Set(registration, "Description", "NetSwitch 网络配置切换器");
                Set(registration, "Author", "NetSwitch");

                var settings = Get(definition, "Settings");
                Set(settings, "StartWhenAvailable", true);
                Set(settings, "DisallowStartIfOnBatteries", false);
                Set(settings, "StopIfGoingOnBatteries", false);
                Set(settings, "ExecutionTimeLimit", "PT0S");
                Set(settings, "MultipleInstances", TaskInstancesIgnoreNew);

                var user = WindowsIdentity.GetCurrent().Name;
                var principal = Get(definition, "Principal");
                Set(principal, "UserId", user);
                Set(principal, "LogonType", TaskLogonInteractiveToken);
                Set(principal, "RunLevel", TaskRunLevelHighest);

                var triggers = Get(definition, "Triggers");
                var trigger = Invoke(triggers, "Create", TaskTriggerLogon);
                Set(trigger, "Enabled", true);
                Set(trigger, "UserId", user);

                var actions = Get(definition, "Actions");
                var action = Invoke(actions, "Create", TaskActionExec);
                Set(action, "Path", exe);
                var directory = Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Set(action, "WorkingDirectory", directory);
                }

                Invoke(root, "RegisterTaskDefinition",
                    TaskName,
                    definition,
                    TaskCreateOrUpdate,
                    user,
                    null,
                    TaskLogonInteractiveToken,
                    null);

                return new StartupChangeResult(true, "已启用开机自启（登录时以管理员权限启动）。");
            }
            catch (Exception ex)
            {
                return new StartupChangeResult(
                    false,
                    $"设置开机自启失败：{Unwrap(ex).Message}");
            }
            finally
            {
                Release(root);
                Release(service);
            }
        }, ct);

    private static object CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new PlatformNotSupportedException("任务计划程序 COM 组件不可用。");
        var service = Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("无法创建任务计划程序服务。");
        Invoke(service, "Connect");
        return service;
    }

    private static object GetRootFolder(object service) =>
        Invoke(service, "GetFolder", "\\");

    private static object Get(object target, string property) =>
        target.GetType().InvokeMember(
            property,
            System.Reflection.BindingFlags.GetProperty,
            null,
            target,
            null)!;

    private static void Set(object target, string property, object? value) =>
        target.GetType().InvokeMember(
            property,
            System.Reflection.BindingFlags.SetProperty,
            null,
            target,
            new[] { value });

    private static object Invoke(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(
            method,
            System.Reflection.BindingFlags.InvokeMethod,
            null,
            target,
            args)!;

    private static Exception Unwrap(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
            ? Unwrap(inner)
            : ex;

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("开机自启仅支持 Windows。");
        }
    }
}
