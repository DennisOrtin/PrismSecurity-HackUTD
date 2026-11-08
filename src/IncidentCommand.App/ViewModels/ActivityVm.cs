using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.App.Views;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

/// A proposed action waiting for a human decision.
public sealed record ActionCard(
    ulong Id, string Title, string StepText, Tier Tier, string TierText, Brush TierBrush,
    string Rationale, string ParamsText, bool HasParams, string ContentHash, string HashShort, string ProposedBy,
    string ReviewText, Brush ReviewBrush, bool Ready, string ApproveLabel);

/// A finished (or running / denied) action on the timeline.
public sealed record ActionEvent(string When, string Title, string ParamsText, string Status, Brush StatusBrush,
                                 string By, string Output, bool HasOutput);

/// A timeline entry (human, system, or an AI draft waiting to be accepted).
public sealed record EntryEvent(ulong Id, string When, string Category, Brush CategoryBrush, string Text, string Author,
                                bool IsAiDraft, Brush AuthorBrush, string AcceptLabel);

/// "Actions & timeline": the AI's plan and the note at the top, decisions waiting, then one timeline in which
/// actions and entries are merged by time (newest first), because both are timestamped and both may need approval.
public partial class ActivityVm : PageVm
{
    string _signature = "";

    public ObservableCollection<ActionCard> Pending { get; } = new();
    public ObservableCollection<object> Feed { get; } = new();

    [ObservableProperty] string planReasoning = "";
    [ObservableProperty] string planBy = "";
    [ObservableProperty] bool hasPlan;
    [ObservableProperty] bool hasPending;
    [ObservableProperty] string note = "";

    // Add-to-timeline row. Choosing a phase here also MOVES the incident to that phase.
    public string[] Categories { get; } =
        { "Note", "Communication", "Detected", "Analysis", "Containment", "Eradication", "Recovery", "Closed" };
    [ObservableProperty] string entryCategory = "Note";
    [ObservableProperty] string entryText = "";

    public bool CanAct => Session.CanAct;

    public ActivityVm() => Refresh();

    public override void Refresh()
    {
        var id = IncidentId;
        var actions = Conn.Db.ActionView.Iter().Where(a => a.IncidentId == id).ToList();
        var entries = Conn.Db.TimelineView.Iter().Where(e => e.IncidentId == id).ToList();
        var reviews = Conn.Db.ReviewView.Iter().Where(r => r.TargetKind == "action").ToList();
        var plan = Conn.Db.PlanView.Iter().Where(p => p.IncidentId == id && p.Active).OrderByDescending(p => p.Id).ToList();
        var phase = Conn.Db.IncidentView.Iter().Where(i => i.Id == id).Select(i => i.Phase).FirstOrDefault();

        var sig = $"{id}|{phase}|{plan.Select(p => p.Id).FirstOrDefault()}|" +
                  string.Join(",", actions.Select(a => $"{a.Id}:{a.Status}:{a.Output.Length}")) + "|" +
                  string.Join(",", entries.Select(e => $"{e.Id}:{e.Accepted}")) + "|" + reviews.Count;
        if (sig == _signature) return;
        _signature = sig;

        HasPlan = plan.Count > 0;
        PlanReasoning = plan.Select(p => p.Reasoning).FirstOrDefault() ?? "No plan yet. Ask the AI on the right, or wait for it to react to new events.";
        PlanBy = plan.Select(p => $"Planned by {p.ModelLabel}").FirstOrDefault() ?? "";

        // ---- Waiting for a decision ----
        Pending.Clear();
        foreach (var a in actions.Where(a => a.Status == ActionStatus.Proposed).OrderBy(a => a.PlanId).ThenBy(a => a.StepOrder))
        {
            var siblings = actions.Where(o => o.PlanId == a.PlanId && a.PlanId != 0).ToList();
            // Mirrors the module: read-only steps never wait; changes wait for every earlier step.
            bool ready = a.PlanId == 0 || a.Tier == Tier.ReadOnly || !siblings.Any(o => o.StepOrder < a.StepOrder &&
                         o.Status is ActionStatus.Proposed or ActionStatus.Approved or ActionStatus.Running);
            var mine = reviews.Where(r => r.TargetId == a.Id).ToList();
            var worst = mine.Count == 0 ? (Risk?)null : mine.Max(r => r.Risk);
            var reasons = mine.OrderByDescending(r => r.Risk).Select(r => r.Reasons).FirstOrDefault() ?? "";
            var title = CatalogText.Title(a.Kind, a.Host);
            var ps = ParamText(a.Params);

            Pending.Add(new ActionCard(
                a.Id, title,
                a.PlanId == 0 ? $"Proposed by {a.ProposedBy}" : $"Step {a.StepOrder} of {siblings.Count}",
                a.Tier, TierText(a.Tier), a.Tier == Tier.ReadOnly ? Neutral : a.Tier == Tier.Containment ? Warning : Danger,
                a.Rationale, ps, ps.Length > 0, a.ContentHash, a.ContentHash[..12], a.ProposedBy,
                worst == null ? "Reviewer: not reviewed yet" : worst == Risk.None ? "Reviewer: no concerns" : $"Reviewer: {worst.ToString()!.ToUpperInvariant()}: {reasons}",
                worst == null ? Warning : worst switch { Risk.Danger => Danger, Risk.Caution => Warning, _ => Ok },
                ready, ready ? "Approve" : "Waiting for earlier steps"));
        }
        HasPending = Pending.Count > 0;

        // ---- One timeline: finished actions and entries, newest first ----
        var feed = new List<(long At, object Item)>();
        foreach (var a in actions.Where(a => a.Status != ActionStatus.Proposed))
        {
            var (word, brush) = a.Status switch
            {
                ActionStatus.Succeeded => ("SUCCEEDED", Ok),
                ActionStatus.Failed => ("FAILED", Danger),
                ActionStatus.Denied => ("DENIED", Danger),
                ActionStatus.Running => ("RUNNING", Res("AccentBrush")),
                ActionStatus.Approved => ("APPROVED", Warning),
                _ => ("STALE", Neutral),
            };
            var output = a.Output.Length > 4000 ? a.Output[..4000] + "\n…" : a.Output;
            var by = a.Status == ActionStatus.Denied ? $"denied by {a.ApprovedBy}" : string.IsNullOrEmpty(a.ApprovedBy) ? "" : $"approved by {a.ApprovedBy}";
            feed.Add((a.UpdatedAt.MicrosecondsSinceUnixEpoch,
                new ActionEvent(Stdb.Ago(a.UpdatedAt), CatalogText.Title(a.Kind, a.Host), ParamText(a.Params), word, brush, by, output, output.Length > 0)));
        }
        foreach (var e in entries)
        {
            bool ai = e.AuthorKind == AuthorKind.Assistant;
            bool movesPhase = Enum.TryParse<Phase>(e.Category, out var p) && p != phase;
            feed.Add((e.At.MicrosecondsSinceUnixEpoch, new EntryEvent(
                e.Id, Stdb.Ago(e.At), e.Category, CategoryBrush(e.Category), e.Text,
                ai ? $"{e.Author} (AI)" : e.Author, ai && !e.Accepted, ai ? Res("AccentBrush") : Res("SubTextBrush"),
                movesPhase ? $"Accept: move to {p}" : "Accept")));
        }
        Feed.Clear();
        foreach (var (_, item) in feed.OrderByDescending(f => f.At)) Feed.Add(item);
    }

    static string ParamText(List<Param> ps) => string.Join("   ", ps.Select(p => $"{p.Key}: {p.Value}"));

    static string TierText(Tier t) => t switch
    {
        Tier.ReadOnly => "READ-ONLY · looks, changes nothing",
        Tier.Containment => "CHANGES THE NETWORK",
        _ => "DESTRUCTIVE · commander only",
    };

    static Brush CategoryBrush(string category) => category switch
    {
        "Detected" or "Analysis" => Danger,
        "Containment" or "Eradication" => Warning,
        "Recovery" or "Closed" => Ok,
        "Communication" => Res("AccentBrush"),
        _ => Neutral,
    };

    // ---- Decisions ----

    [RelayCommand]
    void Approve(ActionCard card)
    {
        if (!card.Ready) return;
        if (card.Tier != Tier.ReadOnly &&
            !StepUpDialog.Confirm($"{card.Title}\n{card.ParamsText}", card.Tier == Tier.Destructive ? "Approve destructive action" : "Approve"))
            return;
        // Only the ID and the hash we displayed go back. The executor runs the STORED proposal.
        Conn.Reducers.ApproveAction(card.Id, card.ContentHash, Note.Trim());
        Note = "";
    }

    [RelayCommand]
    void Deny(ActionCard card)
    {
        Conn.Reducers.DenyAction(card.Id, Note.Trim());
        Note = "";
    }

    [RelayCommand]
    void ApproveAllReadOnly()
    {
        foreach (var c in Pending.Where(c => c.Tier == Tier.ReadOnly && c.Ready).ToList())
            Conn.Reducers.ApproveAction(c.Id, c.ContentHash, "bulk approve (read-only)");
    }

    // ---- Timeline ----

    [RelayCommand] void Accept(EntryEvent row) => Conn.Reducers.ReviewTimelineEntry(row.Id, true);
    [RelayCommand] void Reject(EntryEvent row) => Conn.Reducers.ReviewTimelineEntry(row.Id, false);

    [RelayCommand]
    void AddEntry()
    {
        if (IncidentId == 0) { Session.ReportError("Open an incident first."); return; }
        bool isPhase = Enum.TryParse<Phase>(EntryCategory, out var phase);
        if (!isPhase && string.IsNullOrWhiteSpace(EntryText)) return;
        if (isPhase)
        {
            var current = Conn.Db.IncidentView.Iter().Where(i => i.Id == IncidentId).Select(i => i.Phase).FirstOrDefault();
            if (!Session.IsCommander)
                Session.ReportError("Only the incident commander can move the incident to a new phase. Your note was still added.");
            else if (phase != current)
                Conn.Reducers.SetPhase(IncidentId, phase);   // the module also writes "Phase changed to ..." to the timeline
        }
        if (!string.IsNullOrWhiteSpace(EntryText))
            Conn.Reducers.AddTimelineEntry(IncidentId, EntryCategory, EntryText.Trim());
        EntryText = "";
    }
}
