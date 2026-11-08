// Incident Command - SpacetimeDB 2.x C# module: types and tables.
// Every table is PRIVATE (no Public = true). Clients only see data through the
// role-checked views in Views.cs.
using SpacetimeDB;

// ---------- Enums and small types ----------

[SpacetimeDB.Type] public enum Role { Commander, Responder, Observer, Agent, Reviewer, Executor }
[SpacetimeDB.Type] public enum Tier { ReadOnly, Containment, Destructive }
[SpacetimeDB.Type] public enum ActionStatus { Proposed, Approved, Denied, Running, Succeeded, Failed, Stale }
[SpacetimeDB.Type] public enum Phase { Detected, Analysis, Containment, Eradication, Recovery, Closed }
[SpacetimeDB.Type] public enum AuthorKind { Human, Assistant, System }   // "Assistant", not "AI": codegen re-cases acronyms
[SpacetimeDB.Type] public enum ScopeLevel { User, Application, System, Network }
[SpacetimeDB.Type] public enum DataStatus { Unknown, Secure, Encrypted, Exfiltrated, EncryptedAndExfiltrated }
[SpacetimeDB.Type] public enum DataClass { Credentials, PersonalData, Financial, IntellectualProperty, Operational }
[SpacetimeDB.Type] public enum Risk { None, Caution, Danger }
[SpacetimeDB.Type] public enum RequestStatus { Pending, Planned, Failed }
[SpacetimeDB.Type] public enum TerminalStatus { Queued, Running, Done, Failed }

[SpacetimeDB.Type]
public partial struct Param
{
    public string Key;
    public string Value;
}

[SpacetimeDB.Type]
public partial struct PlannedStep
{
    public string Kind;
    public string Host;
    public List<Param> Params;
    public string Rationale;
}

// ---------- Identity and config ----------

[SpacetimeDB.Table(Accessor = "User")]
public partial struct User
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey] public Identity Identity;
    [SpacetimeDB.Unique] public string Username;   // assigned by the module, never updated
    public string DisplayName;
    public Role Role;
    public bool Active;
    public Timestamp CreatedAt;
}

// One-time enrollment codes. Only the SHA-256 of the code is stored; the
// commander's app shows the plaintext code on screen.
[SpacetimeDB.Table(Accessor = "Invite")]
public partial struct Invite
{
    [SpacetimeDB.PrimaryKey] public string CodeHash;
    public Role Role;
    public string DisplayName;
    public string CreatedBy;
    public Timestamp CreatedAt;
    public bool Used;
}

[SpacetimeDB.Table(Accessor = "Config")]
public partial struct Config
{
    [SpacetimeDB.PrimaryKey] public uint Id;          // always 0
    public bool BootstrapDone;
    public bool AutoApproveReadOnly;
    public ulong UserCounter;
    public string ResponderAddress;                    // the laptop's IP on the network, never a valid target
}

[SpacetimeDB.Table(Accessor = "SshHost")]
public partial struct SshHost
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey] public string Name;      // e.g. "web-01"
    public string Address;
    public uint Port;
    public string PinnedFingerprint;                   // SHA256 fingerprint, as SSH.NET reports it
}

// ---------- Incident documentation ----------

[SpacetimeDB.Table(Accessor = "Incident")]
public partial struct Incident
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    public string Title;
    public Phase Phase;
    public byte Severity;                              // 1 (low) to 4 (critical)
    public Timestamp OpenedAt;
    public string OpenedBy;
    public string Summary;
}

[SpacetimeDB.Table(Accessor = "TimelineEntry")]
public partial struct TimelineEntry
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public Timestamp At;
    public string Author;
    public AuthorKind AuthorKind;
    public string Category;                            // Detection, Analysis, Containment, Eradication, Recovery, Communication, Note
    public string Text;
    public bool Accepted;                              // AI entries start false
}

[SpacetimeDB.Table(Accessor = "Asset")]
public partial struct Asset
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public string Host;
    public DataClass DataClass;
    public ScopeLevel ScopeLevel;
    public DataStatus Status;
    public string Evidence;
    public string UpdatedBy;
    public Timestamp UpdatedAt;
    public bool Accepted;                              // AI proposals start false
}

[SpacetimeDB.Table(Accessor = "NetEvent")]
public partial struct NetEvent
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public string Host;
    public string Source;
    public Timestamp At;
    public string Raw;                                 // UNTRUSTED: written by the network
    public byte Severity;
}

// ---------- Agent workflow ----------

[SpacetimeDB.Table(Accessor = "AgentRequest")]
public partial struct AgentRequest
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public string FromUser;
    public string Text;
    public Timestamp At;
    public RequestStatus Status;
}

[SpacetimeDB.Table(Accessor = "Plan")]
public partial struct Plan
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public ulong RequestId;
    public string Reasoning;
    public Timestamp CreatedAt;
    public bool Active;
}

// Named ResponseAction to avoid clashing with System.Action.
[SpacetimeDB.Table(Accessor = "ResponseAction")]
public partial struct ResponseAction
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    [SpacetimeDB.Index.BTree] public ulong PlanId;     // 0 = proposed by a human, outside a plan
    public uint StepOrder;
    public string Kind;
    public string Host;
    public List<Param> Params;
    public Tier Tier;
    public string ProposedBy;
    public string Rationale;
    public ActionStatus Status;
    public string ApprovedBy;
    public string ApprovalNote;
    public string ContentHash;                         // SHA-256 of kind|host|params, checked on approval
    public string Output;
    public Timestamp CreatedAt;
    public Timestamp UpdatedAt;
}

[SpacetimeDB.Table(Accessor = "Review")]
public partial struct Review
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    public string TargetKind;                          // "action" or "suggestion"
    public ulong TargetId;
    public Risk Risk;
    public string Reasons;
    public Timestamp At;
}

[SpacetimeDB.Table(Accessor = "SuggestedCommand")]
public partial struct SuggestedCommand
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public string Host;
    public string Text;                                // NEVER executable from here
    public string Rationale;
    public Timestamp CreatedAt;
}

[SpacetimeDB.Table(Accessor = "Message")]
public partial struct Message
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong IncidentId;
    public string Author;
    public AuthorKind AuthorKind;
    public string Text;
    public Timestamp At;
}

// ---------- Human terminal ----------

[SpacetimeDB.Table(Accessor = "TerminalCommand")]
public partial struct TerminalCommand
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    public string Host;
    public string Command;
    public string IssuedBy;
    public TerminalStatus Status;
    public int ExitCode;
    public Timestamp At;
}

[SpacetimeDB.Table(Accessor = "TerminalOutput")]
public partial struct TerminalOutput
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Id;
    [SpacetimeDB.Index.BTree] public ulong CommandId;
    public string Chunk;
}

// ---------- Audit chain ----------

[SpacetimeDB.Table(Accessor = "AuditEntry")]
public partial struct AuditEntry
{
    /// Always 0. Views can't scan tables (no Iter in a ViewContext), only use indexes,
    /// so views read every row with Bucket.Filter(0). Never set it; the default is 0.
    [SpacetimeDB.Index.BTree] public byte Bucket;
    [SpacetimeDB.PrimaryKey, SpacetimeDB.AutoInc] public ulong Seq;
    public Timestamp At;
    public Identity Actor;
    public string Username;
    public string Reducer;
    public string PayloadJson;
    public string PrevHash;
    public string Hash;
}

[SpacetimeDB.Table(Accessor = "AuditHead")]
public partial struct AuditHead
{
    [SpacetimeDB.PrimaryKey] public uint Id;          // always 0
    public string LastHash;
    public ulong Count;
}
