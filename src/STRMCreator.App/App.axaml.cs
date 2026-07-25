using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using STRMCreator.App.Localization;
using STRMCreator.Infrastructure;

namespace STRMCreator.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var bootstrap = new BootstrapConfigStore().LoadAsync().GetAwaiter().GetResult();
            LocalizationManager.SetLanguage(bootstrap.Language);
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
