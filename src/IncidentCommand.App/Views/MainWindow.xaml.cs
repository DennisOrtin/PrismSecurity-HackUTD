using System.ComponentModel;
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

    // Remember panel sizes while they're hidden on the pages that don't use them.
    GridLength _chatWidth = new(420);
    GridLength _termHeight = new(260);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm = new MainVm();
        Application.Current.MainWindow = this;

        _vm.PropertyChanged += OnVmChanged;
        TerminalPanel.CollapseToggled += collapsed =>
        {
            if (collapsed) { _termHeight = TermRow.Height; TermRow.Height = new GridLength(TerminalPanel.HeaderHeight); }
            else TermRow.Height = _termHeight.Value < 120 ? new GridLength(260) : _termHeight;
        };

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

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainVm.ShowWorkPanels)) return;
        if (_vm.ShowWorkPanels)
        {
            ChatCol.Width = _chatWidth.Value < 260 ? new GridLength(420) : _chatWidth;
            TermRow.Height = _termHeight.Value < TerminalPanel.HeaderHeight ? new GridLength(260) : _termHeight;
            SetPanels(Visibility.Visible);
        }
        else
        {
            _chatWidth = ChatCol.Width;
            _termHeight = TermRow.Height;
            ChatCol.Width = new GridLength(0);
            TermRow.Height = new GridLength(0);
            SetPanels(Visibility.Collapsed);
        }
    }

    void SetPanels(Visibility v)
    {
        ChatPanel.Visibility = v;
        ChatSplitter.Visibility = v;
        TerminalPanel.Visibility = v;
        TermSplitter.Visibility = v;
    }
}
