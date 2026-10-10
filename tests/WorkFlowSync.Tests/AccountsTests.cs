using System.Net;
using System.Text.Json;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.OAuth;
using Xunit;

namespace WorkFlowSync.Tests;

public sealed class AccountsTests
{
    [Fact]
    public void CloudAccount_RoundTrip_ThroughConfigJson()
    {
        var config = new SyncConfig();
        var account = new CloudAccount
        {
            Id = "acc-123",
            Provider = CloudProvider.GoogleDrive,
            Name = "Тестовий Користувач",
            Email = "user@example.com",
            ClientId = "test-client-id.apps.googleusercontent.com",
            ClientSecret = "test-secret",
            AccessToken = "ya29.test-access-token",
            RefreshToken = "1//test-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsActive = true
        };

        config.Accounts.Add(account);

        var json = JsonSerializer.Serialize(config);
        var loaded = JsonSerializer.Deserialize<SyncConfig>(json);

        Assert.NotNull(loaded);
        Assert.Single(loaded.Accounts);
        var reloaded = loaded.Accounts[0];
        Assert.Equal("acc-123", reloaded.Id);
        Assert.Equal(CloudProvider.GoogleDrive, reloaded.Provider);
        Assert.Equal("Тестовий Користувач", reloaded.Name);
        Assert.Equal("user@example.com", reloaded.Email);
        Assert.Equal("test-client-id.apps.googleusercontent.com", reloaded.ClientId);
        Assert.Equal("test-secret", reloaded.ClientSecret);
        Assert.Equal("ya29.test-access-token", reloaded.AccessToken);
        Assert.Equal("1//test-refresh-token", reloaded.RefreshToken);
        Assert.True(reloaded.IsActive);
        Assert.False(reloaded.IsExpired);
    }

    [Fact]
    public void CloudAccount_IsExpired_BehavesCorrectly()
    {
        var fresh = new CloudAccount
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        Assert.False(fresh.IsExpired);

        var expiringVerySoon = new CloudAccount
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30) // Within 60s safety buffer
        };
        Assert.True(expiringVerySoon.IsExpired);

        var expired = new CloudAccount
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
        };
        Assert.True(expired.IsExpired);

        var noExpiry = new CloudAccount
        {
            ExpiresAtUtc = null
        };
        Assert.False(noExpiry.IsExpired);
    }

    [Fact]
    public void GetActiveGoogleAccount_ReturnsFirstActiveGoogleDriveAccount()
    {
        var config = new SyncConfig();
        Assert.Null(config.GetActiveGoogleAccount());

        var inactive = new CloudAccount
        {
            Id = "1",
            Provider = CloudProvider.GoogleDrive,
            Email = "inactive@gmail.com",
            IsActive = false
        };
        config.Accounts.Add(inactive);
        Assert.Null(config.GetActiveGoogleAccount());

        var active = new CloudAccount
        {
            Id = "2",
            Provider = CloudProvider.GoogleDrive,
            Email = "active@gmail.com",
            IsActive = true
        };
        config.Accounts.Add(active);

        var res = config.GetActiveGoogleAccount();
        Assert.NotNull(res);
        Assert.Equal("active@gmail.com", res.Email);
    }

    [Fact]
    public void GoogleOAuthService_GetFreeLoopbackPort_ReturnsValidPort()
    {
        var port = GoogleOAuthService.GetFreeLoopbackPort();
        Assert.InRange(port, 1024, 65535);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task GoogleOAuthService_RefreshTokenIfNeeded_RefreshesWhenExpired()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("https://oauth2.googleapis.com/token", req.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"ya29.new-refreshed-token\",\"expires_in\":3600}")
            };
        });

        using var client = new HttpClient(handler);
        var service = new GoogleOAuthService(client);

        var account = new CloudAccount
        {
            ClientId = "cid",
            ClientSecret = "csec",
            RefreshToken = "rt_123",
            AccessToken = "ya29.old",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5) // Expired
        };

        var ok = await service.RefreshTokenIfNeededAsync(account);
        Assert.True(ok);
        Assert.Equal("ya29.new-refreshed-token", account.AccessToken);
        Assert.False(account.IsExpired);
    }

    [Fact]
    public async Task GoogleOAuthService_RefreshTokenIfNeeded_SkipsWhenStillValid()
    {
        var handlerCalled = false;
        var handler = new MockHttpMessageHandler(_ =>
        {
            handlerCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(handler);
        var service = new GoogleOAuthService(client);

        var account = new CloudAccount
        {
            ClientId = "cid",
            ClientSecret = "csec",
            RefreshToken = "rt_123",
            AccessToken = "ya29.valid",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) // Still valid
        };

        var ok = await service.RefreshTokenIfNeededAsync(account);
        Assert.True(ok);
        Assert.False(handlerCalled);
        Assert.Equal("ya29.valid", account.AccessToken);
    }

    [Fact]
    public void AccountWizardViewModel_CanStart_RequiresClientId()
    {
        var vm = new AccountWizardViewModel();
        Assert.False(vm.CanStart);

        vm.ClientId = "   ";
        Assert.False(vm.CanStart);

        vm.ClientId = "my-client-id.apps.googleusercontent.com";
        Assert.True(vm.CanStart);

        vm.IsBusy = true;
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void AccountWizardViewModel_CancelAuthorization_ResetsToStep1()
    {
        var vm = new AccountWizardViewModel("cid", "secret")
        {
            Step = 2,
            IsBusy = true
        };

        vm.CancelAuthorization();

        Assert.Equal(1, vm.Step);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void AccountWizardViewModel_AlwaysStartsAtStep0_AndSupportsCarouselNavigation()
    {
        // Even with initial ClientId/Secret, it must start at step 0 to display provider catalog
        var vm = new AccountWizardViewModel("prefilled-client-id", "prefilled-secret");
        Assert.Equal(0, vm.Step);
        Assert.True(vm.IsStep0);
        Assert.NotEmpty(vm.Providers);
        Assert.Equal("prefilled-client-id", vm.ClientId);

        // Selecting Google Drive transitions to Step 1
        var gdrive = vm.Providers.First(p => p.Id == "GoogleDrive");
        vm.SelectProvider(gdrive);
        Assert.Equal(1, vm.Step);
        Assert.True(vm.IsStep1);

        // Carousel starts at step 1
        Assert.Equal(1, vm.InstructionStep);
        Assert.True(vm.IsInstructionStep1);
        Assert.False(vm.CanInstructionPrev);
        Assert.True(vm.CanInstructionNext);
        Assert.Equal("1 / 5", vm.InstructionStepCounter);

        // Advance to step 2
        vm.NextInstructionStep();
        Assert.Equal(2, vm.InstructionStep);
        Assert.True(vm.IsInstructionStep2);
        Assert.True(vm.CanInstructionPrev);
        Assert.True(vm.CanInstructionNext);

        // Advance to step 5 (max)
        vm.NextInstructionStep();
        vm.NextInstructionStep();
        vm.NextInstructionStep();
        Assert.Equal(5, vm.InstructionStep);
        Assert.True(vm.IsInstructionStep5);
        Assert.False(vm.CanInstructionNext);

        // Further next does not exceed 5
        vm.NextInstructionStep();
        Assert.Equal(5, vm.InstructionStep);

        // Step back
        vm.PrevInstructionStep();
        Assert.Equal(4, vm.InstructionStep);

        // Direct jump via SetInstructionStep
        vm.SetInstructionStep("3");
        Assert.Equal(3, vm.InstructionStep);
        Assert.True(vm.IsInstructionStep3);
    }

    [Fact]
    public void AccountItemViewModel_ExposesProviderId()
    {
        var account = new CloudAccount
        {
            Id = "a1",
            Provider = CloudProvider.GoogleDrive,
            Email = "user@gmail.com",
            Name = "User"
        };

        var item = new AccountItemViewModel(account, _ => { }, () => { });
        Assert.Equal("GoogleDrive", item.ProviderId);
    }

    [Fact]
    public void MainViewModel_AccountsCollection_IntegratesWithConfig()
    {
        var tempConfigPath = Path.Combine(Path.GetTempPath(), $"wfs_acc_test_{Guid.NewGuid():N}.json");
        try
        {
            var initial = new SyncConfig();
            initial.Pairs.Add(new FolderPair { Name = "p1", Source = @"C:\src", Target = @"C:\dst", Enabled = false });
            initial.Accounts.Add(new CloudAccount
            {
                Id = "a1",
                Provider = CloudProvider.GoogleDrive,
                Email = "hello@world.com",
                Name = "Hello",
                IsActive = true
            });
            ConfigFile.Save(initial, tempConfigPath);

            var vm = new MainViewModel(tempConfigPath);
            vm.Background.Stop();
            Assert.True(vm.HasAccounts);
            Assert.Single(vm.Accounts);
            Assert.Equal("hello@world.com", vm.Accounts[0].Email);

            // Add new account
            vm.AddAccount(new CloudAccount
            {
                Id = "a2",
                Provider = CloudProvider.GoogleDrive,
                Email = "second@world.com",
                Name = "Second",
                IsActive = true
            });

            Assert.Equal(2, vm.Accounts.Count);

            var reloaded = ConfigFile.LoadOrDefault(tempConfigPath);
            Assert.Equal(2, reloaded.Accounts.Count);

            // Remove account
            vm.RemoveAccount(vm.Accounts[0]);
            Assert.Single(vm.Accounts);

            var reloadedAfterRemove = ConfigFile.LoadOrDefault(tempConfigPath);
            Assert.Single(reloadedAfterRemove.Accounts);
            Assert.Equal("second@world.com", reloadedAfterRemove.Accounts[0].Email);
        }
        finally
        {
            if (File.Exists(tempConfigPath)) File.Delete(tempConfigPath);
        }
    }

    [Fact]
    public void GoogleDrive_CloudPath_FormatAndParse()
    {
        var path = GoogleDrive.FormatCloudPath("test@gmail.com", "My Reports", "acc123", "fld456");
        Assert.StartsWith("gdrive://", path);
        Assert.True(GoogleDrive.IsCloudPath(path));
        Assert.True(GoogleDrive.IsGoogleDrivePath(path));

        var (accountName, folderName, accountId, folderId) = GoogleDrive.ParseCloudPath(path);
        Assert.Equal("test@gmail.com", accountName);
        Assert.Equal("My Reports", folderName);
        Assert.Equal("acc123", accountId);
        Assert.Equal("fld456", folderId);
    }

    [Fact]
    public void SyncConfig_Nesting_HandlesCloudPaths()
    {
        var cloud1 = GoogleDrive.FormatCloudPath("acc@gmail.com", "FolderA", "acc1", "fld1");
        var cloud2 = GoogleDrive.FormatCloudPath("acc@gmail.com", "FolderB", "acc1", "fld2");
        var local = @"C:\Local\Folder";

        // Cloud + Local are never nested
        Assert.Null(SyncConfig.Nesting(cloud1, local));
        Assert.Null(SyncConfig.Nesting(local, cloud1));

        // Different cloud folders are not nested
        Assert.Null(SyncConfig.Nesting(cloud1, cloud2));

        // Exactly the same cloud path is rejected
        var sameResult = SyncConfig.Nesting(cloud1, cloud1);
        Assert.NotNull(sameResult);
        Assert.Contains("same folder", sameResult);
    }

    [Fact]
    public void PairViewModel_GoogleDocExportApplies_TogglesWithCloudPath()
    {
        var vm = new PairViewModel();
        vm.Source = @"C:\Regular\Folder";
        vm.Target = @"D:\Backup\Folder";
        Assert.False(vm.GoogleDocExportApplies);

        // Setting cloud source enables it
        vm.Source = GoogleDrive.FormatCloudPath("user@gmail.com", "Docs", "a1", "f1");
        Assert.True(vm.GoogleDocExportApplies);

        // Resetting source to local but target to cloud still enables it
        vm.Source = @"C:\Regular\Folder";
        Assert.False(vm.GoogleDocExportApplies);
        vm.Target = GoogleDrive.FormatCloudPath("user@gmail.com", "Docs", "a1", "f1");
        Assert.True(vm.GoogleDocExportApplies);
    }

    [Fact]
    public async Task CloudFolderPickerViewModel_NavigatesAndSelects()
    {
        var mockOauth = new MockOAuthService();
        var account = new CloudAccount
        {
            Id = "acc999",
            Email = "picker@test.com",
            Name = "Picker User",
            AccessToken = "valid_token"
        };

        var vm = new CloudFolderPickerViewModel(account, mockOauth);
        await vm.InitializeAsync();

        Assert.Equal("root", vm.CurrentFolderId);
        Assert.True(vm.IsRootFolder);
        Assert.Single(vm.Breadcrumbs);
        Assert.Equal(2, vm.Folders.Count);
        Assert.Equal("Subfolder 1", vm.Folders[0].Name);

        // Open subfolder
        await vm.OpenFolderAsync(vm.Folders[0]);
        Assert.Equal("folder-1", vm.CurrentFolderId);
        Assert.False(vm.IsRootFolder);
        Assert.Equal(2, vm.Breadcrumbs.Count);

        // Go up
        await vm.GoUpAsync();
        Assert.Equal("root", vm.CurrentFolderId);
        Assert.True(vm.IsRootFolder);
        Assert.Single(vm.Breadcrumbs);

        // Select current folder (entire drive)
        vm.SelectCurrentFolder();
        Assert.NotNull(vm.Result);
        Assert.Equal("root", vm.Result.FolderId);
        Assert.Equal("acc999", vm.Result.AccountId);
        Assert.StartsWith("gdrive://", vm.Result.CloudPath);
    }

    [Fact]
    public void CloudAccountsSummary_And_GoToAccounts_WorkCorrectly()
    {
        var tempConfigPath = Path.Combine(Path.GetTempPath(), $"wfs_cloud_summary_{Guid.NewGuid():N}.json");
        try
        {
            var config = new SyncConfig();
            config.Pairs.Add(new FolderPair { Name = "p1", Source = @"C:\src", Target = @"C:\dst", Enabled = false });
            ConfigFile.Save(config, tempConfigPath);

            var vm = new MainViewModel(tempConfigPath);
            vm.Background.Stop();

            // Zero accounts
            Assert.False(string.IsNullOrWhiteSpace(vm.CloudAccountsSummary));

            // Navigation to accounts page
            vm.SelectedPage = 3;
            Assert.True(vm.IsSettingsPage);
            vm.GoToAccountsCommand.Execute(null);
            Assert.Equal(2, vm.SelectedPage);
            Assert.True(vm.IsAccountsPage);

            // 1 account
            vm.AddAccount(new CloudAccount
            {
                Id = "acc-1",
                Name = "Google Drive 1",
                Email = "user1@example.com",
                Provider = CloudProvider.GoogleDrive
            });
            Assert.Contains("Google Drive 1", vm.CloudAccountsSummary);

            // 2 accounts
            vm.AddAccount(new CloudAccount
            {
                Id = "acc-2",
                Name = "Google Drive 2",
                Email = "user2@example.com",
                Provider = CloudProvider.GoogleDrive
            });
            Assert.Contains("2", vm.CloudAccountsSummary);
        }
        finally
        {
            if (File.Exists(tempConfigPath)) File.Delete(tempConfigPath);
        }
    }

    private sealed class MockOAuthService : IGoogleOAuthService
    {
        public Task<CloudAccount> AuthorizeAsync(string clientId, string clientSecret, Action<string> openBrowser, CancellationToken ct = default)
            => Task.FromResult(new CloudAccount());

        public Task<bool> RefreshTokenIfNeededAsync(CloudAccount account, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> TestConnectionAsync(CloudAccount account, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<CloudFolderItem>> ListFoldersAsync(CloudAccount account, string parentId = "root", CancellationToken ct = default)
        {
            IReadOnlyList<CloudFolderItem> list = parentId == "root"
                ? new List<CloudFolderItem>
                {
                    new("folder-1", "Subfolder 1", DateTimeOffset.UtcNow),
                    new("folder-2", "Subfolder 2", DateTimeOffset.UtcNow),
                }
                : new List<CloudFolderItem>
                {
                    new("folder-nested", "Nested Child", DateTimeOffset.UtcNow)
                };
            return Task.FromResult(list);
        }
    }
}
