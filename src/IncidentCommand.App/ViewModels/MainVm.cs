using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public OverviewVm Overview { get; } = new();
    public ActionsVm Actions { get; } = new();
    public TimelineVm Timeline { get; } = new();
    public TerminalVm Terminal { get; } = new();
    public PostureVm Posture { get; } = new();
    public SettingsVm Settings { get; } = new();

    public MainVm()
    {
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
            PhaseText = "";
            PhaseBrush = Neutral;
        }
        foreach (var i in Conn.Db.IncidentView.Iter().Where(i => i.Id == id))
        {
            IncidentLine = $"Incident #{i.Id}: {i.Title}  ·  Severity {i.Severity}";
            PhaseText = i.Phase.ToString().ToUpperInvariant();
            PhaseBrush = i.Phase switch
            {
                Phase.Detected or Phase.Analysis => Danger,
                Phase.Containment or Phase.Eradication => Warning,
                _ => Ok,
            };
        }

        var lastAi = Conn.Db.MessageView.Iter()
            .Where(m => m.IncidentId == id && m.AuthorKind == AuthorKind.AI)
            .OrderByDescending(m => m.Id).Select(m => m.Text).FirstOrDefault();
        NextStep = lastAi == null ? "" : "AI recommends: " + (lastAi.Length > 220 ? lastAi[..220] + "…" : lastAi);

        var pending = Conn.Db.ActionView.Iter().Count(a => a.IncidentId == id && a.Status == ActionStatus.Proposed);
        ActionsHeader = pending > 0 ? $"Actions ({pending} waiting)" : "Actions";
    }

    [RelayCommand] void DismissError() => ErrorText = "";
    [RelayCommand] void LockNow() => Session.Lock();

    public void DetachAll()
    {
        Detach(); Overview.Detach(); Actions.Detach(); Timeline.Detach();
        Terminal.Detach(); Posture.Detach(); Settings.Detach();
    }
}
