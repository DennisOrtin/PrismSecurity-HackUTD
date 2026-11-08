using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

/// Every screen rebuilds itself from the client cache when Session.Changed fires.
public abstract class PageVm : ObservableObject
{
    protected PageVm() => Session.Changed += SafeRefresh;
    public void Detach() => Session.Changed -= SafeRefresh;

    void SafeRefresh() { if (Session.Conn != null) Refresh(); }
    public abstract void Refresh();

    protected static DbConnection Conn => Session.Conn!;
    protected static ulong IncidentId => Session.CurrentIncidentId;

    protected static Brush Res(string key) => (Brush)System.Windows.Application.Current.Resources[key];
    public static Brush Danger => Res("DangerBrush");
    public static Brush Warning => Res("WarningBrush");
    public static Brush Ok => Res("OkBrush");
    public static Brush Neutral => Res("NeutralBrush");
}
