using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record EntryRow(ulong Id, string When, string Category, string Text, string Author, bool IsAiDraft, Brush AuthorBrush);
public sealed record ChatRow(string When, string Author, string Text, Brush AuthorBrush);

public partial class TimelineVm : PageVm
{
    public ObservableCollection<EntryRow> Entries { get; } = new();
    public ObservableCollection<ChatRow> Chat { get; } = new();
    public string[] Categories { get; } = { "Detection", "Analysis", "Containment", "Eradication", "Recovery", "Communication", "Note" };

    [ObservableProperty] string prompt = "";
    [ObservableProperty] string entryCategory = "Note";
    [ObservableProperty] string entryText = "";
    public bool CanAct => Session.CanAct;

    public TimelineVm() => Refresh();

    public override void Refresh()
    {
        var id = IncidentId;
        Entries.Clear();
        foreach (var e in Conn.Db.TimelineView.Iter().Where(e => e.IncidentId == id).OrderByDescending(e => e.Id))
            Entries.Add(new EntryRow(e.Id, Stdb.Ago(e.At), e.Category, e.Text,
                e.AuthorKind == AuthorKind.AI ? $"{e.Author} (AI)" : e.Author,
                e.AuthorKind == AuthorKind.AI && !e.Accepted,
                e.AuthorKind == AuthorKind.AI ? Warning : Neutral));

        Chat.Clear();
        foreach (var m in Conn.Db.MessageView.Iter().Where(m => m.IncidentId == id).OrderBy(m => m.Id))
            Chat.Add(new ChatRow(Stdb.Ago(m.At),
                m.AuthorKind == AuthorKind.AI ? $"{m.Author} (AI)" : m.Author, m.Text,
                m.AuthorKind == AuthorKind.AI ? Warning : m.AuthorKind == AuthorKind.System ? Danger : Neutral));
    }

    [RelayCommand]
    void Send()
    {
        if (IncidentId == 0) { Session.ReportError("Open an incident first."); return; }
        if (string.IsNullOrWhiteSpace(Prompt)) return;
        Conn.Reducers.SubmitPrompt(IncidentId, Prompt.Trim());
        Prompt = "";
    }

    [RelayCommand]
    void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(EntryText)) return;
        Conn.Reducers.AddTimelineEntry(IncidentId, EntryCategory, EntryText.Trim());
        EntryText = "";
    }

    [RelayCommand] void Accept(EntryRow row) => Conn.Reducers.ReviewTimelineEntry(row.Id, true);
    [RelayCommand] void Reject(EntryRow row) => Conn.Reducers.ReviewTimelineEntry(row.Id, false);

    [RelayCommand]
    void RequestSummary() =>
        Conn.Reducers.SubmitPrompt(IncidentId,
            "Write the post-incident summary: timeline, what data was affected and how, actions taken and by whom, open risks, and lessons learned.");
}
