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
