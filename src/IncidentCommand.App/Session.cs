using System.Windows.Threading;
using IncidentCommand.Client;
using SpacetimeDB;
using SpacetimeDB.Types;

namespace IncidentCommand.App;

/// The logged-in user's live connection. All SpacetimeDB work happens on the UI thread:
/// a DispatcherTimer calls FrameTick, so row callbacks can touch UI collections directly.
public static class Session
{
    static readonly List<DbConnection> Ticking = new();
    static DispatcherTimer? _pump;
    static bool _dirty;

    public static DbConnection? Conn { get; private set; }
    public static string Username { get; private set; } = "";
    public static Role Role { get; private set; }
    public static bool IsCommander => Role == Role.Commander;
    public static bool CanAct => Role is Role.Commander or Role.Responder;

    /// Fires (debounced) whenever any subscribed row changed. Screens rebuild their lists.
    public static event Action? Changed;
    public static event Action<string>? Error;
    public static event Action? Locked;

    static void EnsurePump()
    {
        if (_pump != null) return;
        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _pump.Tick += (_, _) =>
        {
            foreach (var c in Ticking.ToArray()) c.FrameTick();
            if (_dirty) { _dirty = false; Changed?.Invoke(); }
        };
        _pump.Start();
    }

    /// token == null => the local server mints a brand-new identity (used for enrollment).
    public static async Task<(DbConnection Conn, string Token)> OpenAsync(string? token)
    {
        var (conn, tok, _) = await OpenWithIdentityAsync(token);
        return (conn, tok);
    }

    public static Task<(DbConnection Conn, string Token, string Identity)> OpenWithIdentityAsync(string? token)
    {
        EnsurePump();
        var tcs = new TaskCompletionSource<(DbConnection, string, string)>();
        DbConnection? conn = null;
        conn = Stdb.Connect(token,
            onConnect: (c, id, tok) => tcs.TrySetResult((c, tok, id.ToString())),
            onError: e => { tcs.TrySetException(e); if (conn != null) Ticking.Remove(conn); },
            onDisconnect: (c, e) =>
            {
                Ticking.Remove(c);
                if (c == Conn) { Error?.Invoke("Lost the connection to the local database. Log in again."); Lock(); }
            });
        Ticking.Add(conn);
        return tcs.Task;
    }

    public static void Close(DbConnection c)
    {
        Ticking.Remove(c);
        try { c.Disconnect(); } catch { /* already closed */ }
    }

    /// Connect with an unlocked token, subscribe to every view, and confirm the account is active.
    public static async Task StartAsync(string token)
    {
        var (conn, _) = await OpenAsync(token);
        var applied = new TaskCompletionSource();
        Stdb.SubscribeAll(conn, () => applied.TrySetResult(), e => applied.TrySetException(e));
        await applied.Task;

        string? username = null;
        Role role = default;
        bool active = false;
        foreach (var u in conn.Db.MyUser.Iter()) { username = u.Username; role = u.Role; active = u.Active; }
        if (username == null || !active)
        {
            Close(conn);
            throw new InvalidOperationException("This account is not enrolled or has been deactivated.");
        }

        Conn = conn;
        Username = username;
        Role = role;
        Stdb.WatchAll(conn, () => _dirty = true);
        WireErrors(conn);
        _dirty = true;

        // The commander's session makes sure the agent, reviewer and executor have identities.
        if (role == Role.Commander) _ = ServiceProvisioner.EnsureAllAsync();
    }

    public static void Lock()
    {
        var c = Conn;
        Conn = null;
        Username = "";
        SelectedIncidentId = 0;
        if (c != null) Close(c);
        Locked?.Invoke();
    }

    public static void ReportError(string message) => Error?.Invoke(message);

    /// The open incident the screens work on: newest one that isn't Closed. 0 = none.
    public static ulong CurrentIncidentId
    {
        get
        {
            if (Conn == null) return 0;
            // A past incident picked under "Previous incidents" stays on screen until "Back to current".
            if (SelectedIncidentId != 0 && Conn.Db.IncidentView.Iter().Any(i => i.Id == SelectedIncidentId)) return SelectedIncidentId;
            return Conn.Db.IncidentView.Iter().Where(i => i.Phase != Phase.Closed)
                       .OrderByDescending(i => i.Id).Select(i => i.Id).FirstOrDefault();
        }
    }

    /// 0 = follow the newest open incident (normal). Otherwise every screen shows this incident.
    public static ulong SelectedIncidentId { get; private set; }

    public static void ViewIncident(ulong incidentId)
    {
        SelectedIncidentId = incidentId;
        _dirty = true;
    }

    public static void BackToCurrent() => ViewIncident(0);

    /// Reducer calls are fire-and-forget; failures come back here and show in the red banner.
    static void WireErrors(DbConnection c)
    {
        var r = c.Reducers;
        void Check(ReducerEventContext ctx, string what)
        {
            var f = Stdb.Failure(ctx);
            if (f != null) Error?.Invoke($"{what} failed: {f}");
        }
        r.OnApproveAction += (ctx, _, _, _) => Check(ctx, "Approval");
        r.OnDenyAction += (ctx, _, _) => Check(ctx, "Deny");
        r.OnProposeAction += (ctx, _, _, _, _, _) => Check(ctx, "Proposing the action");
        r.OnSubmitPrompt += (ctx, _, _, _) => Check(ctx, "Sending to the AI");
        r.OnSetAiConfig += (ctx, _, _, _) => Check(ctx, "Changing the AI settings");
        r.OnOpenIncident += (ctx, _, _) => Check(ctx, "Opening the incident");
        r.OnSetPhase += (ctx, _, _) => Check(ctx, "Changing the phase");
        r.OnSetSummary += (ctx, _, _) => Check(ctx, "Saving the summary");
        r.OnUpsertAsset += (ctx, _, _, _, _, _, _, _) => Check(ctx, "Saving scope");
        r.OnReviewAsset += (ctx, _, _) => Check(ctx, "Reviewing scope");
        r.OnAddTimelineEntry += (ctx, _, _, _) => Check(ctx, "Adding to the timeline");
        r.OnReviewTimelineEntry += (ctx, _, _) => Check(ctx, "Reviewing the timeline entry");
        r.OnRunTerminalCommand += (ctx, _, _, _) => Check(ctx, "Running the command");
        r.OnDismissSuggestion += (ctx, _) => Check(ctx, "Dismissing the suggestion");
        r.OnSetAutoAccept += (ctx, _, _) => Check(ctx, "Changing auto-accept");
        r.OnClearSuggestions += (ctx, _) => Check(ctx, "Clearing suggestions");
        r.OnReleaseHeld += (ctx, _) => Check(ctx, "Continuing");
        r.OnDiscardHeld += (ctx, _) => Check(ctx, "Stopping");
        r.OnCreateInvite += (ctx, _, _, _) => Check(ctx, "Creating the invite");
        r.OnDeactivateUser += (ctx, _) => Check(ctx, "Deactivating the user");
        r.OnSetSshHost += (ctx, _, _, _, _) => Check(ctx, "Saving the host");
        r.OnSetResponderAddress += (ctx, _) => Check(ctx, "Saving the responder address");
        r.OnSetAutoApproveReadOnly += (ctx, _) => Check(ctx, "Changing auto-approve");
        r.OnRecordPosture += (ctx, _, _, _, _, _) => Check(ctx, "Recording the posture change");
        r.OnSetServiceEnabled += (ctx, _, _) => Check(ctx, "Switching the service");
    }
}
