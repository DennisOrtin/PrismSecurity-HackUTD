using SpacetimeDB;
using SpacetimeDB.Types;   // generated namespace (module_bindings)

namespace IncidentCommand.Client;

/// Small helpers around the generated DbConnection.
/// RULE: whoever builds a connection must call conn.FrameTick() regularly on ONE thread
/// (the WPF dispatcher, or the console main loop). No callbacks fire otherwise.
public static class Stdb
{
    public static DbConnection Connect(string? token,
                                       Action<DbConnection, Identity, string> onConnect,
                                       Action<Exception> onError,
                                       Action<DbConnection, Exception?>? onDisconnect = null) =>
        DbConnection.Builder()
            .WithUri(Settings.Current.ServerUri)
            .WithDatabaseName(Settings.Current.DatabaseName)
            .WithToken(token)                      // null => the local server mints a NEW identity
            .OnConnect((conn, identity, tok) => onConnect(conn, identity, tok))
            .OnConnectError(e => onError(e))
            .OnDisconnect((conn, e) => onDisconnect?.Invoke(conn, e))
            .Build();

    /// Subscribe to every role-checked view. Tables themselves are private.
    /// If a view name doesn't resolve, check the generated RemoteTables class for the exact accessor.
    public static void SubscribeAll(DbConnection conn, Action onApplied, Action<Exception> onError) =>
        conn.SubscriptionBuilder()
            .OnApplied(_ => onApplied())
            .OnError((_, e) => onError(e))
            .AddQuery(q => q.From.MyUser())
            .AddQuery(q => q.From.UserView())
            .AddQuery(q => q.From.ConfigView())
            .AddQuery(q => q.From.SshHostView())
            .AddQuery(q => q.From.IncidentView())
            .AddQuery(q => q.From.TimelineView())
            .AddQuery(q => q.From.AssetView())
            .AddQuery(q => q.From.EventView())
            .AddQuery(q => q.From.RequestView())
            .AddQuery(q => q.From.PlanView())
            .AddQuery(q => q.From.ActionView())
            .AddQuery(q => q.From.ReviewView())
            .AddQuery(q => q.From.SuggestionView())
            .AddQuery(q => q.From.MessageView())
            .AddQuery(q => q.From.TerminalCommandView())
            .AddQuery(q => q.From.TerminalOutputView())
            .AddQuery(q => q.From.AuditView())
            .Subscribe();

    /// Calls `changed` whenever any subscribed row changes. Simple and good enough:
    /// screens rebuild their lists from conn.Db.*.Iter() when this fires.
    public static void WatchAll(DbConnection conn, Action changed)
    {
        var db = conn.Db;
        db.MyUser.OnInsert += (_, _) => changed(); db.MyUser.OnDelete += (_, _) => changed();
        db.UserView.OnInsert += (_, _) => changed(); db.UserView.OnDelete += (_, _) => changed();
        db.ConfigView.OnInsert += (_, _) => changed(); db.ConfigView.OnDelete += (_, _) => changed();
        db.SshHostView.OnInsert += (_, _) => changed(); db.SshHostView.OnDelete += (_, _) => changed();
        db.IncidentView.OnInsert += (_, _) => changed(); db.IncidentView.OnDelete += (_, _) => changed();
        db.TimelineView.OnInsert += (_, _) => changed(); db.TimelineView.OnDelete += (_, _) => changed();
        db.AssetView.OnInsert += (_, _) => changed(); db.AssetView.OnDelete += (_, _) => changed();
        db.EventView.OnInsert += (_, _) => changed(); db.EventView.OnDelete += (_, _) => changed();
        db.RequestView.OnInsert += (_, _) => changed(); db.RequestView.OnDelete += (_, _) => changed();
        db.PlanView.OnInsert += (_, _) => changed(); db.PlanView.OnDelete += (_, _) => changed();
        db.ActionView.OnInsert += (_, _) => changed(); db.ActionView.OnDelete += (_, _) => changed();
        db.ReviewView.OnInsert += (_, _) => changed(); db.ReviewView.OnDelete += (_, _) => changed();
        db.SuggestionView.OnInsert += (_, _) => changed(); db.SuggestionView.OnDelete += (_, _) => changed();
        db.MessageView.OnInsert += (_, _) => changed(); db.MessageView.OnDelete += (_, _) => changed();
        db.TerminalCommandView.OnInsert += (_, _) => changed(); db.TerminalCommandView.OnDelete += (_, _) => changed();
        db.TerminalOutputView.OnInsert += (_, _) => changed(); db.TerminalOutputView.OnDelete += (_, _) => changed();
        db.AuditView.OnInsert += (_, _) => changed(); db.AuditView.OnDelete += (_, _) => changed();
    }

    /// null = committed; otherwise a human-readable reason.
    public static string? Failure(ReducerEventContext ctx) => ctx.Event.Status switch
    {
        Status.Committed => null,
        Status.Failed(var message) => message,
        _ => "No result (connection lost or out of energy).",
    };

    public static string Ago(Timestamp t)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(t.MicrosecondsSinceUnixEpoch / 1000).ToLocalTime();
        return at.ToString("HH:mm:ss");
    }
}
