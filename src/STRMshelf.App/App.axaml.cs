using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using STRMshelf.App.Localization;
using STRMshelf.Infrastructure;

namespace STRMshelf.App;

public partial class App : Application
{
    public override void Initialize()
    {
        var bootstrap = new BootstrapConfigStore().Load();
        LocalizationManager.SetLanguage(bootstrap.Language);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
