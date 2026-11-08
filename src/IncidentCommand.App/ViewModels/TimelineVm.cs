using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record EntryRow(ulong Id, string When, string Category, string Text, string Author, bool IsAiDraft, Brush AuthorBrush,
                              string AcceptLabel);
public sealed record ChatRow(string When, string Author, string Text, Brush AuthorBrush, string ModelLabel);
public sealed record ModelOption(string Label, string Value);   // Value = LM Studio model ID, "" = default

public partial class TimelineVm : PageVm
{
    public ObservableCollection<EntryRow> Entries { get; } = new();
    public ObservableCollection<ChatRow> Chat { get; } = new();
    // Choosing a phase here also MOVES the incident to that phase (same as the Overview buttons).
    public string[] Categories { get; } =
        { "Note", "Communication", "Detected", "Analysis", "Containment", "Eradication", "Recovery", "Closed" };

    public ModelOption[] ModelOptions { get; } =
        new[] { new ModelOption("Agent's model (set in Settings)", "") }
            .Concat(Settings.Current.Ai.Models.Select(m => new ModelOption(m.Name, m.Id)))
            .ToArray();
    [ObservableProperty] ModelOption selectedModel = null!;

    [ObservableProperty] string prompt = "";
    [ObservableProperty] string entryCategory = "Note";
    [ObservableProperty] string entryText = "";
    public bool CanAct => Session.CanAct;

    public TimelineVm()
    {
        SelectedModel = ModelOptions[0];
        Refresh();
    }

    public override void Refresh()
    {
        var id = IncidentId;
        var currentPhase = Conn.Db.IncidentView.Iter().Where(i => i.Id == id).Select(i => i.Phase).FirstOrDefault();
        Entries.Clear();
        foreach (var e in Conn.Db.TimelineView.Iter().Where(e => e.IncidentId == id).OrderByDescending(e => e.Id))
        {
            // An AI entry filed under a different phase is a proposed phase change: say so on the button.
            bool movesPhase = Enum.TryParse<Phase>(e.Category, out var p) && p != currentPhase;
            Entries.Add(new EntryRow(e.Id, Stdb.Ago(e.At), e.Category, e.Text,
                e.AuthorKind == AuthorKind.Assistant ? $"{e.Author} (AI)" : e.Author,
                e.AuthorKind == AuthorKind.Assistant && !e.Accepted,
                e.AuthorKind == AuthorKind.Assistant ? Warning : Neutral,
                movesPhase ? $"Accept and move incident to {p}" : "Accept"));
        }

        Chat.Clear();
        foreach (var m in Conn.Db.MessageView.Iter().Where(m => m.IncidentId == id).OrderBy(m => m.Id))
            Chat.Add(new ChatRow(Stdb.Ago(m.At),
                m.AuthorKind == AuthorKind.Assistant ? $"{m.Author} (AI)" : m.Author, m.Text,
                m.AuthorKind == AuthorKind.Assistant ? Warning : m.AuthorKind == AuthorKind.System ? Danger : Neutral,
                m.ModelLabel));
    }

    [RelayCommand]
    void Send()
    {
        if (IncidentId == 0) { Session.ReportError("Open an incident first."); return; }
        if (string.IsNullOrWhiteSpace(Prompt)) return;
        Conn.Reducers.SubmitPrompt(IncidentId, Prompt.Trim(), SelectedModel.Value);
        Prompt = "";
    }

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

    [RelayCommand] void Accept(EntryRow row) => Conn.Reducers.ReviewTimelineEntry(row.Id, true);
    [RelayCommand] void Reject(EntryRow row) => Conn.Reducers.ReviewTimelineEntry(row.Id, false);

    [RelayCommand]
    void RequestSummary() =>
        Conn.Reducers.SubmitPrompt(IncidentId,
            "Write the post-incident summary: timeline, what data was affected and how, actions taken and by whom, open risks, and lessons learned.",
            SelectedModel.Value);
}
