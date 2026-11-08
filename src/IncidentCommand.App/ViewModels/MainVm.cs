using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

/// One sidebar entry. Glyph is a Segoe Fluent Icons code point (built into Windows 11).
public sealed partial class PageItem : ObservableObject
{
    public PageItem(string glyph, string name) { Glyph = glyph; Name = name; }
    public string Glyph { get; }
    public string Name { get; }
    [ObservableProperty] int badge;              // e.g. actions waiting; 0 = hidden
    public bool HasBadge => Badge > 0;
    partial void OnBadgeChanged(int value) => OnPropertyChanged(nameof(HasBadge));
}

/// The window: sidebar, banner, the current page, and the persistent AI chat (right) and terminal (bottom).
public partial class MainVm : PageVm
{
    public const int OverviewPage = 0, ActivityPage = 1, PreviousPage = 2, PosturePage = 3, SettingsPage = 4;

    // ---- Banner ----
    [ObservableProperty] string incidentLine = "No open incident";
    [ObservableProperty] string phaseText = "";
    [ObservableProperty] Brush phaseBrush = Neutral;
    [ObservableProperty] bool hasPhase;
    [ObservableProperty] string userLine = "";
    [ObservableProperty] string errorText = "";
    [ObservableProperty] string aiLine = "";
    [ObservableProperty] Brush aiBrush = Neutral;
    [ObservableProperty] bool viewingPast;         // a past incident was opened from "Previous incidents"

    // ---- Navigation ----
    public ObservableCollection<PageItem> Pages { get; } = new()
    {
        new("\uE80F", "Overview"),
        new("\uE8FD", "Actions & timeline"),
        new("\uE81C", "Previous incidents"),
        new("\uEA18", "Laptop posture"),
        new("\uE713", "Settings"),
    };
    [ObservableProperty] int selectedPage;
    [ObservableProperty] bool sidebarExpanded;
    [ObservableProperty] double sidebarWidth = 60;
    /// Chat and terminal belong to the incident pages (Overview, Actions & timeline).
    [ObservableProperty] bool showWorkPanels = true;

    // ---- Pages and panels ----
    public OverviewVm Overview { get; } = new();
    public ActivityVm Activity { get; } = new();
    public PreviousIncidentsVm PreviousIncidents { get; }
    public PostureVm Posture { get; } = new();
    public SettingsVm Settings { get; } = new();
    public TerminalVm Terminal { get; } = new();
    public ChatVm Chat { get; }

    public MainVm()
    {
        PreviousIncidents = new PreviousIncidentsVm(() => SelectedPage = OverviewPage);
        Chat = new ChatVm((host, command) => Terminal.Prefill(host, command));
        Session.Error += msg => ErrorText = msg;
        UserLine = $"{Session.Username} · {Session.Role}";
        Refresh();
    }

    partial void OnSelectedPageChanged(int value) => ShowWorkPanels = value is OverviewPage or ActivityPage;
    partial void OnSidebarExpandedChanged(bool value) => SidebarWidth = value ? 220 : 60;

    public override void Refresh()
    {
        var id = IncidentId;
        HasPhase = id != 0;
        if (id == 0)
        {
            IncidentLine = "No open incident";
            ViewingPast = false;
            PhaseText = "";
            PhaseBrush = Neutral;
        }
        foreach (var i in Conn.Db.IncidentView.Iter().Where(i => i.Id == id))
        {
            ViewingPast = Session.SelectedIncidentId != 0;
            IncidentLine = $"#{i.Id}  {i.Title}" +
                           (ViewingPast ? (i.Phase == Phase.Closed ? "   ·   past incident (closed)" : "   ·   reopened past incident") : "");
            PhaseText = i.Phase.ToString().ToUpperInvariant();
            PhaseBrush = i.Phase switch
            {
                Phase.Detected or Phase.Analysis => Danger,
                Phase.Containment or Phase.Eradication => Warning,
                _ => Ok,
            };
        }

        foreach (var c in Conn.Db.ConfigView.Iter())
        {
            string M(bool on, string configured, string agent) => on ? AiNames.Name(AiNames.Resolve(configured, agent)) : "off";
            AiLine = $"agent {M(c.AgentEnabled, c.AgentModel, "agent")}  ·  reviewer {M(c.ReviewerEnabled, c.ReviewerModel, "reviewer")}  ·  parser {M(c.ParserEnabled, c.ParserModel, "parser")}";
            AiBrush = c.AgentEnabled && c.ReviewerEnabled && c.ParserEnabled ? Neutral : Warning;
        }

        Pages[ActivityPage].Badge = Conn.Db.ActionView.Iter().Count(a => a.IncidentId == id && a.Status == ActionStatus.Proposed);
    }

    [RelayCommand] void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;
    [RelayCommand] void DismissError() => ErrorText = "";
    [RelayCommand] void LockNow() => Session.Lock();
    [RelayCommand] void BackToCurrent() => Session.BackToCurrent();

    public void DetachAll()
    {
        Detach(); Overview.Detach(); Activity.Detach(); PreviousIncidents.Detach();
        Terminal.Detach(); Chat.Detach(); Posture.Detach(); Settings.Detach();
    }
}
