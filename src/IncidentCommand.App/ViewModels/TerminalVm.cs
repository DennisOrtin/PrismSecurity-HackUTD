using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record CommandRow(string When, string Host, string Command, string IssuedBy, string Status, string Output);

/// An AI-suggested command. NEVER executable from here: "Copy to terminal" only pre-fills the box.
public sealed record SuggestionCard(ulong Id, string Host, string Text, string Rationale,
                                    string Banner, Brush BannerBrush, string Details);

public partial class TerminalVm : PageVm
{
    public ObservableCollection<string> Hosts { get; } = new();
    public ObservableCollection<CommandRow> Commands { get; } = new();
    public ObservableCollection<SuggestionCard> Suggestions { get; } = new();

    [ObservableProperty] string? selectedHost;
    [ObservableProperty] string commandText = "";
    public bool CanAct => Session.CanAct;

    public TerminalVm() => Refresh();

    public override void Refresh()
    {
        var hosts = Conn.Db.SshHostView.Iter().Select(h => h.Name).OrderBy(n => n).ToList();
        if (!hosts.SequenceEqual(Hosts)) { Hosts.Clear(); foreach (var h in hosts) Hosts.Add(h); }

        var outputs = Conn.Db.TerminalOutputView.Iter().ToList();
        Commands.Clear();
        foreach (var c in Conn.Db.TerminalCommandView.Iter().OrderByDescending(c => c.Id).Take(20))
        {
            var text = string.Concat(outputs.Where(o => o.CommandId == c.Id).OrderBy(o => o.Id).Select(o => o.Chunk));
            var status = c.Status is TerminalStatus.Done or TerminalStatus.Failed ? $"{c.Status} (exit {c.ExitCode})" : c.Status.ToString();
            Commands.Add(new CommandRow(Stdb.Ago(c.At), c.Host, c.Command, c.IssuedBy, status, text));
        }

        var reviews = Conn.Db.ReviewView.Iter().Where(r => r.TargetKind == "suggestion").ToList();
        Suggestions.Clear();
        foreach (var s in Conn.Db.SuggestionView.Iter().Where(s => s.IncidentId == IncidentId).OrderByDescending(s => s.Id))
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

            Suggestions.Add(new SuggestionCard(s.Id, s.Host, s.Text, s.Rationale, banner,
                danger ? Danger : caution ? Warning : Neutral, details));
        }
    }

    [RelayCommand]
    void Run()
    {
        if (SelectedHost == null || string.IsNullOrWhiteSpace(CommandText)) return;
        Conn.Reducers.RunTerminalCommand(SelectedHost, CommandText.Trim());
        CommandText = "";
    }

    [RelayCommand]
    void CopyToTerminal(SuggestionCard card)
    {
        SelectedHost = card.Host;
        CommandText = card.Text;   // pre-fill only; the human presses Run
    }
}
