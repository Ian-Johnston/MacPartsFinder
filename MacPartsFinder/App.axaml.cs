using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MacPartsFinder;

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
            // Dark grey's slate title bar: on every window as it opens, and on all open ones when the theme changes.
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => TitleBar.Apply(w));
            ActualThemeVariantChanged += (_, _) =>
            {
                foreach (var w in desktop.Windows) TitleBar.Apply(w);
            };
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void AboutMenu_Click(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main }) main.ShowAbout();
    }
}