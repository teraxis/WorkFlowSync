using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WorkFlowSync.App.ViewModels;
using WorkFlowSync.App.Views;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Optional: WorkFlowSync.exe --config <path>
            var configPath = ConfigFile.DefaultPath;
            var args = desktop.Args ?? Array.Empty<string>();
            for (var i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--config") configPath = args[i + 1];

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(configPath),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
