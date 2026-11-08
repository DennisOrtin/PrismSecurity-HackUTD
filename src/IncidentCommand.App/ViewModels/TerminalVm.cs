using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

/// One command and its output, shown like a terminal: prompt line, then the output underneath.
public sealed record CommandRow(string When, string Host, string Command, string IssuedBy, string Status, Brush StatusBrush,
                                string Output, bool HasOutput, bool FromAi);

/// The human-only terminal at the bottom of the screen. Every command runs on the chosen host over SSH
/// (through the executor) and is recorded with the user's name and the current incident.
public partial class TerminalVm : PageVm
{
    string _signature = "";

    public ObservableCollection<string> Hosts { get; } = new();
    public ObservableCollection<CommandRow> Commands { get; } = new();

    [ObservableProperty] string? selectedHost;
    [ObservableProperty] string commandText = "";
    [ObservableProperty] bool hasCommands;
    public bool CanAct => Session.CanAct;

    /// Raised when the chat copies a suggestion in, so the view can focus the input line.
    public event Action? Prefilled;

    public TerminalVm() => Refresh();

    public override void Refresh()
    {
        var hosts = Conn.Db.SshHostView.Iter().Select(h => h.Name).OrderBy(n => n).ToList();
        if (!hosts.SequenceEqual(Hosts))
        {
            Hosts.Clear();
            foreach (var h in hosts) Hosts.Add(h);
            if (SelectedHost == null || !hosts.Contains(SelectedHost)) SelectedHost = hosts.FirstOrDefault();
        }

        var id = IncidentId;
        var commands = Conn.Db.TerminalCommandView.Iter().Where(c => c.IncidentId == id).OrderBy(c => c.Id).ToList();
        var ids = commands.Select(c => c.Id).ToHashSet();
        var outputs = Conn.Db.TerminalOutputView.Iter().Where(o => ids.Contains(o.CommandId)).ToList();

        // Rebuild only when something changed (keeps the scroll position steady).
        var sig = $"{id}|{string.Join(",", commands.Select(c => $"{c.Id}:{c.Status}"))}|{outputs.Count}";
        if (sig == _signature) return;
        _signature = sig;

        Commands.Clear();
        foreach (var c in commands.TakeLast(40))   // only this incident's commands: a new incident starts empty
        {
            var text = string.Concat(outputs.Where(o => o.CommandId == c.Id).OrderBy(o => o.Id).Select(o => o.Chunk)).TrimEnd();
            var (status, brush) = c.Status switch
            {
                TerminalStatus.Done when c.ExitCode == 0 => ("exit 0", Ok),
                TerminalStatus.Done => ($"exit {c.ExitCode}", Warning),
                TerminalStatus.Failed => ($"failed (exit {c.ExitCode})", Danger),
                TerminalStatus.Running => ("running…", Res("AccentBrush")),
                _ => ("queued…", Neutral),
            };
            Commands.Add(new CommandRow(Stdb.Ago(c.At), c.Host, c.Command, c.IssuedBy, status, brush, text, text.Length > 0, c.SuggestionId != 0));
        }
        HasCommands = Commands.Count > 0;
    }

    [RelayCommand]
    void Run()
    {
        if (SelectedHost == null) { Session.ReportError("Pick a host first. Hosts are added under Settings."); return; }
        if (string.IsNullOrWhiteSpace(CommandText)) return;
        if (IncidentId == 0) { Session.ReportError("Open an incident first: terminal commands are recorded against it."); return; }
        Conn.Reducers.RunTerminalCommand(IncidentId, SelectedHost, CommandText.Trim());
        CommandText = "";
    }

    /// From the chat's "Copy to terminal": fills in the line, never runs it.
    public void Prefill(string host, string command)
    {
        if (Hosts.Contains(host)) SelectedHost = host;
        CommandText = command;
        Prefilled?.Invoke();
    }
}
