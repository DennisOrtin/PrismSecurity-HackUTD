using SpacetimeDB;

// Tables are private. Clients subscribe to these views instead, and each view returns
// nothing unless the caller is an active enrolled user. The Invite table has NO view.
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

    static List<T> Empty<T>() => new List<T>();

    // Lets a fresh login (or a just-claimed invite) learn its own username and role.
    [SpacetimeDB.View(Accessor = "MyUser", Public = true)]
    public static User? MyUser(ViewContext ctx) => ctx.Db.User.Identity.Find(ctx.Sender);

    [SpacetimeDB.View(Accessor = "UserView", Public = true)]
    public static List<User> UserView(ViewContext ctx) =>
        IsHumanMember(ctx) ? ctx.Db.User.Iter().ToList() : Empty<User>();

    [SpacetimeDB.View(Accessor = "ConfigView", Public = true)]
    public static List<Config> ConfigView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Config.Iter().ToList() : Empty<Config>();

    [SpacetimeDB.View(Accessor = "SshHostView", Public = true)]
    public static List<SshHost> SshHostView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.SshHost.Iter().ToList() : Empty<SshHost>();

    [SpacetimeDB.View(Accessor = "IncidentView", Public = true)]
    public static List<Incident> IncidentView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Incident.Iter().ToList() : Empty<Incident>();

    [SpacetimeDB.View(Accessor = "TimelineView", Public = true)]
    public static List<TimelineEntry> TimelineView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.TimelineEntry.Iter().ToList() : Empty<TimelineEntry>();

    [SpacetimeDB.View(Accessor = "AssetView", Public = true)]
    public static List<Asset> AssetView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Asset.Iter().ToList() : Empty<Asset>();

    [SpacetimeDB.View(Accessor = "EventView", Public = true)]
    public static List<NetEvent> EventView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.NetEvent.Iter().ToList() : Empty<NetEvent>();

    [SpacetimeDB.View(Accessor = "RequestView", Public = true)]
    public static List<AgentRequest> RequestView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.AgentRequest.Iter().ToList() : Empty<AgentRequest>();

    [SpacetimeDB.View(Accessor = "PlanView", Public = true)]
    public static List<Plan> PlanView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Plan.Iter().ToList() : Empty<Plan>();

    [SpacetimeDB.View(Accessor = "ActionView", Public = true)]
    public static List<ResponseAction> ActionView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.ResponseAction.Iter().ToList() : Empty<ResponseAction>();

    [SpacetimeDB.View(Accessor = "ReviewView", Public = true)]
    public static List<Review> ReviewView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Review.Iter().ToList() : Empty<Review>();

    [SpacetimeDB.View(Accessor = "SuggestionView", Public = true)]
    public static List<SuggestedCommand> SuggestionView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.SuggestedCommand.Iter().ToList() : Empty<SuggestedCommand>();

    [SpacetimeDB.View(Accessor = "MessageView", Public = true)]
    public static List<Message> MessageView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.Message.Iter().ToList() : Empty<Message>();

    [SpacetimeDB.View(Accessor = "TerminalCommandView", Public = true)]
    public static List<TerminalCommand> TerminalCommandView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.TerminalCommand.Iter().ToList() : Empty<TerminalCommand>();

    [SpacetimeDB.View(Accessor = "TerminalOutputView", Public = true)]
    public static List<TerminalOutput> TerminalOutputView(ViewContext ctx) =>
        IsMember(ctx) ? ctx.Db.TerminalOutput.Iter().ToList() : Empty<TerminalOutput>();

    [SpacetimeDB.View(Accessor = "AuditView", Public = true)]
    public static List<AuditEntry> AuditView(ViewContext ctx) =>
        IsHumanMember(ctx) ? ctx.Db.AuditEntry.Iter().ToList() : Empty<AuditEntry>();
}
