using System.Text.RegularExpressions;
using SpacetimeDB.Types;

namespace IncidentCommand.Executor;

/// The fixed command template for every catalog kind. The module already validated the parameters;
/// we check again here (defense in depth) before anything reaches a shell.
public static class Templates
{
    static readonly Regex SafeValue = new(@"^[A-Za-z0-9_./:-]{1,200}$");

    public static readonly Dictionary<string, string> LogPaths = new()
    {
        ["auth"] = "/var/log/auth.log",
        ["syslog"] = "/var/log/syslog",
        ["app"] = "/var/log/app.log",
    };

    public static string Build(string kind, List<Param> ps)
    {
        var p = ps.ToDictionary(x => x.Key, x => x.Value);
        foreach (var v in p.Values)
            if (!SafeValue.IsMatch(v)) throw new InvalidOperationException($"Refusing unsafe parameter value '{v}'.");

        return kind switch
        {
            "collect_logs" => $"tail -n {int.Parse(p["lines"])} {LogPaths[p["log"]]}",
            "follow_logs" => $"tail -n 0 -F {LogPaths[p["log"]]}",
            "list_processes" => "ps -eo pid,user,etime,cmd --sort=start_time",
            "list_connections" => "ss -tunp",
            // "not port 22" leaves out the executor's own SSH sessions, so only real traffic shows.
            "capture_traffic" => $"timeout {int.Parse(p["seconds"])} tcpdump -i any -nn -c 5000 -w /tmp/cap.pcap not port 22 2>/dev/null; " +
                                 "echo \"$(tcpdump -nn -r /tmp/cap.pcap 2>/dev/null | wc -l) packets (SSH management traffic excluded)\"; " +
                                 "tcpdump -nn -r /tmp/cap.pcap 2>/dev/null | head -n 200",
            // Containment check: can this host still open a connection to that address?
            "check_egress" => $"if timeout 3 bash -c 'echo > /dev/tcp/{p["ip"]}/{int.Parse(p["port"])}' 2>/dev/null; " +
                              $"then echo 'REACHABLE: {p["ip"]}:{p["port"]} is still reachable from this host'; " +
                              $"else echo 'BLOCKED: {p["ip"]}:{p["port"]} cannot be reached from this host'; fi",
            // Eradication: move a file out of the way (kept for forensics), not delete it.
            "quarantine_file" => $"test -e {p["path"]} || {{ echo 'not found: {p["path"]}'; exit 1; }}; " +
                                 $"mkdir -p /var/quarantine && chmod 700 /var/quarantine && " +
                                 $"mv {p["path"]} /var/quarantine/$(echo {p["path"]} | tr / _) && " +
                                 $"echo 'quarantined {p["path"]} -> /var/quarantine/'$(echo {p["path"]} | tr / _)",
            // Recovery: put a directory back from the backup taken before the attack (/srv/.backup in the sim).
            "restore_from_backup" => $"b=/srv/.backup/$(basename {p["dir"]}); test -d $b || {{ echo \"no backup at $b\"; exit 1; }}; " +
                                     $"rm -rf {p["dir"]} && cp -a $b {p["dir"]} && echo 'restored {p["dir"]} from backup:' && ls -la {p["dir"]}",
            "hash_files" => $"find {p["dir"]} -maxdepth 3 -type f -exec sha256sum {{}} + | head -n 500",
            "kill_process" => $"kill -9 {int.Parse(p["pid"])}",
            "lock_account" => $"usermod -L {p["username"]} && (pkill -KILL -u {p["username"]} || true)",
            "block_ip" => $"iptables -I OUTPUT -d {p["ip"]} -j DROP && iptables -I INPUT -s {p["ip"]} -j DROP",
            // Accept rules first, THEN drop policies, so our own SSH session survives.
            // In the Docker sim the management channel is SSH on port 22.
            "isolate_host" => "iptables -A INPUT -i lo -j ACCEPT && iptables -A OUTPUT -o lo -j ACCEPT && " +
                              "iptables -A INPUT -p tcp --dport 22 -j ACCEPT && " +
                              "iptables -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT && " +
                              "iptables -P INPUT DROP && iptables -P OUTPUT DROP && iptables -P FORWARD DROP && echo isolated",
            // In a container, stopping PID 1 stops the 'host'. On a real server: shutdown -h now.
            "shutdown_host" => "echo shutting down; (sleep 1; kill -TERM 1) &",
            _ => throw new InvalidOperationException($"No template for '{kind}'."),
        };
    }

    /// Crude severity for streamed log lines: 3+ wakes the agent automatically.
    public static byte Severity(string line)
    {
        var l = line.ToLowerInvariant();
        // The implant's routine beacons arrive every 10 s: visible as events, but they shouldn't wake the agent each time.
        if (l.Contains("beacon ok")) return 2;
        if (l.Contains("beacon failed")) return 1;
        if (l.Contains(".locked") || l.Contains("ransom") || l.Contains("ai assistant") || l.Contains("ignore previous")) return 4;
        if (l.Contains("accepted") || l.Contains("curl") || l.Contains("upload") || l.Contains("sudo")) return 3;
        if (l.Contains("failed password") || l.Contains("invalid user")) return 2;
        return 1;
    }
}
