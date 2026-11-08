using System.Collections.Concurrent;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace IncidentCommand.Client;

/// Shared console plumbing for the agent, reviewer and executor services.
/// One thread owns the connection: it calls FrameTick and runs queued reducer calls.
/// Slow work (model calls, SSH) runs on background tasks and posts results back via Post().
public sealed class ServiceRunner
{
    readonly ConcurrentQueue<Action<DbConnection>> _queue = new();
    public DbConnection? Conn { get; private set; }
    public bool Ready { get; private set; }
    public string Username { get; private set; } = "";

    public void Post(Action<DbConnection> job) => _queue.Enqueue(job);

    public static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    /// Runs forever. `poll` is called on the connection thread about every 50 ms once subscribed.
    public void Run(string role, Action<DbConnection> onReady, Action<DbConnection> poll)
    {
        var token = ServiceToken.Load(role);
        if (token == null)
        {
            var cmd = role == "executor" ? "dist\\Executor.exe enroll CODE" : $"dist\\Agent.exe enroll {role} CODE";
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"The {role} service isn't enrolled yet (normal after a fresh database or reset-db.bat).");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"  1. In the app, log in as the commander > Settings > create an invite with role '{char.ToUpper(role[0]) + role[1..]}'.");
            Console.WriteLine($"  2. In the IncidentCommand folder run:  {cmd}");
            Console.WriteLine("  3. Start the services again:  start-services.bat");
            Console.WriteLine();
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
            Environment.Exit(2);
        }

        Conn = Stdb.Connect(token,
            onConnect: (c, id, _) =>
            {
                Log($"Connected as {id}");
                Stdb.SubscribeAll(c, () =>
                {
                    foreach (var u in c.Db.MyUser.Iter()) Username = u.Username;
                    if (Username == "") { Log("This identity is not enrolled or is deactivated. Exiting."); Environment.Exit(2); }
                    Ready = true;
                    Log($"Subscribed as {Username}");
                    onReady(c);
                }, e => Log("Subscription error: " + e.Message));
            },
            onError: e => { Log("Connect error: " + e.Message); Environment.Exit(1); },
            onDisconnect: (_, e) => { Log("Disconnected: " + (e?.Message ?? "closed")); Environment.Exit(1); });

        while (true)
        {
            Conn.FrameTick();
            while (_queue.TryDequeue(out var job))
            {
                try { job(Conn); } catch (Exception ex) { Log("Job failed: " + ex.Message); }
            }
            if (Ready)
            {
                try { poll(Conn); } catch (Exception ex) { Log("Poll failed: " + ex.Message); }
            }
            Thread.Sleep(50);
        }
    }

    /// `<exe> enroll <code>`: fresh identity from the local server, claim the invite, save the token with DPAPI.
    public static int Enroll(string role, string code)
    {
        string? token = null, username = null, failure = null;
        bool claimed = false;
        var conn = Stdb.Connect(null,
            onConnect: (c, _, tok) =>
            {
                token = tok;
                c.Db.MyUser.OnInsert += (_, u) => username = u.Username;
                c.SubscriptionBuilder()
                    .OnApplied(_ =>
                    {
                        c.Reducers.OnClaimInvite += (ctx, _) => { failure = Stdb.Failure(ctx); claimed = true; };
                        c.Reducers.ClaimInvite(code);
                    })
                    .AddQuery(q => q.From.MyUser())
                    .Subscribe();
            },
            onError: e => { failure = e.Message; claimed = true; });

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !(claimed && (failure != null || username != null)))
        {
            conn.FrameTick();
            Thread.Sleep(50);
        }
        if (failure != null) { Log("Enrollment failed: " + failure); return 1; }
        if (token == null || username == null) { Log("Enrollment timed out."); return 1; }
        ServiceToken.Save(role, token);
        Log($"Enrolled as {username}. Token saved for '{role}'.");
        conn.Disconnect();
        return 0;
    }
}
