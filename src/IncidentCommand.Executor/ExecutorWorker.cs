using IncidentCommand.Client;
using Renci.SshNet;
using SpacetimeDB.Types;

namespace IncidentCommand.Executor;

/// Subscribes to approved actions and queued terminal commands. Runs each over SSH, with the
/// host key pinned, and writes results back. It runs the STORED proposal, never anything else.
public sealed class ExecutorWorker
{
    readonly ServiceRunner _runner;
    readonly HashSet<ulong> _startedActions = new();
    readonly HashSet<ulong> _startedCommands = new();

    public ExecutorWorker(ServiceRunner runner) => _runner = runner;

    /// Only begin SSH work once the module has accepted Start*, so two executors can never both run it.
    public void Wire(DbConnection conn)
    {
        conn.Reducers.OnStartAction += (ctx, actionId) =>
        {
            if (Stdb.Failure(ctx) is { } f) { ServiceRunner.Log($"StartAction {actionId} refused: {f}"); return; }
            foreach (var a in ctx.Db.ActionView.Iter().Where(a => a.Id == actionId)) Launch(a);
        };
        conn.Reducers.OnStartTerminalCommand += (ctx, commandId) =>
        {
            if (Stdb.Failure(ctx) is { } f) { ServiceRunner.Log($"StartTerminalCommand {commandId} refused: {f}"); return; }
            foreach (var c in ctx.Db.TerminalCommandView.Iter().Where(c => c.Id == commandId)) LaunchTerminal(c);
        };
    }

    public void Poll(DbConnection conn)
    {
        foreach (var a in conn.Db.ActionView.Iter().Where(a => a.Status == ActionStatus.Approved).OrderBy(a => a.Id))
            if (_startedActions.Add(a.Id)) conn.Reducers.StartAction(a.Id);

        foreach (var c in conn.Db.TerminalCommandView.Iter().Where(c => c.Status == TerminalStatus.Queued).OrderBy(c => c.Id))
            if (_startedCommands.Add(c.Id)) conn.Reducers.StartTerminalCommand(c.Id);
    }

    // ---------- Catalog actions ----------

    void Launch(ResponseAction a)
    {
        var host = HostFor(a.Host);
        if (host == null) { _runner.Post(c => c.Reducers.FinishAction(a.Id, false, "Unknown host.")); return; }
        string command;
        try { command = Templates.Build(a.Kind, a.Params); }
        catch (Exception ex) { _runner.Post(c => c.Reducers.FinishAction(a.Id, false, ex.Message)); return; }

        ServiceRunner.Log($"Running action {a.Id} ({a.Kind}) on {a.Host}, approved by {a.ApprovedBy}");

        if (a.Kind == "follow_logs") { Follow(a, host.Value, command); return; }

        Task.Run(() =>
        {
            var (ok, output) = Exec(host.Value, command, TimeSpan.FromSeconds(180));
            _runner.Post(c => c.Reducers.FinishAction(a.Id, ok, output));
        });
    }

    /// Long-running: mark the action done right away, then stream each new log line as an event.
    void Follow(ResponseAction a, HostInfo host, string command)
    {
        var source = "log:" + a.Params.First(p => p.Key == "log").Value;
        _runner.Post(c => c.Reducers.FinishAction(a.Id, true, $"Now following {source} on {a.Host}. New lines appear as events."));
        Task.Run(() =>
        {
            try
            {
                using var client = Connect(host);
                using var cmd = client.CreateCommand(command);
                var result = cmd.BeginExecute();
                using var reader = new StreamReader(cmd.OutputStream);
                while (!result.IsCompleted)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) { Thread.Sleep(100); continue; }
                    var sev = Templates.Severity(line);
                    _runner.Post(c => c.Reducers.RecordEvent(a.IncidentId, a.Host, source, line, sev));
                }
            }
            catch (Exception ex) { ServiceRunner.Log($"Follow on {a.Host} ended: {ex.Message}"); }
        });
    }

    // ---------- Human terminal ----------

    void LaunchTerminal(TerminalCommand c)
    {
        var host = HostFor(c.Host);
        if (host == null) { _runner.Post(x => x.Reducers.FinishTerminalCommand(c.Id, -1, true)); return; }
        ServiceRunner.Log($"Terminal {c.Id} on {c.Host} by {c.IssuedBy}: {c.Command}");
        Task.Run(() =>
        {
            int exit = -1;
            bool failed = false;
            try
            {
                using var client = Connect(host.Value);
                using var cmd = client.CreateCommand(c.Command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(120);
                var output = cmd.Execute() + cmd.Error;
                exit = (int?)cmd.ExitStatus ?? -1;
                foreach (var chunk in Chunks(output, 4000))
                    _runner.Post(x => x.Reducers.AppendTerminalOutput(c.Id, chunk));
            }
            catch (Exception ex)
            {
                failed = true;
                _runner.Post(x => x.Reducers.AppendTerminalOutput(c.Id, "[executor] " + ex.Message));
            }
            _runner.Post(x => x.Reducers.FinishTerminalCommand(c.Id, exit, failed));
        });
    }

    // ---------- SSH ----------

    readonly record struct HostInfo(string Name, string Address, int Port, string Fingerprint);

    HostInfo? HostFor(string name)
    {
        foreach (var h in _runner.Conn!.Db.SshHostView.Iter().Where(h => h.Name == name))
            return new HostInfo(h.Name, h.Address, (int)h.Port, h.PinnedFingerprint);
        return null;
    }

    static string Norm(string fp) => fp.Replace("SHA256:", "").TrimEnd('=').Trim();

    static SshClient Connect(HostInfo h)
    {
        var s = Settings.Current;
        var client = new SshClient(new ConnectionInfo(h.Address, h.Port, s.SshUser,
            new PrivateKeyAuthenticationMethod(s.SshUser, new PrivateKeyFile(s.SshPrivateKeyPath))));
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = Norm(e.FingerPrintSHA256) == Norm(h.Fingerprint);
            if (!e.CanTrust)
                ServiceRunner.Log($"HOST KEY MISMATCH on {h.Name}: got {e.FingerPrintSHA256}, pinned {h.Fingerprint}. Possible impersonation. Refusing.");
        };
        client.Connect();
        return client;
    }

    static (bool Ok, string Output) Exec(HostInfo h, string command, TimeSpan timeout)
    {
        try
        {
            using var client = Connect(h);
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout;
            var output = cmd.Execute() + cmd.Error;
            var exit = (int?)cmd.ExitStatus ?? -1;
            return (exit == 0, output.Length == 0 ? $"(no output, exit {exit})" : output);
        }
        catch (Exception ex) { return (false, "[executor] " + ex.Message); }
    }

    static IEnumerable<string> Chunks(string s, int size)
    {
        if (s.Length == 0) { yield return "(no output)"; yield break; }
        for (int i = 0; i < s.Length; i += size) yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}
