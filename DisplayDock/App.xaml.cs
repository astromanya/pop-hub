using Microsoft.UI.Xaml;

namespace DisplayDock;

public partial class App : Application
{
    private static Mutex? _singleInstance;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
    }

    internal static void Log(Exception? ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "DisplayDock.log"),
                $"[{DateTime.Now:O}] {ex}\r\n\r\n");
        }
        catch { }
    }

    internal static void Trace(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "DisplayDock.log"),
                $"[{DateTime.Now:O}] {msg}\r\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Tek kopya çalışsın
        _singleInstance = new Mutex(true, "DisplayDock.SingleInstance", out bool created);
        if (!created)
        {
            Exit();
            return;
        }

        // Pencere gizli başlar; fare ekranın üst ortasına gelince görünür.
        try
        {
            _window = new MainWindow();
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }
}
