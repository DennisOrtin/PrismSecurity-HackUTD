using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IncidentCommand.App.ViewModels;

namespace IncidentCommand.App.Views;

public partial class ChatPanel : UserControl
{
    bool _stickToBottom = true;

    public ChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is ChatVm vm)
            {
                vm.Items.CollectionChanged += (_, _) => KeepAtBottom();
                vm.PropertyChanged += (_, p) => { if (p.PropertyName == nameof(ChatVm.HasHeld)) KeepAtBottom(); };
            }
        };
    }

    // New messages stay in view unless the user scrolled up to read something.
    void KeepAtBottom()
    {
        if (_stickToBottom) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroll.ScrollToEnd());
    }

    void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0) _stickToBottom = Scroll.VerticalOffset >= Scroll.ScrollableHeight - 24;
    }

    // Enter sends; Shift+Enter adds a line.
    void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || DataContext is not ChatVm vm) return;
        e.Handled = true;
        _stickToBottom = true;
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }
}
