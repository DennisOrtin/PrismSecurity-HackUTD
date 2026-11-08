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

    // ---- AI provider and models (stored in the database, applied by the agent on its next call) ----
    public AiProvider[] Providers { get; } = Enum.GetValues<AiProvider>();
    public AiModel[] Models { get; } = Enum.GetValues<AiModel>();
    [ObservableProperty] AiProvider aiProvider;
    [ObservableProperty] AiModel agentModel;
    [ObservableProperty] AiModel reviewerModel;
    [ObservableProperty] bool isCloud;
    bool _loadingAi;

    public string LocalEndpoint => $"{Settings.Current.Ai.Local.BaseUrl}   Nano: {Settings.Current.Ai.Local.NanoModel}   Lightning: {Settings.Current.Ai.Local.LightningModel}";
    public string CloudEndpoint => $"{Settings.Current.Ai.Cloud.BaseUrl}   Nano: {Settings.Current.Ai.Cloud.NanoModel}   Lightning: {Settings.Current.Ai.Cloud.LightningModel}";
    public string SettingsFile => Settings.FilePath;

    public SettingsVm() => Refresh();

    public override void Refresh()
    {
        Users.Clear();
        foreach (var u in Conn.Db.UserView.Iter().OrderBy(u => u.Username))
            Users.Add(new UserRow(u.Username, u.DisplayName, u.Role.ToString(), u.Active ? "Active" : "Deactivated"));
        Hosts.Clear();
        foreach (var h in Conn.Db.SshHostView.Iter().OrderBy(h => h.Name))
            Hosts.Add(new HostRow(h.Name, h.Address, h.Port, h.PinnedFingerprint));
        foreach (var c in Conn.Db.ConfigView.Iter())
        {
            if (string.IsNullOrEmpty(ResponderAddress)) ResponderAddress = c.ResponderAddress;
            AutoApproveReadOnly = c.AutoApproveReadOnly;
            _loadingAi = true;
            AiProvider = c.AiProvider;
            AgentModel = c.AgentModel;
            ReviewerModel = c.ReviewerModel;
            IsCloud = c.AiProvider == AiProvider.Cloud;
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

    partial void OnAutoApproveReadOnlyChanged(bool value)
    {
        var current = Conn.Db.ConfigView.Iter().Select(c => c.AutoApproveReadOnly).FirstOrDefault();
        if (value != current && Session.IsCommander) Conn.Reducers.SetAutoApproveReadOnly(value);
    }

    partial void OnAiProviderChanged(AiProvider value) => PushAi();
    partial void OnAgentModelChanged(AiModel value) => PushAi();
    partial void OnReviewerModelChanged(AiModel value) => PushAi();

    void PushAi()
    {
        if (_loadingAi || Session.Conn == null || !Session.IsCommander) return;
        foreach (var c in Conn.Db.ConfigView.Iter())
            if (c.AiProvider == AiProvider && c.AgentModel == AgentModel && c.ReviewerModel == ReviewerModel) return;
        if (AiProvider == AiProvider.Cloud && !Conn.Db.ConfigView.Iter().Any(c => c.AiProvider == AiProvider.Cloud))
        {
            var ok = System.Windows.MessageBox.Show(
                "Cloud mode sends incident data (logs, scope, actions) to the Brev endpoint over the internet.\n\n" +
                "Use it only for development and testing. Switch to cloud?",
                "Send incident data off this laptop?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (ok != System.Windows.MessageBoxResult.Yes) { Refresh(); return; }
        }
        Conn.Reducers.SetAiConfig(AiProvider, AgentModel, ReviewerModel);
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
