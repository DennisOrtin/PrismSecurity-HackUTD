using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IncidentCommand.App.ViewModels;

namespace IncidentCommand.App.Views;

public partial class TerminalPanel : UserControl
{
    public const double HeaderHeight = 38;
    public event Action<bool>? CollapseToggled;
    bool _collapsed;
    bool _stickToBottom = true;

    public TerminalPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is TerminalVm vm)
            {
                vm.Prefilled += () =>
                {
                    if (_collapsed) Collapse_Click(this, new RoutedEventArgs());
                    CommandBox.Focus();
                    CommandBox.CaretIndex = CommandBox.Text.Length;
                };
                vm.Commands.CollectionChanged += OnCommandsChanged;
            }
        };
    }

    // Keep the newest output in view, unless the user has scrolled up to read something.
    void OnCommandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_stickToBottom) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroll.ScrollToEnd());
    }

    void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0) _stickToBottom = Scroll.VerticalOffset >= Scroll.ScrollableHeight - 24;
    }

    void CommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not TerminalVm vm) return;
        e.Handled = true;
        _stickToBottom = true;
        if (vm.RunCommand.CanExecute(null)) vm.RunCommand.Execute(null);
    }

    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        CollapseGlyph.Text = _collapsed ? "\uE70E" : "\uE70D";   // chevron up / down
        Scroll.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        InputArea.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseToggled?.Invoke(_collapsed);
    }
}
