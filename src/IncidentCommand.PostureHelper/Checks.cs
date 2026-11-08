/// Each check: a PowerShell read, a pass test, an optional fix, and an optional undo that restores the
/// value captured before the first fix. Markers: R = restart, N = breaks network connectivity, M = manual.
public sealed record Check(
    string Id, string Name, string Markers, string PassText, string FailText,
    string Read, Func<string, bool> Pass, string? Fix, Func<string, string>? Undo);

public static class Checks
{
    static string Q(string s) => s.Replace("'", "''");   // safe inside a PowerShell single-quoted string

    static IEnumerable<(string Key, string Value)> Pairs(string state) =>
        state.Split(',', StringSplitOptions.RemoveEmptyEntries)
             .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).Select(p => (p[0].Trim(), p[1].Trim()));

    // {{ }} because this goes through string.Format.
    const string Profiles = "(Get-NetFirewallProfile | % {{ \"$($_.Name)={0}\" }}) -join ','";

    static string Reg(string path, string name) =>
        $"$v=(Get-ItemProperty '{path}' -ErrorAction SilentlyContinue).{name}; if($v -eq $null){{'unset'}}else{{$v}}";

    static string SetReg(string path, string name, string value) =>
        $"New-Item -Path '{path}' -Force -ErrorAction SilentlyContinue | Out-Null; Set-ItemProperty -Path '{path}' -Name {name} -Value {value} -Type DWord";

    static string RestoreReg(string path, string name, string before) =>
        before == "unset" ? $"Remove-ItemProperty -Path '{path}' -Name {name} -ErrorAction SilentlyContinue"
                          : SetReg(path, name, Q(before));

    static string ServiceState(string svc) => $"$s=Get-Service {svc}; \"$($s.Status)/$($s.StartType)\"";

    static string RestoreService(string svc, string before)
    {
        var parts = before.Split('/');
        var start = parts.ElementAtOrDefault(1) ?? "Manual";
        var run = parts.ElementAtOrDefault(0) == "Running" ? $"; Start-Service {svc}" : "";
        return $"Set-Service {svc} -StartupType {Q(start)}{run}";
    }

    const string LlmnrKey = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    const string MdnsKey = @"HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
    const string RdpKey = @"HKLM:\System\CurrentControlSet\Control\Terminal Server";
    const string NetBtKey = @"HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces";
    const string EgressRule = "IncidentCommand-Executor-Egress";

    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IncidentCommand", "settings.json");

    public static readonly List<Check> All = new()
    {
        new("firewall-on", "Windows Firewall is on for every network type", "",
            "On for Domain, Private and Public.", "Off for at least one network type.",
            string.Format(Profiles, "$($_.Enabled)"),
            s => !s.Contains("False"),
            "Set-NetFirewallProfile -All -Enabled True",
            before => string.Join("; ", Pairs(before).Select(p => $"Set-NetFirewallProfile -Name {Q(p.Key)} -Enabled {Q(p.Value)}"))),

        new("inbound-blocked", "Nothing on the network can start a connection to this laptop", "",
            "Unsolicited inbound traffic is dropped, including allow rules.", "Unsolicited inbound traffic can get in.",
            string.Format(Profiles, "$($_.DefaultInboundAction)/$($_.AllowInboundRules)"),
            s => Pairs(s).All(p => p.Value == "Block/False"),
            "Set-NetFirewallProfile -All -DefaultInboundAction Block -AllowInboundRules False",
            before => string.Join("; ", Pairs(before).Select(p =>
            {
                var v = p.Value.Split('/');
                return $"Set-NetFirewallProfile -Name {Q(p.Key)} -DefaultInboundAction {Q(v[0])} -AllowInboundRules {Q(v.ElementAtOrDefault(1) ?? "NotConfigured")}";
            }))),

        new("egress-allowlist", "Only the executor may connect out, and only over SSH", "N",
            "Outbound is blocked except Executor.exe on SSH ports.", "Any program can connect out.",
            string.Format(Profiles, "$($_.DefaultOutboundAction)") + $"; if(Get-NetFirewallRule -DisplayName '{EgressRule}' -ErrorAction SilentlyContinue){{',rule=yes'}}",
            s => s.Contains("rule=yes") && Pairs(s).Where(p => p.Key != "rule").All(p => p.Value == "Block"),
            // Executor.exe is published into the same folder as PostureHelper.exe (see scripts/build.ps1).
            $"$exe=Join-Path '{Q(AppContext.BaseDirectory)}' 'Executor.exe'; " +
            $"New-NetFirewallRule -DisplayName '{EgressRule}' -Direction Outbound -Program $exe -Protocol TCP -RemotePort 22,2200-2299 -Action Allow | Out-Null; " +
            "Set-NetFirewallProfile -All -DefaultOutboundAction Block",
            before => $"Remove-NetFirewallRule -DisplayName '{EgressRule}' -ErrorAction SilentlyContinue; " +
                      string.Join("; ", Pairs(before).Where(p => p.Key != "rule").Select(p => $"Set-NetFirewallProfile -Name {Q(p.Key)} -DefaultOutboundAction {Q(p.Value)}"))),

        new("no-listeners", "No program is listening on the network", "",
            "Only loopback listeners.", "Programs are listening on the network (blocked only if the firewall checks pass)",
            "(Get-NetTCPConnection -State Listen | ? { $_.LocalAddress -notin '127.0.0.1','::1' } | % { \"$($_.LocalAddress):$($_.LocalPort)\" } | Sort-Object -Unique) -join ', '",
            s => s.Length == 0, null, null),

        new("stdb-loopback", "SpacetimeDB listens on 127.0.0.1 only", "M",
            "Bound to loopback.", "Not running, or reachable from the network. Restart it with --listen-addr 127.0.0.1:3000",
            "(Get-NetTCPConnection -State Listen -LocalPort 3000 -ErrorAction SilentlyContinue | % LocalAddress | Sort-Object -Unique) -join ','",
            s => s.Length > 0 && s.Split(',').All(a => a is "127.0.0.1" or "::1"), null, null),

        new("model-loopback", "The local AI endpoint is on this laptop", "M",
            "Local (LM Studio) endpoint is loopback. Cloud mode is shown separately in the banner.",
            "The 'Local' AI endpoint in settings.json is not on this laptop: incident data would leave the machine.",
            $"$s=Get-Content '{SettingsPath}' -Raw | ConvertFrom-Json; ([Uri]$s.Ai.Local.BaseUrl).Host",
            s => s is "127.0.0.1" or "localhost" or "[::1]", null, null),

        new("signing-key-locked", "Only you can read SpacetimeDB's signing key", "",
            "Key folder is private to your account.", "Other users can read the key folder (anyone who reads it can forge any login). Set SpacetimeKeyDir in settings.json if this says missing.",
            $"$s=Get-Content '{SettingsPath}' -Raw | ConvertFrom-Json; $d=$s.SpacetimeKeyDir; if(-not $d -or -not (Test-Path $d)){{'missing'}}else{{(Get-Acl $d).Sddl}}",
            s => s != "missing" && !s.Contains(";;;WD)") && !s.Contains(";;;BU)") && !s.Contains(";;;AU)"),
            $"$s=Get-Content '{SettingsPath}' -Raw | ConvertFrom-Json; icacls $s.SpacetimeKeyDir /inheritance:r /grant:r \"$($env:USERNAME):(OI)(CI)F\" /grant:r \"SYSTEM:(OI)(CI)F\" | Out-Null",
            before => $"$s=Get-Content '{SettingsPath}' -Raw | ConvertFrom-Json; $a=Get-Acl $s.SpacetimeKeyDir; $a.SetSecurityDescriptorSddlForm('{Q(before)}'); Set-Acl $s.SpacetimeKeyDir $a"),

        new("smb-off", "File sharing server (SMB) is off", "",
            "SMB server stopped and disabled.", "SMB server can accept connections.",
            ServiceState("LanmanServer"), s => s == "Stopped/Disabled",
            "Stop-Service LanmanServer -Force; Set-Service LanmanServer -StartupType Disabled",
            before => RestoreService("LanmanServer", before)),

        new("rdp-off", "Remote Desktop is off", "",
            "Remote Desktop connections are denied.", "Remote Desktop is allowed.",
            Reg(RdpKey, "fDenyTSConnections"), s => s == "1",
            SetReg(RdpKey, "fDenyTSConnections", "1"), before => RestoreReg(RdpKey, "fDenyTSConnections", before)),

        new("winrm-off", "Remote management (WinRM) is off", "",
            "WinRM stopped and disabled.", "WinRM can accept remote commands.",
            ServiceState("WinRM"), s => s == "Stopped/Disabled",
            "Stop-Service WinRM -Force; Set-Service WinRM -StartupType Disabled",
            before => RestoreService("WinRM", before)),

        new("llmnr-off", "LLMNR name resolution is off (stops credential-stealing tricks)", "",
            "LLMNR disabled by policy.", "LLMNR is on: attackers can answer name lookups and capture credentials.",
            Reg(LlmnrKey, "EnableMulticast"), s => s == "0",
            SetReg(LlmnrKey, "EnableMulticast", "0"), before => RestoreReg(LlmnrKey, "EnableMulticast", before)),

        new("netbios-off", "NetBIOS over TCP/IP is off on every adapter", "R",
            "NetBIOS disabled on all adapters.", "NetBIOS is on for at least one adapter.",
            $"(Get-ChildItem '{NetBtKey}' | % {{ \"$($_.PSChildName)=$((Get-ItemProperty $_.PSPath).NetbiosOptions)\" }}) -join ','",
            s => Pairs(s).All(p => p.Value == "2"),
            $"Get-ChildItem '{NetBtKey}' | % {{ Set-ItemProperty -Path $_.PSPath -Name NetbiosOptions -Value 2 }}",
            before => string.Join("; ", Pairs(before).Select(p =>
                $"Set-ItemProperty -Path '{NetBtKey}\\{Q(p.Key)}' -Name NetbiosOptions -Value {(int.TryParse(p.Value, out var n) ? n : 0)}"))),

        new("mdns-off", "Multicast DNS is off", "R",
            "mDNS disabled.", "mDNS is on: another name-resolution trick attackers use.",
            Reg(MdnsKey, "EnableMDNS"), s => s == "0",
            SetReg(MdnsKey, "EnableMDNS", "0"), before => RestoreReg(MdnsKey, "EnableMDNS", before)),

        new("wireless-off", "Wi-Fi and Bluetooth adapters are off", "N",
            "No wireless adapters enabled.", "Wireless adapters are on: extra ways in.",
            "(Get-NetAdapter | ? { $_.PhysicalMediaType -match '802.11|Bluetooth|Native 802.11' -and $_.Status -ne 'Disabled' } | % Name) -join ','",
            s => s.Length == 0,
            "Get-NetAdapter | ? { $_.PhysicalMediaType -match '802.11|Bluetooth|Native 802.11' } | Disable-NetAdapter -Confirm:$false",
            before => string.Join("; ", before.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => $"Enable-NetAdapter -Name '{Q(n)}' -Confirm:$false"))),

        new("not-domain-joined", "This laptop does not trust the company's domain", "M,R",
            "Not domain-joined.", "Domain-joined: the attacker could push policy to this laptop. Leave the domain by hand.",
            "(Get-CimInstance Win32_ComputerSystem).PartOfDomain", s => s == "False", null, null),

        new("disk-encrypted", "The system disk is encrypted", "M,R",
            "BitLocker protection is on.", "Disk is not encrypted (or this check needs admin rights to read). Enable BitLocker by hand.",
            "(Get-BitLockerVolume -MountPoint C: -ErrorAction Stop).ProtectionStatus", s => s == "On", null, null),
    };
}
