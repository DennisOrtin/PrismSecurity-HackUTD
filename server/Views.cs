using SpacetimeDB;

// Tables are private. Clients subscribe to these views instead, and each view returns
// nothing unless the caller is an active enrolled user. The Invite table has NO view.
// Views may only read through indexes, so every table has a Bucket index that is always 0.
public static partial class Module
{
    static bool IsMember(ViewContext ctx)
    {
        var u = ctx.Db.User.Identity.Find(ctx.Sender);
        return u is not null && u.Value.Active;
    }

    static bool IsHumanMember(ViewContext ctx)
    {
        var u = ctx.Db.User.Identity.Find(ctx.Sender);
        return u is not null && u.Value.Active && IsHuman(u.Value.Role);
    }

    const byte All = 0;

    // Lets a fresh login (or a just-claimed invite) learn its own username and role.
    [SpacetimeDB.View(Accessor = "MyUser", Public = true)]
    public static User? MyUser(ViewContext ctx) => ctx.Db.User.Identity.Find(ctx.Sender);

    [SpacetimeDB.View(Accessor = "UserView", Public = true)]
    public static List<User> UserView(ViewContext ctx) =>
        IsHumanMember(ctx) ? ctx.Db.User.Bucket.Filter(All).ToList() : new List<User>();

    [SpacetimeDB.View(Accessor = "ConfigView", Public = true)]
    public static List<Config> ConfigView(ViewContext ctx)
    {
        var list = new List<Config>();
        if (IsMember(ctx) && ctx.Db.Config.Id.Find(0u) is { } c) list.Add(c);
        return list;
    }

    [SpacetimeDB.View(Accessor = "SshHostView", Public = true)]
    public static List<SshHost> SshHostView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.SshHost.Bucket.Filter(All).ToList() : new List<SshHost>();

    [SpacetimeDB.View(Accessor = "IncidentView", Public = true)]
    public static List<Incident> IncidentView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Incident.Bucket.Filter(All).ToList() : new List<Incident>();

    [SpacetimeDB.View(Accessor = "TimelineView", Public = true)]
    public static List<TimelineEntry> TimelineView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.TimelineEntry.Bucket.Filter(All).ToList() : new List<TimelineEntry>();

    [SpacetimeDB.View(Accessor = "AssetView", Public = true)]
    public static List<Asset> AssetView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Asset.Bucket.Filter(All).ToList() : new List<Asset>();

    [SpacetimeDB.View(Accessor = "EventView", Public = true)]
    public static List<NetEvent> EventView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.NetEvent.Bucket.Filter(All).ToList() : new List<NetEvent>();

    [SpacetimeDB.View(Accessor = "RequestView", Public = true)]
    public static List<AgentRequest> RequestView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.AgentRequest.Bucket.Filter(All).ToList() : new List<AgentRequest>();

    [SpacetimeDB.View(Accessor = "PlanView", Public = true)]
    public static List<Plan> PlanView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Plan.Bucket.Filter(All).ToList() : new List<Plan>();

    [SpacetimeDB.View(Accessor = "ActionView", Public = true)]
    public static List<ResponseAction> ActionView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.ResponseAction.Bucket.Filter(All).ToList() : new List<ResponseAction>();

    [SpacetimeDB.View(Accessor = "ReviewView", Public = true)]
    public static List<Review> ReviewView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Review.Bucket.Filter(All).ToList() : new List<Review>();

    [SpacetimeDB.View(Accessor = "SuggestionView", Public = true)]
    public static List<SuggestedCommand> SuggestionView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.SuggestedCommand.Bucket.Filter(All).ToList() : new List<SuggestedCommand>();

    [SpacetimeDB.View(Accessor = "MessageView", Public = true)]
    public static List<Message> MessageView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Message.Bucket.Filter(All).ToList() : new List<Message>();

    [SpacetimeDB.View(Accessor = "TerminalCommandView", Public = true)]
    public static List<TerminalCommand> TerminalCommandView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.TerminalCommand.Bucket.Filter(All).ToList() : new List<TerminalCommand>();

    [SpacetimeDB.View(Accessor = "TerminalOutputView", Public = true)]
    public static List<TerminalOutput> TerminalOutputView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.TerminalOutput.Bucket.Filter(All).ToList() : new List<TerminalOutput>();

    [SpacetimeDB.View(Accessor = "IndicatorView", Public = true)]
    public static List<Indicator> IndicatorView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Indicator.Bucket.Filter(All).ToList() : new List<Indicator>();

    [SpacetimeDB.View(Accessor = "ParserCursorView", Public = true)]
    public static List<ParserCursor> ParserCursorView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.ParserCursor.Bucket.Filter(All).ToList() : new List<ParserCursor>();

    [SpacetimeDB.View(Accessor = "AuditView", Public = true)]
    public static List<AuditEntry> AuditView(ViewContext ctx) =>
        IsHumanMember(ctx) ? ctx.Db.AuditEntry.Bucket.Filter(All).ToList() : new List<AuditEntry>();
}
