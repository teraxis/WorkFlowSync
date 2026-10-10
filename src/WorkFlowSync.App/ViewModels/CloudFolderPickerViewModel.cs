using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.OAuth;

namespace WorkFlowSync.App.ViewModels;

public sealed record BreadcrumbItem(string Id, string Name);

public sealed partial class CloudFolderItemViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public DateTimeOffset? ModifiedTime { get; }
    public string ModifiedDisplay => ModifiedTime.HasValue
        ? ModifiedTime.Value.ToLocalTime().ToString("g")
        : "";

    public CloudFolderItemViewModel(CloudFolderItem item)
    {
        Id = item.Id;
        Name = item.Name;
        ModifiedTime = item.ModifiedTime;
    }
}

public sealed record CloudFolderSelection(
    string AccountId,
    string AccountName,
    string AccountEmail,
    string FolderId,
    string FolderName,
    string CloudPath);

public sealed partial class CloudFolderPickerViewModel : ObservableObject
{
    private readonly CloudAccount _account;
    private readonly IGoogleOAuthService _oauthService;

    [ObservableProperty] private string _accountTitle = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _canGoUp;
    [ObservableProperty] private CloudFolderItemViewModel? _selectedFolder;

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
    public ObservableCollection<CloudFolderItemViewModel> Folders { get; } = new();

    public CloudFolderSelection? Result { get; private set; }

    public string CurrentFolderName => Breadcrumbs.Count > 0 ? Breadcrumbs[^1].Name : I18n.T("cloud_picker.my_drive", "My Drive");
    public string CurrentFolderId => Breadcrumbs.Count > 0 ? Breadcrumbs[^1].Id : "root";

    public bool IsRootFolder => CurrentFolderId == "root";

    public string SelectCurrentButtonText => IsRootFolder
        ? I18n.T("cloud_picker.select_entire_drive", "Select entire Google Drive")
        : I18n.T("cloud_picker.select_current", "Select current folder: {0}", CurrentFolderName);

    public event Action? RequestClose;

    public CloudFolderPickerViewModel(CloudAccount account, IGoogleOAuthService? oauthService = null)
    {
        _account = account;
        _oauthService = oauthService ?? new GoogleOAuthService();

        var name = string.IsNullOrWhiteSpace(account.Name) ? account.Email : account.Name;
        AccountTitle = $"Google Drive: {name} ({account.Email})";
    }

    public async Task InitializeAsync()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("root", I18n.T("cloud_picker.my_drive", "My Drive")));
        await LoadFoldersInternalAsync("root");
    }

    [RelayCommand]
    public async Task OpenFolderAsync(CloudFolderItemViewModel folder)
    {
        if (folder is null) return;
        Breadcrumbs.Add(new BreadcrumbItem(folder.Id, folder.Name));
        await LoadFoldersInternalAsync(folder.Id);
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        if (Breadcrumbs.Count <= 1) return;
        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        var target = Breadcrumbs[^1];
        await LoadFoldersInternalAsync(target.Id);
    }

    [RelayCommand]
    public async Task NavigateToBreadcrumbAsync(BreadcrumbItem item)
    {
        if (item is null) return;
        var index = Breadcrumbs.IndexOf(item);
        if (index < 0 || index == Breadcrumbs.Count - 1) return;

        while (Breadcrumbs.Count > index + 1)
        {
            Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        }

        await LoadFoldersInternalAsync(item.Id);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadFoldersInternalAsync(CurrentFolderId);
    }

    [RelayCommand]
    public void ConfirmSelection()
    {
        if (SelectedFolder is not null)
        {
            SetResultAndClose(SelectedFolder.Id, SelectedFolder.Name);
        }
        else
        {
            SetResultAndClose(CurrentFolderId, CurrentFolderName);
        }
    }

    [RelayCommand]
    public void SelectCurrentFolder()
    {
        SetResultAndClose(CurrentFolderId, CurrentFolderName);
    }

    [RelayCommand]
    public void Cancel()
    {
        Result = null;
        RequestClose?.Invoke();
    }

    private void SetResultAndClose(string folderId, string folderName)
    {
        var displayAccount = string.IsNullOrWhiteSpace(_account.Email) ? _account.Name : _account.Email;
        var cloudPath = GoogleDrive.FormatCloudPath(displayAccount, folderName, _account.Id, folderId);
        Result = new CloudFolderSelection(_account.Id, _account.Name, _account.Email, folderId, folderName, cloudPath);
        RequestClose?.Invoke();
    }

    private async Task LoadFoldersInternalAsync(string folderId)
    {
        IsLoading = true;
        ErrorMessage = "";
        HasError = false;
        SelectedFolder = null;
        Folders.Clear();

        try
        {
            var items = await _oauthService.ListFoldersAsync(_account, folderId);
            foreach (var item in items)
            {
                Folders.Add(new CloudFolderItemViewModel(item));
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = I18n.T("cloud_picker.error", "Failed to load folders: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
            CanGoUp = Breadcrumbs.Count > 1;
            OnPropertyChanged(nameof(CurrentFolderName));
            OnPropertyChanged(nameof(CurrentFolderId));
            OnPropertyChanged(nameof(IsRootFolder));
            OnPropertyChanged(nameof(SelectCurrentButtonText));
        }
    }
}
