using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.OAuth;

namespace WorkFlowSync.App.ViewModels;

public sealed class CloudProviderOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconKey { get; init; } = "";
    public bool IsAvailable { get; init; }
    public string BadgeText { get; init; } = "";
}

public sealed partial class AccountWizardViewModel : ObservableObject
{
    private readonly IGoogleOAuthService _oauthService;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private int _step = 0; // 0: Select Provider, 1: Credentials, 2: Waiting, 3: Success, 4: Error

    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _clientSecret = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _errorMessage = "";

    [ObservableProperty] private string _authorizedEmail = "";
    [ObservableProperty] private string _authorizedName = "";

    public CloudAccount? ResultAccount { get; private set; }

    public bool CanStart => !string.IsNullOrWhiteSpace(ClientId) && !IsBusy;

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;

    public IReadOnlyList<CloudProviderOption> Providers { get; } = new List<CloudProviderOption>
    {
        new() { Id = "GoogleDrive", Name = "Google Drive", Description = "Google Docs, Sheets, Drive API", IconKey = "IconGoogleDrive", IsAvailable = true, BadgeText = I18n.T("accounts.provider_available", "Доступно") },
        new() { Id = "OneDrive", Name = "Microsoft OneDrive", Description = "OneDrive & SharePoint API", IconKey = "IconOneDrive", IsAvailable = false, BadgeText = I18n.T("accounts.provider_soon", "Незабаром") },
        new() { Id = "Dropbox", Name = "Dropbox", Description = "Dropbox Cloud API", IconKey = "IconDropbox", IsAvailable = false, BadgeText = I18n.T("accounts.provider_soon", "Незабаром") },
        new() { Id = "Nextcloud", Name = "Nextcloud / ownCloud", Description = "WebDAV Private Cloud", IconKey = "IconNextcloud", IsAvailable = false, BadgeText = I18n.T("accounts.provider_soon", "Незабаром") },
        new() { Id = "AmazonS3", Name = "Amazon S3 / MinIO", Description = "S3-compatible Storage", IconKey = "IconAmazonS3", IsAvailable = false, BadgeText = I18n.T("accounts.provider_soon", "Незабаром") },
        new() { Id = "Sftp", Name = "SFTP (SSH Server)", Description = "Secure File Transfer Protocol", IconKey = "IconSftp", IsAvailable = false, BadgeText = I18n.T("accounts.provider_soon", "Незабаром") },
    };

    public Action<string>? BrowserLauncher { get; set; }

    [ObservableProperty] private int _instructionStep = 1; // 1 to 5

    public bool IsInstructionStep1 => InstructionStep == 1;
    public bool IsInstructionStep2 => InstructionStep == 2;
    public bool IsInstructionStep3 => InstructionStep == 3;
    public bool IsInstructionStep4 => InstructionStep == 4;
    public bool IsInstructionStep5 => InstructionStep == 5;

    public bool CanInstructionPrev => InstructionStep > 1;
    public bool CanInstructionNext => InstructionStep < 5;

    public string InstructionStepCounter => $"{InstructionStep} / 5";

    public AccountWizardViewModel(
        string? initialClientId = null,
        string? initialClientSecret = null,
        IGoogleOAuthService? oauthService = null)
    {
        _clientId = initialClientId ?? "";
        _clientSecret = initialClientSecret ?? "";
        _oauthService = oauthService ?? new GoogleOAuthService();
        _step = 0; // Always start by showing provider catalog
    }

    [RelayCommand]
    public void SelectProvider(CloudProviderOption provider)
    {
        if (provider is null || !provider.IsAvailable) return;
        Step = 1;
    }

    [RelayCommand]
    public void BackToProviders()
    {
        Step = 0;
    }

    [RelayCommand]
    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (BrowserLauncher is not null)
            {
                BrowserLauncher(url);
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch
        {
            // Best effort URL open
        }
    }

    [RelayCommand]
    public async Task StartAuthorizationAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            ErrorMessage = I18n.T("accounts.wizard_client_id_required");
            Step = 4;
            return;
        }

        Step = 2;
        IsBusy = true;
        ErrorMessage = "";
        StatusMessage = I18n.T("accounts.wizard_waiting");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var account = await _oauthService.AuthorizeAsync(
                ClientId.Trim(),
                ClientSecret.Trim(),
                url =>
                {
                    if (BrowserLauncher is not null)
                    {
                        BrowserLauncher(url);
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                },
                _cts.Token);

            ResultAccount = account;
            AuthorizedEmail = account.Email;
            AuthorizedName = account.Name;
            Step = 3;
            StatusMessage = I18n.T("accounts.wizard_success");
        }
        catch (OperationCanceledException)
        {
            Step = 1;
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            Step = 4;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void CancelAuthorization()
    {
        _cts?.Cancel();
        Step = 1;
        IsBusy = false;
    }

    [RelayCommand]
    public void Retry()
    {
        Step = 1;
        ErrorMessage = "";
    }

    [RelayCommand]
    public void NextInstructionStep()
    {
        if (InstructionStep < 5)
        {
            InstructionStep++;
        }
    }

    [RelayCommand]
    public void PrevInstructionStep()
    {
        if (InstructionStep > 1)
        {
            InstructionStep--;
        }
    }

    [RelayCommand]
    public void SetInstructionStep(string stepStr)
    {
        if (int.TryParse(stepStr, out var step) && step >= 1 && step <= 5)
        {
            InstructionStep = step;
        }
    }

    partial void OnInstructionStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsInstructionStep1));
        OnPropertyChanged(nameof(IsInstructionStep2));
        OnPropertyChanged(nameof(IsInstructionStep3));
        OnPropertyChanged(nameof(IsInstructionStep4));
        OnPropertyChanged(nameof(IsInstructionStep5));
        OnPropertyChanged(nameof(CanInstructionPrev));
        OnPropertyChanged(nameof(CanInstructionNext));
        OnPropertyChanged(nameof(InstructionStepCounter));
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep0));
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        OnPropertyChanged(nameof(IsStep4));
    }

    partial void OnClientIdChanged(string value) => OnPropertyChanged(nameof(CanStart));
}
