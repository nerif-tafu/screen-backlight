using System.Windows;
using BacklightStreamer.Models;
using BacklightStreamer.Services;
using BacklightStreamer.Views;

namespace BacklightStreamer;

public partial class App : System.Windows.Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static StreamEngine Engine { get; } = new();
    public static LocalApiServer Api { get; } = new(Engine);

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // An ambient backlight should never compete with a game for CPU time;
        // below-normal priority keeps the stream running while yielding under
        // load. Costs at most a little LED latency during full CPU saturation.
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // not critical if the priority can't be changed
        }

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "Backlight Streamer error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Settings = SettingsStore.Load();
        StartupService.Apply(Settings.StartOnBoot);

        try
        {
            Api.Start(Settings);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not start local API on port {Settings.ApiPort}: {ex.Message}",
                "Backlight Streamer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

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
        Api.Dispose();
        Engine.Dispose();
        SettingsStore.Save(Settings);
        base.OnExit(e);
    }
}
