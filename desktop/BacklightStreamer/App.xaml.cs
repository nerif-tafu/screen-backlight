using System.Windows;
using BacklightStreamer.Models;
using BacklightStreamer.Services;
using BacklightStreamer.Views;

namespace BacklightStreamer;

public partial class App : System.Windows.Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static StreamEngine Engine { get; } = new();

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "Backlight Streamer error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Settings = SettingsStore.Load();
        StartupService.Apply(Settings.StartOnBoot);

        var startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)
            || Settings.StartMinimized;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (startMinimized)
            window.HideToTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Engine.Dispose();
        SettingsStore.Save(Settings);
        base.OnExit(e);
    }
}
