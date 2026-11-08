using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IncidentCommand.App.ViewModels;

namespace IncidentCommand.App.Views;

public partial class MainWindow : Window
{
    static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    DateTime _lastInput = DateTime.UtcNow;
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly MainVm _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm = new MainVm();
        Application.Current.MainWindow = this;

        // Auto-lock after 5 minutes without keyboard or mouse input.
        InputManager.Current.PreProcessInput += (_, _) => _lastInput = DateTime.UtcNow;
        _idle.Tick += (_, _) => { if (DateTime.UtcNow - _lastInput > IdleLimit) Session.Lock(); };
        _idle.Start();

        Closed += (_, _) =>
        {
            _idle.Stop();
            _vm.DetachAll();
            if (Session.Conn != null) { Session.Lock(); Application.Current.Shutdown(); }   // user closed the window
        };
    }
}
