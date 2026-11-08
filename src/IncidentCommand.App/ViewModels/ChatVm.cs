using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record ModelOption(string Label, string Value);   // Value = LM Studio model ID, "" = the agent's default

/// One chat bubble. Human messages sit on the right, AI on the left, system notices centred.
public sealed record ChatMessageItem(string When, string Author, string Text, string ModelLabel,
                                     HorizontalAlignment Align, Brush Bubble, Brush AuthorBrush, bool IsSystem);

/// An AI-suggested command for the human terminal. NEVER executable from here: "Copy to terminal" only pre-fills it.
public sealed record SuggestionItem(ulong Id, string When, string Host, string Text, string Rationale,
                                    string Banner, Brush BannerBrush, string Details, bool HasDetails);

/// The persistent AI panel on the right: conversation, suggested commands (in the order they were made)
/// and the Continue / Stop card at the bottom.
public partial class ChatVm : PageVm
{
    readonly Action<string, string> _copyToTerminal;
    string _signature = "";

    public ObservableCollection<object> Items { get; } = new();

    public ModelOption[] ModelOptions { get; } =
        new[] { new ModelOption("Agent's model", "") }
            .Concat(Settings.Current.Ai.Models.Select(m => new ModelOption(m.Name, m.Id)))
            .ToArray();
    [ObservableProperty] ModelOption selectedModel = null!;
    [ObservableProperty] string prompt = "";
    [ObservableProperty] bool hasSuggestions;
    public bool CanAct => Session.CanAct;

    // ---- Continue / Stop after several automatic rounds ----
    [ObservableProperty] bool hasHeld;
    [ObservableProperty] string heldText = "";
    ulong _heldRequestId;

    public ChatVm(Action<string, string> copyToTerminal)
    {
        _copyToTerminal = copyToTerminal;
        SelectedModel = ModelOptions[0];
        Refresh();
    }

    public override void Refresh()
    {
        var id = IncidentId;
        RefreshHeld(id);

        var messages = Conn.Db.MessageView.Iter().Where(m => m.IncidentId == id).ToList();
        var suggestions = Conn.Db.SuggestionView.Iter().Where(s => s.IncidentId == id && !s.Dismissed).ToList();
        var reviews = Conn.Db.ReviewView.Iter().Where(r => r.TargetKind == "suggestion").ToList();

        // Rebuild only when something changed, so the panel doesn't flicker or lose its scroll position.
        var sig = $"{id}|{messages.Count}|{messages.Select(m => m.Id).DefaultIfEmpty().Max()}|" +
                  string.Join(",", suggestions.Select(s => s.Id)) + "|" + reviews.Count;
        if (sig == _signature) return;
        _signature = sig;

        var timeline = new List<(long At, object Item)>();
        foreach (var m in messages)
        {
            bool human = m.AuthorKind == AuthorKind.Human;
            bool system = m.AuthorKind == AuthorKind.System;
            timeline.Add((m.At.MicrosecondsSinceUnixEpoch, new ChatMessageItem(
                Stdb.Ago(m.At), human ? m.Author : system ? "system" : m.Author, m.Text, m.ModelLabel,
                human ? HorizontalAlignment.Right : system ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                human ? Res("AccentSoftBrush") : system ? Brushes.Transparent : Res("Surface2Brush"),
                human ? Res("AccentBrush") : system ? Warning : Res("SubTextBrush"),
                system)));
        }
        foreach (var s in suggestions)
            timeline.Add((s.CreatedAt.MicrosecondsSinceUnixEpoch, Suggestion(s, reviews)));

        Items.Clear();
        foreach (var (_, item) in timeline.OrderBy(t => t.At)) Items.Add(item);
        HasSuggestions = suggestions.Count > 0;
    }

    static SuggestionItem Suggestion(SuggestedCommand s, List<Review> reviews)
    {
        var patterns = RiskPatterns.Check(s.Text);
        var mine = reviews.Where(r => r.TargetId == s.Id).ToList();
        var reviewRisk = mine.Count == 0 ? (Risk?)null : mine.Max(r => r.Risk);

        // Show the STRONGEST warning from either layer. Neither layer blocks.
        bool danger = reviewRisk == Risk.Danger || patterns.Count >= 2;
        bool caution = !danger && (reviewRisk == Risk.Caution || patterns.Count == 1 || reviewRisk == null);
        var banner = danger ? "DANGER: read every character before running this"
                   : caution ? (reviewRisk == null ? "NOT REVIEWED YET: treat with caution" : "CAUTION: check this carefully")
                   : "Reviewer found no concerns. Still written by the AI from attacker-controlled data.";
        var details = string.Join("\n", patterns.Select(p => "Pattern: " + p)
                                   .Concat(mine.Where(r => r.Risk != Risk.None).Select(r => "Reviewer: " + r.Reasons)));
        return new SuggestionItem(s.Id, Stdb.Ago(s.CreatedAt), s.Host, s.Text, s.Rationale, banner,
                                  danger ? Danger : caution ? Warning : Neutral, details, details.Length > 0);
    }

    void RefreshHeld(ulong id)
    {
        // Request ids start at 1, so 0 means "nothing waiting".
        _heldRequestId = Conn.Db.RequestView.Iter()
            .Where(r => r.IncidentId == id && r.Held && !r.Released && (r.Status == RequestStatus.Pending || r.Status == RequestStatus.Ready))
            .OrderByDescending(r => r.Id).Select(r => r.Id).FirstOrDefault();
        HasHeld = _heldRequestId != 0 && Session.CanAct;
        bool ready = Conn.Db.RequestView.Iter().Any(r => r.Id == _heldRequestId && r.Status == RequestStatus.Ready);
        HeldText = ready
            ? "The agent has worked through several rounds on its own. Its next step is ready. Keep going?"
            : "The agent has worked through several rounds on its own and is preparing its next step. Keep going?";
    }

    [RelayCommand]
    void Send()
    {
        if (IncidentId == 0) { Session.ReportError("Open an incident first."); return; }
        if (string.IsNullOrWhiteSpace(Prompt)) return;
        Conn.Reducers.SubmitPrompt(IncidentId, Prompt.Trim(), SelectedModel.Value);
        Prompt = "";
    }

    /// Continue: the prepared answer is applied at once (or as soon as it's ready).
    [RelayCommand]
    void ContinueAgent()
    {
        if (_heldRequestId != 0) Conn.Reducers.ReleaseHeld(_heldRequestId);
    }

    /// Stop: the prepared answer is thrown away unseen.
    [RelayCommand]
    void StopAgent()
    {
        if (_heldRequestId != 0) Conn.Reducers.DiscardHeld(_heldRequestId);
    }

    [RelayCommand] void CopyToTerminal(SuggestionItem s) => _copyToTerminal(s.Host, s.Text);
    [RelayCommand] void Dismiss(SuggestionItem s) => Conn.Reducers.DismissSuggestion(s.Id);

    [RelayCommand]
    void ClearSuggestions()
    {
        if (IncidentId != 0) Conn.Reducers.ClearSuggestions(IncidentId);
    }
}
