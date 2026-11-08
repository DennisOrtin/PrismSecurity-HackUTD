using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record AssetRow(ulong Id, string Host, string DataClass, string Scope, string Status, Brush StatusBrush,
                              string Evidence, string UpdatedBy, bool FromAi);

public sealed record IndicatorRow(string Kind, string Value, string Context, string Confidence, Brush ConfidenceBrush,
                                  string Sources, string LastSeen);

public sealed record PhaseOption(Phase Phase, string Label, bool IsCurrent);

public partial class OverviewVm : PageVm
{
    // ---- Top tiles ----
    [ObservableProperty] string phaseText = "";
    [ObservableProperty] Brush phaseBrush = Neutral;
    [ObservableProperty] string severityText = "";
    [ObservableProperty] Brush severityBrush = Neutral;
    [ObservableProperty] int hostCount;
    [ObservableProperty] int indicatorCount;
    [ObservableProperty] int pendingCount;
    [ObservableProperty] bool hasProposedScope;
    [ObservableProperty] bool hasScope;
    [ObservableProperty] bool hasIndicators;

    /// The phase bar at the bottom of the Overview; the current phase is highlighted.
    public ObservableCollection<PhaseOption> PhaseOptions { get; } = new();

    public ObservableCollection<IndicatorRow> Indicators { get; } = new();
    [ObservableProperty] string aiSummary = "";
    [ObservableProperty] string classification = "";
    [ObservableProperty] string aiUpdated = "";

    public ObservableCollection<AssetRow> Scope { get; } = new();
    public ObservableCollection<AssetRow> ProposedScope { get; } = new();

    [ObservableProperty] bool hasIncident;
    [ObservableProperty] string summary = "";

    // Open incident form (commander)
    [ObservableProperty] string newTitle = "";
    [ObservableProperty] byte newSeverity = 3;

    // Add scope form
    [ObservableProperty] string assetHost = "";
    [ObservableProperty] DataClass assetClass = DataClass.PersonalData;
    [ObservableProperty] ScopeLevel assetScope = ScopeLevel.System;
    [ObservableProperty] DataStatus assetStatus = DataStatus.Unknown;
    [ObservableProperty] string assetEvidence = "";

    public Phase[] Phases { get; } = Enum.GetValues<Phase>();
    public DataClass[] DataClasses { get; } = Enum.GetValues<DataClass>();
    public ScopeLevel[] ScopeLevels { get; } = Enum.GetValues<ScopeLevel>();
    public DataStatus[] Statuses { get; } = Enum.GetValues<DataStatus>();
    public byte[] Severities { get; } = { 1, 2, 3, 4 };
    public bool IsCommander => Session.IsCommander;
    public bool CanAct => Session.CanAct;

    public OverviewVm() => Refresh();

    public override void Refresh()
    {
        var id = IncidentId;
        HasIncident = id != 0;
        Scope.Clear();
        ProposedScope.Clear();
        foreach (var a in Conn.Db.AssetView.Iter().Where(a => a.IncidentId == id).OrderBy(a => a.Host))
        {
            var row = new AssetRow(a.Id, a.Host, Words(a.DataClass.ToString()), a.ScopeLevel.ToString(),
                                   StatusWord(a.Status), StatusBrush(a.Status), a.Evidence, a.UpdatedBy, !a.Accepted);
            (a.Accepted ? Scope : ProposedScope).Add(row);
        }
        PhaseOptions.Clear();
        foreach (var i in Conn.Db.IncidentView.Iter().Where(i => i.Id == id))
        {
            PhaseText = i.Phase.ToString().ToUpperInvariant();
            PhaseBrush = i.Phase switch
            {
                Phase.Detected or Phase.Analysis => Danger,
                Phase.Containment or Phase.Eradication => Warning,
                _ => Ok,
            };
            SeverityText = i.Severity switch { 4 => "4 · Critical", 3 => "3 · High", 2 => "2 · Medium", _ => "1 · Low" };
            SeverityBrush = i.Severity >= 4 ? Danger : i.Severity == 3 ? Warning : Neutral;
            foreach (var ph in Phases) PhaseOptions.Add(new PhaseOption(ph, ph.ToString(), ph == i.Phase));
            Summary = i.Summary;
            AiSummary = string.IsNullOrWhiteSpace(i.AiSummary) ? "The parser hasn't written a summary yet. It updates as events, chat and actions come in." : i.AiSummary;
            Classification = string.IsNullOrWhiteSpace(i.Classification) ? "Not classified yet" : i.Classification;
            AiUpdated = string.IsNullOrWhiteSpace(i.AiSummary) ? "" : $"updated {IncidentCommand.Client.Stdb.Ago(i.AiUpdatedAt)}";
        }

        HostCount = Scope.Select(r => r.Host).Distinct().Count();
        HasScope = Scope.Count > 0;
        HasProposedScope = ProposedScope.Count > 0;
        PendingCount = Conn.Db.ActionView.Iter().Count(a => a.IncidentId == id && a.Status == ActionStatus.Proposed);

        Indicators.Clear();
        foreach (var x in Conn.Db.IndicatorView.Iter().Where(x => x.IncidentId == id)
                     .OrderByDescending(x => x.Confidence).ThenByDescending(x => x.LastSeen.MicrosecondsSinceUnixEpoch))
            Indicators.Add(new IndicatorRow(Words(x.Kind.ToString()), x.Value, x.Context, x.Confidence.ToString().ToUpperInvariant(),
                x.Confidence switch { Confidence.High => Danger, Confidence.Medium => Warning, _ => Neutral },
                x.Sources, IncidentCommand.Client.Stdb.Ago(x.LastSeen)));
        AfterIndicators();
    }

    public void AfterIndicators()
    {
        IndicatorCount = Indicators.Count;
        HasIndicators = Indicators.Count > 0;
    }

    static string Words(string pascal) => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLower(c) : c.ToString()));

    static string StatusWord(DataStatus s) => s switch
    {
        DataStatus.EncryptedAndExfiltrated => "ENCRYPTED + EXFILTRATED",
        _ => s.ToString().ToUpperInvariant(),
    };

    static Brush StatusBrush(DataStatus s) => s switch
    {
        DataStatus.Secure => Ok,
        DataStatus.Unknown => Neutral,
        _ => Danger,
    };

    [RelayCommand]
    void OpenIncident()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) { Session.ReportError("Give the incident a short title."); return; }
        Conn.Reducers.OpenIncident(NewTitle.Trim(), NewSeverity);
        NewTitle = "";
    }

    [RelayCommand]
    void SetPhase(PhaseOption option)
    {
        if (!option.IsCurrent) Conn.Reducers.SetPhase(IncidentId, option.Phase);
    }

    [RelayCommand]
    void AddScope()
    {
        if (string.IsNullOrWhiteSpace(AssetHost)) { Session.ReportError("Which host is affected?"); return; }
        Conn.Reducers.UpsertAsset(0, IncidentId, AssetHost.Trim(), AssetClass, AssetScope, AssetStatus, AssetEvidence.Trim());
        AssetHost = ""; AssetEvidence = "";
    }

    [RelayCommand] void AcceptScope(AssetRow row) => Conn.Reducers.ReviewAsset(row.Id, true);
    [RelayCommand] void RejectScope(AssetRow row) => Conn.Reducers.ReviewAsset(row.Id, false);
}
