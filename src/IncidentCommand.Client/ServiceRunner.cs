using System.Collections.Concurrent;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace IncidentCommand.Client;

/// Shared console plumbing for the agent, reviewer and executor services.
/// One thread owns the connection: it calls FrameTick and runs queued reducer calls.
/// Slow work (model calls, SSH) runs on background tasks and posts results back via Post().
///
/// The service never needs manual setup: it waits until the commander's app has provisioned its token,
/// reconnects if the database restarts, picks up a re-provisioned token, and idles while disabled.
public sealed class ServiceRunner
{
    readonly ConcurrentQueue<Action<DbConnection>> _queue = new();
    public DbConnection? Conn { get; private set; }
    public bool Ready { get; private set; }
    public string Username { get; private set; } = "";

    public void Post(Action<DbConnection> job) => _queue.Enqueue(job);

    public static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    enum Outcome { Disconnected, Unrecognized, TokenChanged }

    /// Runs forever. `poll` is called on the connection thread about every 50 ms while enabled.
    public void Run(string role, Action<DbConnection> onReady, Action<DbConnection> poll)
    {
        while (true)
        {
            var token = WaitForToken(role);
            var stamp = ServiceToken.Stamp(role);
            var outcome = RunOnce(role, token, stamp, onReady, poll);
            Ready = false;
            Conn = null;

            switch (outcome)
            {
                case Outcome.Unrecognized:
                    Log("The database doesn't know this identity (new database?). Waiting for the commander's app to re-provision it.");
                    while (ServiceToken.Stamp(role) == stamp) Thread.Sleep(2000);
                    break;
                case Outcome.TokenChanged:
                    Log("A new identity was provisioned. Reconnecting with it.");
                    break;
                default:
                    Log("Lost the database connection. Retrying in 3 seconds.");
                    Thread.Sleep(3000);
                    break;
            }
        }
    }

    static string WaitForToken(string role)
    {
        bool said = false;
        while (true)
        {
            var token = ServiceToken.Load(role);
            if (token != null) return token;
            if (!said)
            {
                Log($"No identity for the {role} yet. It is created automatically when the incident commander logs in to the app. Waiting…");
                said = true;
            }
            Thread.Sleep(2000);
        }
    }

    Outcome RunOnce(string role, string token, DateTime stamp, Action<DbConnection> onReady, Action<DbConnection> poll)
    {
        Outcome? ended = null;
        bool known = false, wasActive = true, announcedState = false;
        var lastTokenCheck = DateTime.UtcNow;

        var conn = Stdb.Connect(token,
            onConnect: (c, id, _) =>
            {
                Log($"Connected as {id}");
                Stdb.SubscribeAll(c, () =>
                {
                    foreach (var u in c.Db.MyUser.Iter()) { Username = u.Username; known = true; }
                    if (!known) { ended = Outcome.Unrecognized; return; }
                    Ready = true;
                    Log($"Subscribed as {Username}");
                    onReady(c);
                }, e => Log("Subscription error: " + e.Message));
            },
            onError: e => { Log("Connect error: " + e.Message); ended = Outcome.Disconnected; },
            onDisconnect: (_, e) => { Log("Disconnected: " + (e?.Message ?? "closed")); ended = Outcome.Disconnected; });
        Conn = conn;

        while (ended == null)
        {
            conn.FrameTick();

            // Re-provisioned by the commander's app? Switch to the new identity.
            if ((DateTime.UtcNow - lastTokenCheck).TotalSeconds > 2)
            {
                lastTokenCheck = DateTime.UtcNow;
                if (ServiceToken.Stamp(role) != stamp) { ended = Outcome.TokenChanged; break; }
            }

            while (_queue.TryDequeue(out var job))
            {
                try { job(conn); } catch (Exception ex) { Log("Job failed: " + ex.Message); }
            }

            if (Ready)
            {
                bool active = false, present = false;
                foreach (var u in conn.Db.MyUser.Iter()) { present = true; active = u.Active; }
                if (!present) { ended = Outcome.Unrecognized; break; }
                if (active != wasActive || !announcedState)
                {
                    Log(active ? "Enabled. Working." : "Disabled by the incident commander. Idle until switched back on.");
                    wasActive = active;
                    announcedState = true;
                }
                if (active)
                {
                    try { poll(conn); } catch (Exception ex) { Log("Poll failed: " + ex.Message); }
                }
            }
            Thread.Sleep(50);
        }

        try { conn.Disconnect(); } catch { /* already closed */ }
        return ended ?? Outcome.Disconnected;
    }

    /// Manual fallback: `<exe> enroll [role] <code>` with an invite code from Settings. Normally not needed.
    public static int Enroll(string role, string code)
    {
        string? token = null, username = null, failure = null, identity = null;
        bool claimed = false;
        var conn = Stdb.Connect(null,
            onConnect: (c, id, tok) =>
            {
                token = tok;
                identity = id.ToString();
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
        ServiceToken.Save(role, token, identity ?? "");
        Log($"Enrolled as {username}. Token saved for '{role}'.");
        conn.Disconnect();
        return 0;
    }
}
