using Avalonia.Controls;
using NetSwitch.Core.Models;

namespace NetSwitch.App.Services;

public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error
}

public interface IDialogService
{
    Window? Owner { get; set; }

    /// <summary>
    /// Opens the preset editor. <paramref name="isNew"/> tells the editor that the supplied
    /// preset is only a template (current state or a copied preset) and must be saved as new.
    /// </summary>
    Task<Preset?> EditPresetAsync(
        AdapterInfo adapter,
        Preset? existing,
        IReadOnlyList<string> wifiProfiles,
        bool isNew = false);

    Task ShowTestAsync(
        AdapterInfo adapter,
        string presetName,
        DesiredState desired);

    Task<bool> ConfirmApplyAsync(
        AdapterInfo adapter,
        Preset preset,
        AdapterState? state,
        bool isOnlyConnectedAdapter = false);

    /// <summary>Shows the auto-revert countdown. Returns true when the configuration should be reverted.</summary>
    Task<bool> ShowAutoRevertAsync(
        string presetName,
        int seconds,
        string? gatewayAddress = null);

    Task<bool> ShowSettingsAsync();

    Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info);

    Task<string?> PickExportFileAsync(string suggestedName);

    Task<string?> PickImportFileAsync();

    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText,
        bool dangerous = false);
}
