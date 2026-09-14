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
    [ObservableProperty] private bool _startupInTray = true;
    [ObservableProperty] private bool _taskEnabled;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageIsError;

    public string StartupHint => $"Ярлик у папці автозавантаження: {Autostart.ShortcutPath()}";
    public bool GuiAvailable => File.Exists(Autostart.GuiExePath);
    public string TaskHint => $"Завдання «{Autostart.TaskName}» у Планувальнику: від вашого імені, лише коли ви увійшли, без прав адміністратора.";
    public bool ConsoleAvailable => File.Exists(Autostart.ConsoleExePath);

    /// <summary>The Startup shortcut needs the GUI (tray mode) or the console (windowless mode).</summary>
    public bool CanAutostart => GuiAvailable || ConsoleAvailable;

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
            var mode = Autostart.ReadStartupShortcutMode();
            StartupEnabled = mode is not null;
            if (mode is { } m) StartupInTray = m == Autostart.Mode.Tray;
            TaskEnabled = Autostart.IsScheduledTaskEnabled();
        }
        finally
        {
            _suppress = false;
        }
        if (!CanAutostart)
            Set("Поруч із програмою немає ані WorkFlowSync.exe, ані wfs.exe — автозапуск недоступний.", error: true);
        else if (!ConsoleAvailable)
            Set("Поруч немає wfs.exe, тож доступний лише режим «в області сповіщень» і недоступне завдання Планувальника (візьміть збірку з publish\\portable).", error: false);
    }

    partial void OnStartupEnabledChanged(bool value)
    {
        if (_suppress) return;
        Apply(() =>
        {
            if (value) Autostart.EnableStartupShortcut(_configPath(), mode: StartupInTray && GuiAvailable ? Autostart.Mode.Tray : Autostart.Mode.ConsoleLoop);
            else Autostart.DisableStartupShortcut();
            return value
                ? (StartupInTray ? "Увімкнено: при вході в Windows програма з'явиться в області сповіщень і працюватиме сама."
                                 : "Увімкнено: при вході в Windows запускатиметься фоновий процес без вікна.")
                : "Автозапуск вимкнено.";
        }, revert: () => StartupEnabled = !value);
    }

    partial void OnStartupInTrayChanged(bool value)
    {
        if (_suppress || !StartupEnabled) return;
        Apply(() =>
        {
            Autostart.EnableStartupShortcut(_configPath(), mode: value ? Autostart.Mode.Tray : Autostart.Mode.ConsoleLoop);
            return value ? "Спосіб запуску змінено: в області сповіщень." : "Спосіб запуску змінено: фоновий процес без вікна.";
        }, revert: () => StartupInTray = !value);
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
