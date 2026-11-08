using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.App.Views;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record ActionCard(
    ulong Id, string Title, string StepText, Tier Tier, string TierText, Brush TierBrush,
    string Rationale, string ParamsText, string ContentHash, string HashShort, string ProposedBy,
    string ReviewText, Brush ReviewBrush, bool HasReview, bool Ready, string ApproveLabel);

public sealed record HistoryRow(string When, string Title, string Status, Brush StatusBrush, string By, string Output);

public partial class ActionsVm : PageVm
{
    public ObservableCollection<ActionCard> Pending { get; } = new();
    public ObservableCollection<HistoryRow> History { get; } = new();

    [ObservableProperty] string planReasoning = "";
    [ObservableProperty] string note = "";
    public bool CanAct => Session.CanAct;

    public ActionsVm() => Refresh();

    public override void Refresh()
    {
        var id = IncidentId;
        var actions = Conn.Db.ActionView.Iter().Where(a => a.IncidentId == id).ToList();
        var reviews = Conn.Db.ReviewView.Iter().Where(r => r.TargetKind == "action").ToList();

        PlanReasoning = Conn.Db.PlanView.Iter().Where(p => p.IncidentId == id && p.Active)
            .OrderByDescending(p => p.Id).Select(p => $"{p.Reasoning}\n\n(planned by {p.ModelLabel})").FirstOrDefault() ?? "";

        Pending.Clear();
        foreach (var a in actions.Where(a => a.Status == ActionStatus.Proposed).OrderBy(a => a.PlanId).ThenBy(a => a.StepOrder))
        {
            var siblings = actions.Where(o => o.PlanId == a.PlanId && a.PlanId != 0).ToList();
            bool ready = a.PlanId == 0 || !siblings.Any(o => o.StepOrder < a.StepOrder &&
                         o.Status is ActionStatus.Proposed or ActionStatus.Approved or ActionStatus.Running);
            var worst = reviews.Where(r => r.TargetId == a.Id).OrderByDescending(r => r.Risk).FirstOrDefault();
            bool hasReview = reviews.Any(r => r.TargetId == a.Id);
            var title = CatalogText.Title(a.Kind, a.Host);

            Pending.Add(new ActionCard(
                a.Id, title,
                a.PlanId == 0 ? $"Proposed by {a.ProposedBy}" : $"Step {a.StepOrder} of {siblings.Count}",
                a.Tier, TierText(a.Tier), a.Tier == Tier.ReadOnly ? Neutral : a.Tier == Tier.Containment ? Warning : Danger,
                a.Rationale,
                a.Params.Count == 0 ? "" : string.Join("   ", a.Params.Select(p => $"{p.Key}: {p.Value}")),
                a.ContentHash, a.ContentHash[..12], a.ProposedBy,
                !hasReview ? "Not reviewed yet" : worst!.Risk == Risk.None ? "Reviewer: no concerns" : $"Reviewer: {worst.Risk.ToString().ToUpperInvariant()} - {worst.Reasons}",
                !hasReview ? Warning : worst!.Risk switch { Risk.Danger => Danger, Risk.Caution => Warning, _ => Ok },
                hasReview, ready,
                ready ? $"Approve: {title}" : "Waiting for earlier steps"));
        }

        History.Clear();
        foreach (var a in actions.Where(a => a.Status != ActionStatus.Proposed).OrderByDescending(a => a.UpdatedAt.MicrosecondsSinceUnixEpoch).Take(30))
            History.Add(new HistoryRow(Stdb.Ago(a.UpdatedAt), CatalogText.Title(a.Kind, a.Host), a.Status.ToString().ToUpperInvariant(),
                a.Status switch { ActionStatus.Succeeded => Ok, ActionStatus.Failed or ActionStatus.Denied => Danger, ActionStatus.Running or ActionStatus.Approved => Warning, _ => Neutral },
                a.ApprovedBy, a.Output.Length > 2000 ? a.Output[..2000] + "…" : a.Output));
    }

    static string TierText(Tier t) => t switch
    {
        Tier.ReadOnly => "READ-ONLY: looks, changes nothing",
        Tier.Containment => "CONTAINMENT: changes the network",
        _ => "DESTRUCTIVE: commander only, cannot be undone",
    };

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
}
