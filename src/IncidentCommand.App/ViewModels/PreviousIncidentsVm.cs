using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record PastIncidentRow(ulong Id, string Title, string Closed, string Severity, string Classification);
public sealed record PastEntryRow(string When, string Category, string Text, string Author);
public sealed record PastScopeRow(string Host, string Data, string Scope, string Status, Brush StatusBrush);
public sealed record PastIndicatorRow(string Kind, string Value, string Context);
public sealed record PastActionRow(string Title, string Status, string ApprovedBy);

/// Closed incidents: the post-incident report, timeline, affected data, indicators and actions.
/// "Show in Overview" puts the incident back on every screen, where the commander's phase buttons reopen it.
public partial class PreviousIncidentsVm : PageVm
{
    readonly Action _showOverview;

    public ObservableCollection<PastIncidentRow> Incidents { get; } = new();
    public ObservableCollection<PastEntryRow> Timeline { get; } = new();
    public ObservableCollection<PastScopeRow> Scope { get; } = new();
    public ObservableCollection<PastIndicatorRow> Indicators { get; } = new();
    public ObservableCollection<PastActionRow> ActionsTaken { get; } = new();

    [ObservableProperty] PastIncidentRow? selected;
    [ObservableProperty] bool hasSelection;
    [ObservableProperty] string report = "";
    [ObservableProperty] string header = "";
    [ObservableProperty] bool hasIncidents;

    public PreviousIncidentsVm(Action showOverview)
    {
        _showOverview = showOverview;
        Refresh();
    }

    static string When(long micros) =>
        micros <= 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).ToLocalTime().ToString("MMM d, HH:mm");

    public override void Refresh()
    {
        var keep = Selected?.Id;
        var closed = Conn.Db.IncidentView.Iter().Where(i => i.Phase == Phase.Closed)
            .OrderByDescending(i => i.ClosedAtMicros).ThenByDescending(i => i.Id).ToList();
        Incidents.Clear();
        foreach (var i in closed)
            Incidents.Add(new PastIncidentRow(i.Id, $"#{i.Id}  {i.Title}", When(i.ClosedAtMicros), $"Severity {i.Severity}",
                                              string.IsNullOrWhiteSpace(i.Classification) ? "" : i.Classification));
        HasIncidents = Incidents.Count > 0;
        Selected = Incidents.FirstOrDefault(r => r.Id == keep) ?? Incidents.FirstOrDefault();
        LoadDetail();
    }

    partial void OnSelectedChanged(PastIncidentRow? value) => LoadDetail();

    void LoadDetail()
    {
        Timeline.Clear(); Scope.Clear(); Indicators.Clear(); ActionsTaken.Clear();
        HasSelection = Selected != null;
        if (Selected == null) { Report = ""; Header = ""; return; }
        var id = Selected.Id;

        foreach (var i in Conn.Db.IncidentView.Iter().Where(i => i.Id == id))
        {
            Header = $"Incident #{i.Id}: {i.Title}   ·   opened by {i.OpenedBy}   ·   closed {When(i.ClosedAtMicros)}";
            Report = !string.IsNullOrWhiteSpace(i.ClosingSummary) ? i.ClosingSummary
                   : !string.IsNullOrWhiteSpace(i.AiSummary) ? i.AiSummary + "\n\n(The full post-incident report is still being written.)"
                   : "(No report yet. It is written by the agent when the incident is closed.)";
        }

        foreach (var e in Conn.Db.TimelineView.Iter().Where(e => e.IncidentId == id && e.Accepted).OrderBy(e => e.Id))
            Timeline.Add(new PastEntryRow(Stdb.Ago(e.At), e.Category, e.Text, e.Author));

        foreach (var a in Conn.Db.AssetView.Iter().Where(a => a.IncidentId == id && a.Accepted).OrderBy(a => a.Host))
            Scope.Add(new PastScopeRow(a.Host, a.DataClass.ToString(), a.ScopeLevel.ToString(), a.Status.ToString().ToUpperInvariant(),
                a.Status == DataStatus.Secure ? Ok : a.Status == DataStatus.Unknown ? Neutral : Danger));

        foreach (var x in Conn.Db.IndicatorView.Iter().Where(x => x.IncidentId == id).OrderBy(x => x.Kind.ToString()))
            Indicators.Add(new PastIndicatorRow(x.Kind.ToString(), x.Value, x.Context));

        foreach (var a in Conn.Db.ActionView.Iter()
                     .Where(a => a.IncidentId == id && a.Status is ActionStatus.Succeeded or ActionStatus.Failed).OrderBy(a => a.Id))
            ActionsTaken.Add(new PastActionRow(CatalogText.Title(a.Kind, a.Host) + ParamText(a.Params), a.Status.ToString(), a.ApprovedBy));
    }

    static string ParamText(List<Param> ps) => ps.Count == 0 ? "" : " (" + string.Join(", ", ps.Select(p => $"{p.Key} {p.Value}")) + ")";

    [RelayCommand]
    void ShowInOverview()
    {
        if (Selected == null) return;
        Session.ViewIncident(Selected.Id);
        _showOverview();
    }
}
