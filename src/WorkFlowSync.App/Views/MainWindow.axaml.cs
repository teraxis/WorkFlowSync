using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.App.Views;

public partial class MainWindow : Window
{
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
        if (pair.Enabled || !pair.ShortenLongPaths)
        {
            Vm.StatusText = I18n.T("repair.pause_first");
            Vm.StatusIsError = true;
            return;
        }
        try
        {
            var model = pair.ToModel();
            var config = Vm.ToConfig();
            using var plan = await Task.Run(() =>
            {
                using var gate = PassLock.TryAcquire(Vm.ConfigPath, model.Name);
                if (!gate.Acquired) throw new IOException(I18n.T("repair.busy"));
                using var store = new StateStore(Vm.StateFullPath);
                return TargetRepair.Prepare(model, config, store);
            });
            Vm.StatusText = I18n.T("repair.preview_ready", plan.ActionCount, plan.Conflicts);
            Vm.StatusIsError = false;
            if (plan.ActionCount == 0) return;
            var approved = await ConfirmDialog.ShowAsync(this, I18n.T("tasks.repair_target"),
                I18n.T("repair.confirm", plan.ActionCount, plan.Conflicts),
                I18n.T("repair.apply"));
            if (!approved) return;
            await Task.Run(() =>
            {
                using var gate = PassLock.TryAcquire(Vm.ConfigPath, model.Name);
                if (!gate.Acquired) throw new IOException(I18n.T("repair.busy"));
                using var store = new StateStore(Vm.StateFullPath);
                TargetRepair.Execute(plan, store);
                if (plan.Conflicts == 0) store.SetMeta($"repair_required:{model.Name}", "0");
            });
            Vm.StatusText = I18n.T("repair.completed", plan.ActionCount, plan.Conflicts);
            Vm.StatusIsError = false;
        }
        catch (Exception ex)
        {
            Vm.StatusText = I18n.T("repair.failed", ex.Message);
            Vm.StatusIsError = true;
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
