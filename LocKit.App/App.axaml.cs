using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace LocKit.App;

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
            string? initialProjectPath = null;
            if (desktop.Args != null && desktop.Args.Length > 0)
            {
                initialProjectPath = desktop.Args[0];
            }
            desktop.MainWindow = new MainWindow(initialProjectPath);
        }

        base.OnFrameworkInitializationCompleted();
    }
}