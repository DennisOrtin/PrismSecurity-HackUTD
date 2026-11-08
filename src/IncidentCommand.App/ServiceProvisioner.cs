using IncidentCommand.Client;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace IncidentCommand.App;

/// Gives the built-in agent, reviewer and executor their identities automatically, from the commander's
/// session: one-time invite (never shown) -> fresh identity from the local server -> claim -> DPAPI token file.
/// The waiting service processes pick the token up within a couple of seconds.
public static class ServiceProvisioner
{
    public sealed record Service(string Key, Role Role, string DisplayName);

    public static readonly Service[] All =
    {
        new("agent", Role.Agent, "Response agent"),
        new("reviewer", Role.Reviewer, "Reviewer agent"),
        new("parser", Role.Parser, "Parser agent"),
        new("executor", Role.Executor, "Executor"),
    };

    public static Service Get(string key) => All.First(s => s.Key == key);

    static bool _running;

    /// Provision every service that has no valid identity. force = replace them all (revokes the old ones).
    public static async Task EnsureAllAsync(bool force = false)
    {
        if (_running || Session.Conn == null || !Session.IsCommander) return;
        _running = true;
        try
        {
            foreach (var s in All)
                if (force || !IsProvisioned(s)) await ProvisionAsync(s);
        }
        catch (Exception ex)
        {
            Session.ReportError("Setting up the built-in services failed: " + ex.Message);
        }
        finally { _running = false; }
    }

    /// A local token exists AND the database has an enrolled user with that exact identity.
    public static bool IsProvisioned(Service s)
    {
        var id = ServiceToken.LoadIdentity(s.Key);
        if (string.IsNullOrEmpty(id) || ServiceToken.Load(s.Key) == null || Session.Conn == null) return false;
        return Session.Conn.Db.UserView.Iter().Any(u => u.Role == s.Role && u.Identity.ToString() == id);
    }

    static async Task ProvisionAsync(Service s)
    {
        var conn = Session.Conn!;
        var code = InviteCodes.Generate(16);

        // 1. One-time invite for this role, from the commander's own connection.
        var created = new TaskCompletionSource<string?>();
        void OnCreated(ReducerEventContext ctx, string codeHash, Role role, string displayName) =>
            created.TrySetResult(Stdb.Failure(ctx));
        conn.Reducers.OnCreateInvite += OnCreated;
        try
        {
            conn.Reducers.CreateInvite(InviteCodes.Hash(code), s.Role, s.DisplayName);
            var failed = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (failed != null) throw new InvalidOperationException(failed);
        }
        finally { conn.Reducers.OnCreateInvite -= OnCreated; }

        // 2. Fresh identity from the local server, which claims the invite.
        var (fresh, token, identity) = await Session.OpenWithIdentityAsync(null);
        try
        {
            var claimed = new TaskCompletionSource<string?>();
            fresh.Reducers.OnClaimInvite += (ctx, _) => claimed.TrySetResult(Stdb.Failure(ctx));
            fresh.Reducers.ClaimInvite(code);
            var failed = await claimed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (failed != null) throw new InvalidOperationException(failed);
        }
        finally { Session.Close(fresh); }

        // 3. Hand the token to the waiting service process.
        ServiceToken.Save(s.Key, token, identity);
    }
}
