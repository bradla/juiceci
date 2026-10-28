using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tessera.App.ViewModels;
using Tessera.App.Views;

namespace Tessera.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            if (desktop.Args is [var path, ..])
                _ = vm.OpenPathAsync(Path.GetFullPath(path));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
