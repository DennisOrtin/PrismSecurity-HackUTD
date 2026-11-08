using System.Text.RegularExpressions;

namespace IncidentCommand.Client;

/// Plain-language labels for the action catalog (the module is the authority on what's allowed).
public static class CatalogText
{
    public static string Title(string kind, string host) => kind switch
    {
        "collect_logs" => $"Collect logs from {host}",
        "follow_logs" => $"Follow live logs on {host}",
        "list_processes" => $"List processes on {host}",
        "list_connections" => $"List network connections on {host}",
        "capture_traffic" => $"Capture traffic on {host}",
        "hash_files" => $"Hash files on {host}",
        "kill_process" => $"Kill a process on {host}",
        "lock_account" => $"Lock an account on {host}",
        "block_ip" => $"Block an IP address on {host}",
        "isolate_host" => $"Isolate {host} from the network",
        "shutdown_host" => $"Shut down {host}",
        "check_egress" => $"Check whether {host} can still reach an address",
        "quarantine_file" => $"Quarantine a file on {host}",
        "restore_from_backup" => $"Restore data on {host} from backup",
        _ => $"{kind} on {host}",
    };
}

/// Deterministic red flags for AI-suggested commands. Shown alongside the reviewer agent's verdict;
/// the UI shows the strongest warning from either layer. Never blocks anything.
public static class RiskPatterns
{
    static readonly (Regex Pattern, string Reason)[] Rules =
    {
        (new(@"\|\s*(ba|z|k)?sh\b"), "Pipes output into a shell"),
        (new(@"\b(curl|wget)\b.*\|"), "Downloads something and pipes it onward"),
        (new(@"\brm\s+-[a-z]*[rf]"), "Recursive or forced delete"),
        (new(@"\b(dd|mkfs(\.\w+)?|shred)\b"), "Can destroy disks or data"),
        (new(@">\s*/(etc|dev|boot|bin|usr)/"), "Writes into a system directory"),
        (new(@"\b(base64|xxd)\b.*-d|[A-Za-z0-9+/]{40,}={0,2}"), "Contains or decodes an encoded blob"),
        (new(@"\beval\b|`|\$\("), "Runs dynamically built code"),
        (new(@";|&&|\|\|"), "Chains several commands"),
        (new(@"(^|\s)>>?\s*\S"), "Redirects output into a file"),
        (new(@"\b(chmod|chown)\b.*\s/"), "Changes permissions on system paths"),
        (new(@"\b(nc|ncat|socat)\b"), "Opens raw network connections"),
    };

    public static List<string> Check(string command) =>
        Rules.Where(r => r.Pattern.IsMatch(command)).Select(r => r.Reason).ToList();
}
