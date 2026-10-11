using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.I18n;
using WorkFlowSync.Core.OAuth;

namespace WorkFlowSync.App.ViewModels;

public sealed record BreadcrumbItem(string Id, string Name);

public enum CloudFolderTab
{
    MyDrive,
    Shared,
}

public sealed partial class CloudFolderItemViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public DateTimeOffset? ModifiedTime { get; }
    public bool IsShortcut { get; }
    public string ModifiedDisplay => ModifiedTime.HasValue
        ? ModifiedTime.Value.ToLocalTime().ToString("g")
        : "";

    public CloudFolderItemViewModel(CloudFolderItem item)
    {
        Id = item.EffectiveFolderId;
        Name = item.Name;
        ModifiedTime = item.ModifiedTime;
        IsShortcut = item.IsShortcut;
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
    [ObservableProperty] private CloudFolderTab _activeTab = CloudFolderTab.MyDrive;

    public bool IsMyDriveTab => ActiveTab == CloudFolderTab.MyDrive;
    public bool IsSharedTab => ActiveTab == CloudFolderTab.Shared;
    public bool CanSelectCurrentFolder => CurrentFolderId != "sharedWithMe";

    public string EmptyMessage => IsSharedTab
        ? I18n.T("cloud_picker.empty_shared", "Немає спільних документів або папок")
        : I18n.T("cloud_picker.empty", "У цій теці немає підпапок");

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();
    public ObservableCollection<CloudFolderItemViewModel> Folders { get; } = new();

    public CloudFolderSelection? Result { get; private set; }

    public string CurrentFolderName => Breadcrumbs.Count > 0
        ? Breadcrumbs[^1].Name
        : (IsSharedTab ? I18n.T("cloud_picker.shared_documents", "Спільні документи") : I18n.T("cloud_picker.my_drive", "Мій диск"));

    public string CurrentFolderId => Breadcrumbs.Count > 0 ? Breadcrumbs[^1].Id : (IsSharedTab ? "sharedWithMe" : "root");

    public bool IsRootFolder => CurrentFolderId == "root" || CurrentFolderId == "sharedWithMe";

    public string SelectCurrentButtonText => CurrentFolderId == "sharedWithMe"
        ? I18n.T("cloud_picker.choose_folder_from_list", "Оберіть спільну папку зі списку")
        : (IsRootFolder
            ? I18n.T("cloud_picker.select_entire_drive", "Обрати весь Google Диск")
            : I18n.T("cloud_picker.select_current", "Обрати відкриту теку: {0}", CurrentFolderName));

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
        ActiveTab = CloudFolderTab.MyDrive;
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem("root", I18n.T("cloud_picker.my_drive", "My Drive")));
        await LoadFoldersInternalAsync("root");
    }

    [RelayCommand]
    public async Task SwitchTabAsync(string tabName)
    {
        var newTab = string.Equals(tabName, "shared", StringComparison.OrdinalIgnoreCase)
            ? CloudFolderTab.Shared
            : CloudFolderTab.MyDrive;
        if (ActiveTab == newTab && Breadcrumbs.Count <= 1) return;

        ActiveTab = newTab;
        OnPropertyChanged(nameof(IsMyDriveTab));
        OnPropertyChanged(nameof(IsSharedTab));
        OnPropertyChanged(nameof(EmptyMessage));

        Breadcrumbs.Clear();
        if (ActiveTab == CloudFolderTab.Shared)
        {
            Breadcrumbs.Add(new BreadcrumbItem("sharedWithMe", I18n.T("cloud_picker.shared_documents", "Спільні документи")));
            await LoadFoldersInternalAsync("sharedWithMe");
        }
        else
        {
            Breadcrumbs.Add(new BreadcrumbItem("root", I18n.T("cloud_picker.my_drive", "Мій диск")));
            await LoadFoldersInternalAsync("root");
        }
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
        else if (CanSelectCurrentFolder)
        {
            SetResultAndClose(CurrentFolderId, CurrentFolderName);
        }
    }

    [RelayCommand]
    public void SelectCurrentFolder()
    {
        if (CanSelectCurrentFolder)
        {
            SetResultAndClose(CurrentFolderId, CurrentFolderName);
        }
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
            var items = folderId == "sharedWithMe"
                ? await _oauthService.ListSharedWithMeFoldersAsync(_account)
                : await _oauthService.ListFoldersAsync(_account, folderId);

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
            OnPropertyChanged(nameof(CanSelectCurrentFolder));
            OnPropertyChanged(nameof(SelectCurrentButtonText));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }
}
