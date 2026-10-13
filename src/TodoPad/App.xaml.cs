using System.Windows;

namespace TodoPad;

public partial class App : Application
{
    // Held while the app runs, so a second start (for example from the Startup folder) sees it and exits.
    Mutex? singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstance = new Mutex(true, @"Local\TodoPad.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
