using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace BlockDpi;

public partial class App : Application
{
    private MainWindow? _window;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            bool autostart = desktop.Args?.Contains("--autostart") == true;

            _window = new MainWindow(autostart);
            _window.ExitRequested += ExitApp;

            if (!autostart) desktop.MainWindow = _window;

            desktop.Exit += (_, _) => _window?.ShutdownEngine();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _window?.ShutdownEngine();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        _window?.ShutdownEngine();
        _desktop?.Shutdown();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();
    private void OnTrayShow(object? sender, EventArgs e) => ShowWindow();
    private void OnTrayToggle(object? sender, EventArgs e) => _window?.Toggle();
    private void OnTrayExit(object? sender, EventArgs e) => ExitApp();

    public void SetTrayText(string text)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is { Count: > 0 }) icons[0].ToolTipText = text;
    }
}

