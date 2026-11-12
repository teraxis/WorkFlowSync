using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.Logging;

namespace WorkFlowSync.App.Views;

public partial class MainWindow : Window
{
    private bool _repairRunning;
    public MainWindow()
    {
        InitializeComponent();
        // Keep the run log scrolled to the newest line.
        RunLog.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && RunLog.Text is { Length: > 0 } t)
                RunLog.CaretIndex = t.Length;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    /// <summary>The pair a row button belongs to (row buttons carry the pair as their DataContext).</summary>
    private static PairViewModel? PairOf(object? sender) => (sender as Control)?.DataContext as PairViewModel;

    private async void OnAddPair(object? sender, RoutedEventArgs e)
    {
        var draft = new PairViewModel();
        var dialog = new PairDialog(draft, Vm);
        if (await dialog.ShowDialog<bool>(this))
            Vm.AddPair(draft);
    }

    private async void OnEditPair(object? sender, RoutedEventArgs e)
    {
        var pair = PairOf(sender) ?? Vm.SelectedPair;
        if (pair is null) return;
        var draft = pair.Clone();
        var dialog = new PairDialog(draft, Vm, editing: pair);
        if (await dialog.ShowDialog<bool>(this))
            Vm.ReplacePair(pair, draft);
    }

    private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        OnEditPair(sender, e);
    }

    private void OnOpenFolderPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is string path && !string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (System.IO.Directory.Exists(path) || System.IO.File.Exists(path))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                }
                else
                {
                    Vm.StatusText = $"Тека не існує: {path}";
                    Vm.StatusIsError = true;
                }
            }
            catch (Exception ex)
            {
                Vm.StatusText = $"Не вдалося відкрити теку: {ex.Message}";
                Vm.StatusIsError = true;
            }
            e.Handled = true;
        }
    }

    private async void OnDeletePair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        var ok = await ConfirmDialog.ShowAsync(this,
            "Видалити завдання?",
            $"Завдання «{pair.Name}» зникне зі списку. Уже скопійовані файли залишаться на місці, теки не змінюються.",
            "Видалити");
        if (ok) Vm.RemovePair(pair);
    }

    private async void OnRunPair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        await Vm.RunPairAsync(pair);
    }

    private async void OnRepairPair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        if (_repairRunning)
        {
            Vm.StatusText = I18n.T("repair.busy");
            Vm.StatusIsError = true;
            return;
        }
        var wasEnabled = pair.Enabled;
        var options = await TroubleshootingOptionsWindow.ShowAsync(this,
            pair.Mode == WorkFlowSync.Core.Config.SyncMode.Mirror,
            pair.ShortenLongPaths, pair.RepairRequired, wasEnabled);
        if (options is null) return;
        if (options.AuditMissing && pair.Mode != WorkFlowSync.Core.Config.SyncMode.Mirror)
        {
            Vm.StatusText = I18n.T("troubleshoot.requires_mirror");
            Vm.StatusIsError = true;
            return;
        }
        _repairRunning = true;
        RepairProgressWindow? progressWindow = null;
        FileSyncLog? log = null;
        var pausedByUs = false;
        try
        {
            var configPath = Vm.ConfigPath;
            var pairName = pair.Name;
            var statePath = Vm.StateFullPath;
            var logPath = Vm.LogFullPath;
            Action<string> appendLog = Vm.Run.AppendLog;
            log = new FileSyncLog(logPath, (_, line) => appendLog(line), keepDays: Vm.LogKeepDays);
            log.Info($"[{pair.Name}] troubleshoot start was_enabled={wasEnabled} paths={options.RepairPaths} audit={options.AuditMissing} restore={options.RestoreMissing}");
            if (wasEnabled)
            {
                pair.Enabled = false;
                if (!Vm.SaveNow()) throw new IOException("Could not pause task before troubleshooting");
                pausedByUs = true;
            }
            Vm.StatusText = I18n.T("troubleshoot.waiting");
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            var available = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                available = await Task.Run(() =>
                {
                    using var gate = PassLock.TryAcquire(configPath, pairName);
                    return gate.Acquired;
                });
                if (available) break;
                await Task.Delay(500);
            }
            if (!available) throw new IOException("Timed out waiting for current task pass to stop");
            pair.IsBusy = true;
            var model = pair.ToModel();
            var config = Vm.ToConfig();
            var runner = new RepairRunner(model, config, configPath, statePath, log);

            if (options.RepairPaths)
            {
                progressWindow = new RepairProgressWindow();
                var scanWindow = progressWindow;
                var scanProgress = new Progress<RepairProgress>(p => Dispatcher.UIThread.Post(() => scanWindow.Update(p)));
                progressWindow.Show(this);
                using var plan = await Task.Run(() => runner.Prepare(scanProgress));
                progressWindow.Close();
                progressWindow = null;
                Vm.StatusText = I18n.T("repair.preview_ready", plan.ActionCount, plan.Conflicts);
                Vm.StatusIsError = false;
                if (plan.ActionCount > 0 && await RepairPreviewWindow.ShowAsync(this, plan))
                {
                    progressWindow = new RepairProgressWindow();
                    var applyWindow = progressWindow;
                    var applyProgress = new Progress<RepairProgress>(p => Dispatcher.UIThread.Post(() => applyWindow.Update(p)));
                    progressWindow.Show(this);
                    await Task.Run(() => runner.Apply(plan, applyProgress));
                    progressWindow.Close();
                    progressWindow = null;
                    pair.RepairRequired = plan.Conflicts > 0;
                }
            }
            var restored = 0;
            var errors = 0;
            var skipped = 0;
            if (options.AuditMissing)
            {
                progressWindow = new RepairProgressWindow();
                var auditWindow = progressWindow;
                var auditProgress = new Progress<RepairProgress>(p => Dispatcher.UIThread.Post(() => auditWindow.Update(p)));
                progressWindow.Show(this);
                var audit = await Task.Run(() => runner.PrepareMissing(auditProgress));
                progressWindow.Close();
                progressWindow = null;
                var decision = await RecoveryPreviewWindow.ShowAsync(this, audit, options.RestoreMissing);
                if (decision?.Apply == true)
                {
                    progressWindow = new RepairProgressWindow();
                    var restoreWindow = progressWindow;
                    var restoreProgress = new Progress<RepairProgress>(p => Dispatcher.UIThread.Post(() => restoreWindow.Update(p)));
                    progressWindow.Show(this);
                    var result = await Task.Run(() => runner.ApplyMissing(audit, decision.RestoreDeleted, restoreProgress));
                    restored = result.FilesCopied;
                    errors = result.Errors;
                    skipped = result.Skipped;
                    progressWindow.Close();
                    progressWindow = null;
                }
            }
            Vm.StatusText = I18n.T("troubleshoot.completed", restored, errors, skipped);
            Vm.StatusIsError = errors > 0 || skipped > 0;
        }
        catch (Exception ex)
        {
            log?.Error($"repair ERROR {ex}");
            Vm.StatusText = I18n.T("repair.failed_log");
            Vm.StatusIsError = true;
        }
        finally
        {
            progressWindow?.Close();
            log?.Dispose();
            pair.IsBusy = false;
            if (pausedByUs || (wasEnabled && !pair.Enabled))
            {
                pair.Enabled = true;
                if (!Vm.SaveNow())
                {
                    Vm.StatusText = I18n.T("troubleshoot.resume_failed");
                    Vm.StatusIsError = true;
                }
            }
            _repairRunning = false;
        }
    }

    private async void OnTogglePair(object? sender, RoutedEventArgs e)
    {
        if (PairOf(sender) is not { } pair) return;
        await Vm.TogglePairAsync(pair);
    }

    private void OnViewDetails(object? sender, RoutedEventArgs e)
    {
        Vm.SelectedPage = 1;
    }

    private void OnOpenApproval(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShowApprovalWindow();
        }
    }

    private void OnOpenAppFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Vm.AppFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Vm.StatusText = $"Не вдалося відкрити теку: {ex.Message}";
            Vm.StatusIsError = true;
        }
    }

    private async void OnAddAccount(object? sender, RoutedEventArgs e)
    {
        var existingClientId = Vm.GoogleAuth?.ClientId ?? Vm.Accounts.FirstOrDefault(a => a.Provider == Core.Config.CloudProvider.GoogleDrive)?.Model.ClientId;
        var existingClientSecret = Vm.GoogleAuth?.ClientSecret ?? Vm.Accounts.FirstOrDefault(a => a.Provider == Core.Config.CloudProvider.GoogleDrive)?.Model.ClientSecret;

        var account = await AccountWizardDialog.ShowAsync(this, existingClientId, existingClientSecret);
        if (account is not null)
        {
            Vm.AddAccount(account);
        }
    }

    private static AccountItemViewModel? AccountOf(object? sender) => (sender as Control)?.DataContext as AccountItemViewModel;

    private async void OnTestAccount(object? sender, RoutedEventArgs e)
    {
        if (AccountOf(sender) is not { } account) return;
        if (account.TestConnectionCommand.CanExecute(null))
        {
            await account.TestConnectionCommand.ExecuteAsync(null);
        }
    }

    private async void OnDeleteAccount(object? sender, RoutedEventArgs e)
    {
        if (AccountOf(sender) is not { } account) return;
        var ok = await ConfirmDialog.ShowAsync(this,
            "Видалити обліковий запис?",
            $"Обліковий запис «{account.Name}» ({account.Email}) буде відключено від програми.",
            "Видалити");
        if (ok) account.RemoveCommand.Execute(null);
    }

    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            await app.CheckForUpdatesManualAsync();
        }
    }
}
