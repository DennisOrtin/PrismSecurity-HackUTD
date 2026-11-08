using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

/// The banner that is visible on every screen: incident, phase, what the AI recommends next, errors.
public partial class MainVm : PageVm
{
    [ObservableProperty] string incidentLine = "No open incident";
    [ObservableProperty] string phaseText = "";
    [ObservableProperty] Brush phaseBrush = Neutral;
    [ObservableProperty] string nextStep = "";
    [ObservableProperty] string userLine = "";
    [ObservableProperty] string errorText = "";
    [ObservableProperty] string actionsHeader = "Actions";
    [ObservableProperty] string aiLine = "";
    [ObservableProperty] Brush aiBrush = Neutral;

    public OverviewVm Overview { get; } = new();
    public ActionsVm Actions { get; } = new();
    public TimelineVm Timeline { get; } = new();
    public TerminalVm Terminal { get; } = new();
    public PostureVm Posture { get; } = new();
    public SettingsVm Settings { get; } = new();
    public PreviousIncidentsVm PreviousIncidents { get; }

    [ObservableProperty] int selectedTab;          // 0 = Overview
    [ObservableProperty] bool viewingPast;         // a past incident was opened from "Previous incidents"

    public MainVm()
    {
        PreviousIncidents = new PreviousIncidentsVm(() => SelectedTab = 0);
        Session.Error += msg => ErrorText = msg;
        UserLine = $"{Session.Username} ({Session.Role})";
        Refresh();
    }

    public override void Refresh()
    {
        var id = IncidentId;
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
            IncidentLine = $"Incident #{i.Id}: {i.Title}  ·  Severity {i.Severity}" +
                           (ViewingPast ? (i.Phase == Phase.Closed ? "  ·  past incident (closed)" : "  ·  reopened past incident") : "");
            PhaseText = i.Phase.ToString().ToUpperInvariant();
            PhaseBrush = i.Phase switch
            {
                Phase.Detected or Phase.Analysis => Danger,
                Phase.Containment or Phase.Eradication => Warning,
                _ => Ok,
            };
        }

        var lastAi = Conn.Db.MessageView.Iter()
            .Where(m => m.IncidentId == id && m.AuthorKind == AuthorKind.Assistant)
            .OrderByDescending(m => m.Id).Select(m => m.Text).FirstOrDefault();
        NextStep = lastAi == null ? "" : "AI recommends: " + (lastAi.Length > 220 ? lastAi[..220] + "…" : lastAi);

        foreach (var c in Conn.Db.ConfigView.Iter())
        {
            string M(bool on, string configured, string agent) => on ? AiNames.Name(AiNames.Resolve(configured, agent)) : "off";
            AiLine = $"Local AI · agent: {M(c.AgentEnabled, c.AgentModel, "agent")} · reviewer: {M(c.ReviewerEnabled, c.ReviewerModel, "reviewer")} · parser: {M(c.ParserEnabled, c.ParserModel, "parser")}";
            AiBrush = c.AgentEnabled && c.ReviewerEnabled && c.ParserEnabled ? Neutral : Warning;
        }

        var pending = Conn.Db.ActionView.Iter().Count(a => a.IncidentId == id && a.Status == ActionStatus.Proposed);
        ActionsHeader = pending > 0 ? $"Actions ({pending} waiting)" : "Actions";
    }

    [RelayCommand] void DismissError() => ErrorText = "";
    [RelayCommand] void LockNow() => Session.Lock();
    [RelayCommand] void BackToCurrent() => Session.BackToCurrent();

    public void DetachAll()
    {
        Detach(); Overview.Detach(); Actions.Detach(); Timeline.Detach(); PreviousIncidents.Detach();
        Terminal.Detach(); Posture.Detach(); Settings.Detach();
    }
}
