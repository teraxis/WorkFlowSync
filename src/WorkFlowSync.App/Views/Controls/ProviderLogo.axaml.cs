using System;
using Avalonia;
using Avalonia.Controls;

namespace WorkFlowSync.App.Views.Controls;

public partial class ProviderLogo : UserControl
{
    public static readonly StyledProperty<string?> ProviderIdProperty =
        AvaloniaProperty.Register<ProviderLogo, string?>(nameof(ProviderId), defaultValue: "GoogleDrive");

    public string? ProviderId
    {
        get => GetValue(ProviderIdProperty);
        set => SetValue(ProviderIdProperty, value);
    }

    public ProviderLogo()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProviderIdProperty)
        {
            UpdateVisibility();
        }
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var id = ProviderId?.Trim() ?? "GoogleDrive";
        var isGdrive = id.Equals("GoogleDrive", StringComparison.OrdinalIgnoreCase);
        var isOneDrive = id.Equals("OneDrive", StringComparison.OrdinalIgnoreCase);
        var isDropbox = id.Equals("Dropbox", StringComparison.OrdinalIgnoreCase);
        var isNextcloud = id.Equals("Nextcloud", StringComparison.OrdinalIgnoreCase);
        var isAmazonS3 = id.Equals("AmazonS3", StringComparison.OrdinalIgnoreCase);
        var isSftp = id.Equals("Sftp", StringComparison.OrdinalIgnoreCase);

        if (!isGdrive && !isOneDrive && !isDropbox && !isNextcloud && !isAmazonS3 && !isSftp)
        {
            isGdrive = true;
        }

        if (GdriveBox != null) GdriveBox.IsVisible = isGdrive;
        if (OneDriveBox != null) OneDriveBox.IsVisible = isOneDrive;
        if (DropboxBox != null) DropboxBox.IsVisible = isDropbox;
        if (NextcloudBox != null) NextcloudBox.IsVisible = isNextcloud;
        if (AmazonS3Box != null) AmazonS3Box.IsVisible = isAmazonS3;
        if (SftpBox != null) SftpBox.IsVisible = isSftp;
    }
}
