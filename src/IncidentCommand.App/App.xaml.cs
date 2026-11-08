using System.Windows;
using IncidentCommand.App.Views;

namespace IncidentCommand.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Session.Locked += ShowLockScreen;
        ShowLockScreen();
    }

    void ShowLockScreen()
    {
        var lockWindow = new LockWindow();
        lockWindow.Show();
        foreach (Window w in Current.Windows)
            if (w != lockWindow) w.Close();
    }

    public static void ShowMain()
    {
        var main = new MainWindow();
        main.Show();
        foreach (Window w in Current.Windows)
            if (w != main) w.Close();
    }
}
