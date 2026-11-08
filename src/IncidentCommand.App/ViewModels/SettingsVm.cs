using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.App.ViewModels;

public sealed record UserRow(string Username, string DisplayName, string Role, string State);
public sealed record HostRow(string Name, string Address, uint Port, string Fingerprint);

public partial class SettingsVm : PageVm
{
    public ObservableCollection<UserRow> Users { get; } = new();
    public ObservableCollection<HostRow> Hosts { get; } = new();
    public Role[] Roles { get; } = Enum.GetValues<Role>();
    public bool IsCommander => Session.IsCommander;

    [ObservableProperty] Role inviteRole = Role.Responder;
    [ObservableProperty] string inviteName = "";
    [ObservableProperty] string inviteResult = "";
    [ObservableProperty] UserRow? selectedUser;

    [ObservableProperty] string hostName = "";
    [ObservableProperty] string hostAddress = "";
    [ObservableProperty] string hostPort = "22";
    [ObservableProperty] string hostFingerprint = "";

    [ObservableProperty] string responderAddress = "";
    [ObservableProperty] bool autoApproveReadOnly;
    [ObservableProperty] string auditStatus = "";

    // ---- Local AI models, one per agent (stored in the database, applied on each agent's next call) ----
    // Model IDs from settings.json. The combo boxes bind SelectedValue to the ID.
    public List<AiModelOption> Models { get; } = Settings.Current.Ai.Models;
    [ObservableProperty] string agentModel = "";
    [ObservableProperty] string reviewerModel = "";
    [ObservableProperty] string parserModel = "";
    bool _loadingAi;

    public string LocalEndpoint => $"LM Studio at {Settings.Current.Ai.BaseUrl}. Add or rename models under Ai.Models in settings.json.";
    public string SettingsFile => Settings.FilePath;

    // ---- Built-in services (agent, reviewer, executor): on unless the commander switches them off ----
    [ObservableProperty] bool agentEnabled = true;
    [ObservableProperty] bool reviewerEnabled = true;
    [ObservableProperty] bool executorEnabled = true;
    [ObservableProperty] bool parserEnabled = true;
    [ObservableProperty] string agentStatus = "";
    [ObservableProperty] string reviewerStatus = "";
    [ObservableProperty] string executorStatus = "";
    [ObservableProperty] string parserStatus = "";
    bool _loadingServices;

    public SettingsVm() => Refresh();

    public override void Refresh()
    {
        Users.Clear();
        foreach (var u in Conn.Db.UserView.Iter().OrderBy(u => u.Username))
            Users.Add(new UserRow(u.Username, u.DisplayName, u.Role.ToString(), u.Active ? "Active" : "Deactivated"));
        Hosts.Clear();
        foreach (var h in Conn.Db.SshHostView.Iter().OrderBy(h => h.Name))
            Hosts.Add(new HostRow(h.Name, h.Address, h.Port,
                string.IsNullOrWhiteSpace(h.PinnedFingerprint) ? "(not pinned yet: pinned on first connect)" : h.PinnedFingerprint));
        foreach (var c in Conn.Db.ConfigView.Iter())
        {
            _loadingServices = true;
            AgentEnabled = c.AgentEnabled;
            ReviewerEnabled = c.ReviewerEnabled;
            ExecutorEnabled = c.ExecutorEnabled;
            ParserEnabled = c.ParserEnabled;
            _loadingServices = false;
            AgentStatus = ServiceStatus(ServiceProvisioner.Get("agent"), c.AgentEnabled);
            ReviewerStatus = ServiceStatus(ServiceProvisioner.Get("reviewer"), c.ReviewerEnabled);
            ParserStatus = ServiceStatus(ServiceProvisioner.Get("parser"), c.ParserEnabled);
            ExecutorStatus = ServiceStatus(ServiceProvisioner.Get("executor"), c.ExecutorEnabled);
            if (string.IsNullOrEmpty(ResponderAddress)) ResponderAddress = c.ResponderAddress;
            AutoApproveReadOnly = c.AutoApproveReadOnly;
            _loadingAuto = true;
            AutoAcceptTimeline = c.AutoAcceptTimeline;
            AutoAcceptFindings = c.AutoAcceptFindings;
            _loadingAuto = false;
            _loadingAi = true;
            // Show the effective model ("" in the database means that agent's default).
            AgentModel = AiNames.Resolve(c.AgentModel, "agent");
            ReviewerModel = AiNames.Resolve(c.ReviewerModel, "reviewer");
            ParserModel = AiNames.Resolve(c.ParserModel, "parser");
            _loadingAi = false;
        }
    }

    [RelayCommand]
    void CreateInvite()
    {
        if (string.IsNullOrWhiteSpace(InviteName)) { Session.ReportError("Who is this invite for?"); return; }
        var code = InviteCodes.Generate();
        Conn.Reducers.CreateInvite(InviteCodes.Hash(code), InviteRole, InviteName.Trim());
        InviteResult = $"Code for {InviteName} ({InviteRole}): {code}\nGive it in person. It works once and expires in 15 minutes.";
        InviteName = "";
    }

    [RelayCommand]
    void Deactivate()
    {
        if (SelectedUser == null) return;
        Conn.Reducers.DeactivateUser(SelectedUser.Username);
    }

    [RelayCommand]
    void SaveHost()
    {
        if (!uint.TryParse(HostPort, out var port)) { Session.ReportError("Port must be a number."); return; }
        Conn.Reducers.SetSshHost(HostName.Trim(), HostAddress.Trim(), port, HostFingerprint.Trim());
        HostName = HostAddress = HostFingerprint = ""; HostPort = "22";
    }

    [RelayCommand] void SaveResponderAddress() => Conn.Reducers.SetResponderAddress(ResponderAddress.Trim());

    // Demo friction: the parser's timeline entries and findings go straight onto the record.
    [ObservableProperty] bool autoAcceptTimeline;
    [ObservableProperty] bool autoAcceptFindings;
    bool _loadingAuto;

    partial void OnAutoAcceptTimelineChanged(bool value) => PushAutoAccept();
    partial void OnAutoAcceptFindingsChanged(bool value) => PushAutoAccept();

    void PushAutoAccept()
    {
        if (_loadingAuto || Session.Conn == null || !Session.IsCommander) return;
        foreach (var c in Conn.Db.ConfigView.Iter())
            if (c.AutoAcceptTimeline == AutoAcceptTimeline && c.AutoAcceptFindings == AutoAcceptFindings) return;
        Conn.Reducers.SetAutoAccept(AutoAcceptTimeline, AutoAcceptFindings);
    }

    partial void OnAutoApproveReadOnlyChanged(bool value)
    {
        var current = Conn.Db.ConfigView.Iter().Select(c => c.AutoApproveReadOnly).FirstOrDefault();
        if (value != current && Session.IsCommander) Conn.Reducers.SetAutoApproveReadOnly(value);
    }

    static string ServiceStatus(ServiceProvisioner.Service s, bool enabled) =>
        !ServiceProvisioner.IsProvisioned(s) ? "Identity not set up yet (happens automatically for the commander)"
        : enabled ? "On" : "OFF: it refuses all work until switched back on";

    partial void OnAgentEnabledChanged(bool value) => PushService(Role.Agent, value);
    partial void OnReviewerEnabledChanged(bool value) => PushService(Role.Reviewer, value);
    partial void OnExecutorEnabledChanged(bool value) => PushService(Role.Executor, value);
    partial void OnParserEnabledChanged(bool value) => PushService(Role.Parser, value);

    void PushService(Role role, bool enabled)
    {
        if (_loadingServices || Session.Conn == null || !Session.IsCommander) return;
        Conn.Reducers.SetServiceEnabled(role, enabled);
    }

    [RelayCommand]
    Task SetUpServices() => ServiceProvisioner.EnsureAllAsync();

    [RelayCommand]
    async Task ReplaceServiceIdentities()
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Replace service identities?",
            Content = "This gives the agent, reviewer, parser and executor brand-new identities and revokes the old ones. " +
                      "Use it if a service token may have been copied. Continue?",
            PrimaryButtonText = "Replace",
            SecondaryButtonText = "Cancel",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger
        };
        if (await dialog.ShowDialogAsync(true, CancellationToken.None) == Wpf.Ui.Controls.MessageBoxResult.Primary)
            await ServiceProvisioner.EnsureAllAsync(force: true);
        Refresh();
    }

    partial void OnAgentModelChanged(string value) => PushAi();
    partial void OnReviewerModelChanged(string value) => PushAi();
    partial void OnParserModelChanged(string value) => PushAi();

    void PushAi()
    {
        if (_loadingAi || Session.Conn == null || !Session.IsCommander) return;
        if (string.IsNullOrEmpty(AgentModel) || string.IsNullOrEmpty(ReviewerModel) || string.IsNullOrEmpty(ParserModel)) return;
        foreach (var c in Conn.Db.ConfigView.Iter())
            if (AiNames.Resolve(c.AgentModel, "agent") == AgentModel &&
                AiNames.Resolve(c.ReviewerModel, "reviewer") == ReviewerModel &&
                AiNames.Resolve(c.ParserModel, "parser") == ParserModel) return;
        Conn.Reducers.SetAiConfig(AgentModel, ReviewerModel, ParserModel);
    }

    /// Recompute the hash chain from GENESIS. Same formula as Module.WriteAudit.
    [RelayCommand]
    void VerifyAudit()
    {
        var prev = "GENESIS";
        ulong count = 0;
        foreach (var e in Conn.Db.AuditView.Iter().OrderBy(e => e.Seq))
        {
            var body = $"{prev}|{e.At.MicrosecondsSinceUnixEpoch}|{e.Username}|{e.Reducer}|{e.PayloadJson}";
            if (e.PrevHash != prev || Hashing.Sha256Hex(body) != e.Hash)
            {
                AuditStatus = $"CHAIN BROKEN at entry {e.Seq} ({e.Reducer} by {e.Username}).";
                return;
            }
            prev = e.Hash;
            count++;
        }
        AuditStatus = $"Chain intact: {count} entries verified.";
    }
}
