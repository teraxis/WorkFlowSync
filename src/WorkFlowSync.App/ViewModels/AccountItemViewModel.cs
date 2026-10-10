using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.OAuth;

namespace WorkFlowSync.App.ViewModels;

public sealed partial class AccountItemViewModel : ObservableObject
{
    private readonly CloudAccount _model;
    private readonly IGoogleOAuthService _oauthService;
    private readonly Action<AccountItemViewModel> _onRemove;
    private readonly Action _onChanged;

    public CloudAccount Model => _model;

    public string Id => _model.Id;
    public CloudProvider Provider => _model.Provider;
    public string ProviderId => _model.Provider.ToString();

    public string ProviderName => _model.Provider switch
    {
        CloudProvider.GoogleDrive => I18n.T("accounts.provider_google"),
        _ => _model.Provider.ToString(),
    };

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _email;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    public bool IsExpired => _model.IsExpired;

    public string StatusBadgeText => _model.IsActive
        ? (_model.IsExpired ? I18n.T("accounts.status_expired") : I18n.T("accounts.status_connected"))
        : I18n.T("accounts.status_inactive");

    public string CreatedAtSummary => I18n.T("accounts.connected_date", _model.CreatedAtUtc.ToLocalTime().ToString("g"));

    public AccountItemViewModel(
        CloudAccount model,
        Action<AccountItemViewModel> onRemove,
        Action onChanged,
        IGoogleOAuthService? oauthService = null)
    {
        _model = model;
        _onRemove = onRemove;
        _onChanged = onChanged;
        _oauthService = oauthService ?? new GoogleOAuthService();

        _name = model.Name;
        _email = model.Email;
        _isActive = model.IsActive;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var ok = await _oauthService.TestConnectionAsync(_model);
            if (ok)
            {
                StatusIsError = false;
                StatusMessage = I18n.T("accounts.test_success");
                OnPropertyChanged(nameof(IsExpired));
                OnPropertyChanged(nameof(StatusBadgeText));
            }
            else
            {
                StatusIsError = true;
                StatusMessage = I18n.T("accounts.test_failed", "Token rejected or expired");
            }
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            StatusMessage = I18n.T("accounts.test_failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Remove()
    {
        _onRemove(this);
    }

    partial void OnIsActiveChanged(bool value)
    {
        _model.IsActive = value;
        OnPropertyChanged(nameof(StatusBadgeText));
        _onChanged();
    }
}
