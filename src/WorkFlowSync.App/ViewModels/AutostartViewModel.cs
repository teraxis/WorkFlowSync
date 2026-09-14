using CommunityToolkit.Mvvm.ComponentModel;
using WorkFlowSync.Core;

namespace WorkFlowSync.App.ViewModels;

/// <summary>
/// «Налаштування» → автозапуск. Both switches take effect immediately (they are not part of config.json):
/// the Startup shortcut starts `wfs.exe sync --loop` at logon; the scheduled task runs `wfs.exe sync --once` every N minutes.
/// </summary>
public sealed partial class AutostartViewModel : ObservableObject
{
    private readonly Func<string> _configPath;
    private readonly Func<int> _intervalMinutes;
    private bool _suppress;

    [ObservableProperty] private bool _startupEnabled;
    [ObservableProperty] private bool _taskEnabled;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageIsError;

    public string StartupHint => $"Ярлик у папці автозавантаження: {Autostart.ShortcutPath()}";
    public string TaskHint => $"Завдання «{Autostart.TaskName}» у Планувальнику: від вашого імені, лише коли ви увійшли, без прав адміністратора.";
    public bool ConsoleAvailable => File.Exists(Autostart.ConsoleExePath);

    public AutostartViewModel(Func<string> configPath, Func<int> intervalMinutes)
    {
        _configPath = configPath;
        _intervalMinutes = intervalMinutes;
        Refresh();
    }

    public void Refresh()
    {
        _suppress = true;
        try
        {
            StartupEnabled = Autostart.IsStartupShortcutEnabled();
            TaskEnabled = Autostart.IsScheduledTaskEnabled();
        }
        finally
        {
            _suppress = false;
        }
        if (!ConsoleAvailable)
            Set("Поруч із програмою немає wfs.exe — автозапуск недоступний (див. publish\\portable).", error: true);
    }

    partial void OnStartupEnabledChanged(bool value)
    {
        if (_suppress) return;
        Apply(() =>
        {
            if (value) Autostart.EnableStartupShortcut(_configPath());
            else Autostart.DisableStartupShortcut();
            return value ? "Резидентний режим увімкнено: запуститься при наступному вході в Windows." : "Автозапуск вимкнено.";
        }, revert: () => StartupEnabled = !value);
    }

    partial void OnTaskEnabledChanged(bool value)
    {
        if (_suppress) return;
        Apply(() =>
        {
            if (value) Autostart.EnableScheduledTask(_configPath(), _intervalMinutes());
            else Autostart.DisableScheduledTask();
            return value ? $"Завдання створено: кожні {_intervalMinutes()} хв." : "Завдання видалено.";
        }, revert: () => TaskEnabled = !value);
    }

    private void Apply(Func<string> action, Action revert)
    {
        try
        {
            Set(action(), error: false);
        }
        catch (Exception ex)
        {
            Set($"Не вдалося: {ex.Message}", error: true);
            _suppress = true;
            try { revert(); } finally { _suppress = false; }
        }
    }

    private void Set(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }
}
