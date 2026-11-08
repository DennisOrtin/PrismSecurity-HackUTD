using SpacetimeDB;

public static partial class Module
{
    const long InviteLifetimeMicros = 15L * 60 * 1_000_000;   // 15 minutes

    // ===================== Lifecycle =====================

    [SpacetimeDB.Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx)
    {
        ctx.Db.Config.Insert(new Config { Id = 0, BootstrapDone = false, AutoApproveReadOnly = false, UserCounter = 0, ResponderAddress = "" });
        ctx.Db.AuditHead.Insert(new AuditHead { Id = 0, LastHash = "GENESIS", Count = 0 });
    }

    // ===================== Identity =====================

    /// First run only: the first caller becomes incident commander, then this reducer is dead forever.
    [SpacetimeDB.Reducer]
    public static void ClaimCommander(ReducerContext ctx, string displayName)
    {
        var cfg = GetConfig(ctx);
        if (cfg.BootstrapDone || ctx.Db.User.Count > 0) throw new Exception("Bootstrap already done.");
        var name = displayName.Trim();
        if (name.Length is < 1 or > 64) throw new Exception("Display name must be 1-64 characters.");

        var user = new User
        {
            Identity = ctx.Sender, Username = "commander-01", DisplayName = name,
            Role = Role.Commander, Active = true, CreatedAt = ctx.Timestamp,
        };
        ctx.Db.User.Insert(user);
        ctx.Db.Config.Id.Update(cfg with { BootstrapDone = true, UserCounter = 1 });
        WriteAudit(ctx, user.Username, "claim_commander", Payload("display_name", name));
    }

    /// Commander creates a one-time code. The app generates the code and sends only its SHA-256.
    [SpacetimeDB.Reducer]
    public static void CreateInvite(ReducerContext ctx, string codeHash, Role role, string displayName)
    {
        var u = Require(ctx, Role.Commander);
        if (codeHash.Length != 64) throw new Exception("Bad code hash.");
        ctx.Db.Invite.Insert(new Invite
        {
            CodeHash = codeHash, Role = role, DisplayName = displayName.Trim(),
            CreatedBy = u.Username, CreatedAt = ctx.Timestamp, Used = false,
        });
        WriteAudit(ctx, u.Username, "create_invite", Payload("role", role, "display_name", displayName));
    }

    /// Called from a brand-new connection (fresh identity minted by the local server).
    [SpacetimeDB.Reducer]
    public static void ClaimInvite(ReducerContext ctx, string code)
    {
        if (ctx.Db.User.Identity.Find(ctx.Sender) is not null) throw new Exception("This identity is already enrolled.");
        var inv = ctx.Db.Invite.CodeHash.Find(Sha256Hex(code.Trim())) ?? throw new Exception("Invalid code.");
        if (inv.Used) throw new Exception("This code was already used.");
        if (ctx.Timestamp.MicrosecondsSinceUnixEpoch - inv.CreatedAt.MicrosecondsSinceUnixEpoch > InviteLifetimeMicros)
            throw new Exception("This code has expired. Ask the commander for a new one.");

        var cfg = GetConfig(ctx);
        var n = cfg.UserCounter + 1;
        string username = IsHuman(inv.Role)
            ? $"{inv.Role.ToString().ToLowerInvariant()}-{n:D2}"
            : (ctx.Db.User.Username.Find(inv.Role.ToString().ToLowerInvariant()) is null
                ? inv.Role.ToString().ToLowerInvariant()
                : $"{inv.Role.ToString().ToLowerInvariant()}-{n:D2}");

        ctx.Db.User.Insert(new User
        {
            Identity = ctx.Sender, Username = username, DisplayName = inv.DisplayName,
            Role = inv.Role, Active = true, CreatedAt = ctx.Timestamp,
        });
        ctx.Db.Invite.CodeHash.Update(inv with { Used = true });
        ctx.Db.Config.Id.Update(cfg with { UserCounter = n });
        WriteAudit(ctx, username, "claim_invite", Payload("role", inv.Role, "created_by", inv.CreatedBy));
    }

    [SpacetimeDB.Reducer]
    public static void DeactivateUser(ReducerContext ctx, string username)
    {
        var u = Require(ctx, Role.Commander);
        if (username == u.Username) throw new Exception("You cannot deactivate yourself.");
        var target = ctx.Db.User.Username.Find(username) ?? throw new Exception("No such user.");
        ctx.Db.User.Identity.Update(target with { Active = false });
        WriteAudit(ctx, u.Username, "deactivate_user", Payload("username", username));
    }

    // ===================== Setup =====================

    [SpacetimeDB.Reducer]
    public static void SetSshHost(ReducerContext ctx, string name, string address, uint port, string pinnedFingerprint)
    {
        var u = Require(ctx, Role.Commander);
        var row = new SshHost { Name = name, Address = address, Port = port, PinnedFingerprint = pinnedFingerprint };
        if (ctx.Db.SshHost.Name.Find(name) is null) ctx.Db.SshHost.Insert(row);
        else ctx.Db.SshHost.Name.Update(row);
        WriteAudit(ctx, u.Username, "set_ssh_host", Payload("name", name, "address", address, "port", port, "fingerprint", pinnedFingerprint));
    }

    [SpacetimeDB.Reducer]
    public static void SetResponderAddress(ReducerContext ctx, string address)
    {
        var u = Require(ctx, Role.Commander);
        if (!IsIPv4(address)) throw new Exception("Must be an IPv4 address.");
        ctx.Db.Config.Id.Update(GetConfig(ctx) with { ResponderAddress = address });
        WriteAudit(ctx, u.Username, "set_responder_address", Payload("address", address));
    }

    [SpacetimeDB.Reducer]
    public static void SetAutoApproveReadOnly(ReducerContext ctx, bool enabled)
    {
        var u = Require(ctx, Role.Commander);
        ctx.Db.Config.Id.Update(GetConfig(ctx) with { AutoApproveReadOnly = enabled });
        WriteAudit(ctx, u.Username, "set_auto_approve_read_only", Payload("enabled", enabled));
    }

    /// The app records every posture Fix/Undo here so it lands in the audit chain.
    [SpacetimeDB.Reducer]
    public static void RecordPosture(ReducerContext ctx, string checkId, string op, bool ok, string before, string after)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        WriteAudit(ctx, u.Username, "posture", Payload("check", checkId, "op", op, "ok", ok, "before", before, "after", after));
    }

    // ===================== Incident documentation =====================

    [SpacetimeDB.Reducer]
    public static void OpenIncident(ReducerContext ctx, string title, byte severity)
    {
        var u = Require(ctx, Role.Commander);
        if (severity is < 1 or > 4) throw new Exception("Severity must be 1-4.");
        var inc = ctx.Db.Incident.Insert(new Incident
        {
            Id = 0, Title = title.Trim(), Phase = Phase.Detected, Severity = severity,
            OpenedAt = ctx.Timestamp, OpenedBy = u.Username, Summary = "",
        });
        AddEntry(ctx, inc.Id, u.Username, AuthorKind.System, "Detection", $"Incident opened by {u.Username}: {title}", true);
        WriteAudit(ctx, u.Username, "open_incident", Payload("id", inc.Id, "title", title, "severity", severity));
    }

    [SpacetimeDB.Reducer]
    public static void SetPhase(ReducerContext ctx, ulong incidentId, Phase phase)
    {
        var u = Require(ctx, Role.Commander);
        var inc = ctx.Db.Incident.Id.Find(incidentId) ?? throw new Exception("No such incident.");
        ctx.Db.Incident.Id.Update(inc with { Phase = phase });
        AddEntry(ctx, incidentId, u.Username, AuthorKind.System, phase.ToString(), $"Phase changed to {phase} by {u.Username}", true);
        WriteAudit(ctx, u.Username, "set_phase", Payload("id", incidentId, "phase", phase));
    }

    [SpacetimeDB.Reducer]
    public static void SetSummary(ReducerContext ctx, ulong incidentId, string summary)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var inc = ctx.Db.Incident.Id.Find(incidentId) ?? throw new Exception("No such incident.");
        ctx.Db.Incident.Id.Update(inc with { Summary = summary });
        WriteAudit(ctx, u.Username, "set_summary", Payload("id", incidentId));
    }

    [SpacetimeDB.Reducer]
    public static void AddTimelineEntry(ReducerContext ctx, ulong incidentId, string category, string text)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        AddEntry(ctx, incidentId, u.Username, AuthorKind.Human, category, text, true);
        WriteAudit(ctx, u.Username, "add_timeline_entry", Payload("incident", incidentId, "category", category, "text", text));
    }

    [SpacetimeDB.Reducer]
    public static void ReviewTimelineEntry(ReducerContext ctx, ulong entryId, bool accept)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var e = ctx.Db.TimelineEntry.Id.Find(entryId) ?? throw new Exception("No such entry.");
        if (accept) ctx.Db.TimelineEntry.Id.Update(e with { Accepted = true });
        else ctx.Db.TimelineEntry.Id.Delete(entryId);
        WriteAudit(ctx, u.Username, accept ? "accept_ai_entry" : "reject_ai_entry", Payload("id", entryId, "text", e.Text));
    }

    [SpacetimeDB.Reducer]
    public static void UpsertAsset(ReducerContext ctx, ulong assetId, ulong incidentId, string host, DataClass dataClass,
                                   ScopeLevel scopeLevel, DataStatus status, string evidence)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var row = new Asset
        {
            Id = assetId, IncidentId = incidentId, Host = host, DataClass = dataClass, ScopeLevel = scopeLevel,
            Status = status, Evidence = evidence, UpdatedBy = u.Username, UpdatedAt = ctx.Timestamp, Accepted = true,
        };
        if (assetId == 0) ctx.Db.Asset.Insert(row);
        else ctx.Db.Asset.Id.Update(row);
        WriteAudit(ctx, u.Username, "upsert_asset", Payload("id", assetId, "host", host, "class", dataClass, "scope", scopeLevel, "status", status));
    }

    [SpacetimeDB.Reducer]
    public static void ReviewAsset(ReducerContext ctx, ulong assetId, bool accept)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var a = ctx.Db.Asset.Id.Find(assetId) ?? throw new Exception("No such asset.");
        if (accept) ctx.Db.Asset.Id.Update(a with { Accepted = true, UpdatedBy = u.Username, UpdatedAt = ctx.Timestamp });
        else ctx.Db.Asset.Id.Delete(assetId);
        WriteAudit(ctx, u.Username, accept ? "accept_ai_scope" : "reject_ai_scope", Payload("id", assetId, "host", a.Host, "status", a.Status));
    }

    // ===================== Agent workflow =====================

    /// Dashboard -> DB. Stores the prompt and the chat message; the agent picks it up by subscription.
    [SpacetimeDB.Reducer]
    public static void SubmitPrompt(ReducerContext ctx, ulong incidentId, string text)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        if (ctx.Db.Incident.Id.Find(incidentId) is null) throw new Exception("No such incident.");
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = incidentId, Author = u.Username, AuthorKind = AuthorKind.Human, Text = text, At = ctx.Timestamp });
        ctx.Db.AgentRequest.Insert(new AgentRequest { Id = 0, IncidentId = incidentId, FromUser = u.Username, Text = text, At = ctx.Timestamp, Status = RequestStatus.Pending });
        WriteAudit(ctx, u.Username, "submit_prompt", Payload("incident", incidentId, "text", text));
    }

    /// Agent -> DB. A plan with ordered steps. Older active plans for the incident are superseded
    /// and their unapproved steps marked Stale.
    [SpacetimeDB.Reducer]
    public static void SubmitPlan(ReducerContext ctx, ulong requestId, string reasoning, List<PlannedStep> steps)
    {
        var u = Require(ctx, Role.Agent);
        var req = ctx.Db.AgentRequest.Id.Find(requestId) ?? throw new Exception("No such request.");
        if (steps.Count > 10) throw new Exception("At most 10 steps per plan.");

        // Validate everything before writing anything.
        var tiers = steps.Select(s => Validate(ctx, s.Kind, s.Host, s.Params)).ToList();

        foreach (var old in ctx.Db.Plan.IncidentId.Filter(req.IncidentId).Where(p => p.Active).ToList())
        {
            ctx.Db.Plan.Id.Update(old with { Active = false });
            foreach (var a in ctx.Db.ResponseAction.PlanId.Filter(old.Id).Where(a => a.Status == ActionStatus.Proposed).ToList())
                ctx.Db.ResponseAction.Id.Update(a with { Status = ActionStatus.Stale, UpdatedAt = ctx.Timestamp });
        }

        var plan = ctx.Db.Plan.Insert(new Plan
        {
            Id = 0, IncidentId = req.IncidentId, RequestId = requestId, Reasoning = reasoning, CreatedAt = ctx.Timestamp, Active = true,
        });

        var cfg = GetConfig(ctx);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            bool auto = tiers[i] == Tier.ReadOnly && cfg.AutoApproveReadOnly;
            ctx.Db.ResponseAction.Insert(new ResponseAction
            {
                Id = 0, IncidentId = req.IncidentId, PlanId = plan.Id, StepOrder = (uint)(i + 1),
                Kind = s.Kind, Host = s.Host, Params = s.Params, Tier = tiers[i], ProposedBy = u.Username,
                Rationale = s.Rationale, Status = auto ? ActionStatus.Approved : ActionStatus.Proposed,
                ApprovedBy = auto ? "auto (read-only policy)" : "", ApprovalNote = "",
                ContentHash = ContentHash(s.Kind, s.Host, s.Params), Output = "",
                CreatedAt = ctx.Timestamp, UpdatedAt = ctx.Timestamp,
            });
        }

        ctx.Db.AgentRequest.Id.Update(req with { Status = RequestStatus.Planned });
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = req.IncidentId, Author = u.Username, AuthorKind = AuthorKind.Assistant, Text = reasoning, At = ctx.Timestamp });
        WriteAudit(ctx, u.Username, "submit_plan", Payload("plan", plan.Id, "request", requestId, "steps", steps.Count));
    }

    /// Agent answers a question without proposing anything (doesn't supersede the active plan).
    [SpacetimeDB.Reducer]
    public static void AnswerRequest(ReducerContext ctx, ulong requestId, string text)
    {
        var u = Require(ctx, Role.Agent);
        var req = ctx.Db.AgentRequest.Id.Find(requestId) ?? throw new Exception("No such request.");
        ctx.Db.AgentRequest.Id.Update(req with { Status = RequestStatus.Planned });
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = req.IncidentId, Author = u.Username, AuthorKind = AuthorKind.Assistant, Text = text, At = ctx.Timestamp });
    }

    [SpacetimeDB.Reducer]
    public static void FailRequest(ReducerContext ctx, ulong requestId, string reason)
    {
        var u = Require(ctx, Role.Agent);
        var req = ctx.Db.AgentRequest.Id.Find(requestId) ?? throw new Exception("No such request.");
        ctx.Db.AgentRequest.Id.Update(req with { Status = RequestStatus.Failed });
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = req.IncidentId, Author = u.Username, AuthorKind = AuthorKind.System, Text = $"Agent could not plan: {reason}", At = ctx.Timestamp });
    }

    /// A human proposes a catalog action directly (outside any plan).
    [SpacetimeDB.Reducer]
    public static void ProposeAction(ReducerContext ctx, ulong incidentId, string kind, string host, List<Param> ps, string rationale)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var tier = Validate(ctx, kind, host, ps);
        var row = ctx.Db.ResponseAction.Insert(new ResponseAction
        {
            Id = 0, IncidentId = incidentId, PlanId = 0, StepOrder = 0, Kind = kind, Host = host, Params = ps, Tier = tier,
            ProposedBy = u.Username, Rationale = rationale, Status = ActionStatus.Proposed, ApprovedBy = "", ApprovalNote = "",
            ContentHash = ContentHash(kind, host, ps), Output = "", CreatedAt = ctx.Timestamp, UpdatedAt = ctx.Timestamp,
        });
        WriteAudit(ctx, u.Username, "propose_action", Payload("id", row.Id, "kind", kind, "host", host));
    }

    [SpacetimeDB.Reducer]
    public static void ProposeTimelineEntry(ReducerContext ctx, ulong incidentId, string category, string text)
    {
        var u = Require(ctx, Role.Agent);
        AddEntry(ctx, incidentId, u.Username, AuthorKind.Assistant, category, text, false);
    }

    [SpacetimeDB.Reducer]
    public static void ProposeScopeUpdate(ReducerContext ctx, ulong incidentId, string host, DataClass dataClass,
                                          ScopeLevel scopeLevel, DataStatus status, string evidence)
    {
        var u = Require(ctx, Role.Agent);
        ctx.Db.Asset.Insert(new Asset
        {
            Id = 0, IncidentId = incidentId, Host = host, DataClass = dataClass, ScopeLevel = scopeLevel, Status = status,
            Evidence = evidence, UpdatedBy = u.Username, UpdatedAt = ctx.Timestamp, Accepted = false,
        });
    }

    [SpacetimeDB.Reducer]
    public static void SuggestCommand(ReducerContext ctx, ulong incidentId, string host, string text, string rationale)
    {
        var u = Require(ctx, Role.Agent);
        ctx.Db.SuggestedCommand.Insert(new SuggestedCommand
        {
            Id = 0, IncidentId = incidentId, Host = host, Text = text, Rationale = rationale, CreatedAt = ctx.Timestamp,
        });
        WriteAudit(ctx, u.Username, "suggest_command", Payload("host", host, "text", text));
    }

    [SpacetimeDB.Reducer]
    public static void PostAiMessage(ReducerContext ctx, ulong incidentId, string text)
    {
        var u = Require(ctx, Role.Agent, Role.Reviewer);
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = incidentId, Author = u.Username, AuthorKind = AuthorKind.Assistant, Text = text, At = ctx.Timestamp });
    }

    /// Reviewer can only ADD warnings. It never blocks or approves.
    [SpacetimeDB.Reducer]
    public static void AttachReview(ReducerContext ctx, string targetKind, ulong targetId, Risk risk, string reasons)
    {
        var u = Require(ctx, Role.Reviewer);
        if (targetKind is not ("action" or "suggestion")) throw new Exception("targetKind must be action or suggestion.");
        ctx.Db.Review.Insert(new Review { Id = 0, TargetKind = targetKind, TargetId = targetId, Risk = risk, Reasons = reasons, At = ctx.Timestamp });
        WriteAudit(ctx, u.Username, "attach_review", Payload("target", $"{targetKind}:{targetId}", "risk", risk));
    }

    // ===================== Approval =====================

    /// The dashboard sends back only the ID and the hash it displayed. The executor will run the
    /// STORED proposal, never anything the dashboard sends.
    [SpacetimeDB.Reducer]
    public static void ApproveAction(ReducerContext ctx, ulong actionId, string contentHash, string note)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var a = ctx.Db.ResponseAction.Id.Find(actionId) ?? throw new Exception("No such action.");
        if (a.Status != ActionStatus.Proposed) throw new Exception($"Action is {a.Status}, not awaiting approval.");
        if (a.ContentHash != contentHash) throw new Exception("This proposal changed after you viewed it. Review it again.");
        if (a.Tier == Tier.Destructive && u.Role != Role.Commander) throw new Exception("Destructive actions need the incident commander.");
        if (Validate(ctx, a.Kind, a.Host, a.Params) != a.Tier) throw new Exception("Action no longer passes validation.");

        if (a.PlanId != 0)
        {
            bool blocked = ctx.Db.ResponseAction.PlanId.Filter(a.PlanId).Any(o =>
                o.StepOrder < a.StepOrder &&
                o.Status is ActionStatus.Proposed or ActionStatus.Approved or ActionStatus.Running);
            if (blocked) throw new Exception("Earlier steps in this plan have not finished yet.");
        }

        ctx.Db.ResponseAction.Id.Update(a with
        {
            Status = ActionStatus.Approved, ApprovedBy = u.Username, ApprovalNote = note, UpdatedAt = ctx.Timestamp,
        });
        WriteAudit(ctx, u.Username, "approve_action", Payload("id", actionId, "hash", contentHash, "kind", a.Kind, "host", a.Host, "note", note));
    }

    [SpacetimeDB.Reducer]
    public static void DenyAction(ReducerContext ctx, ulong actionId, string note)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        var a = ctx.Db.ResponseAction.Id.Find(actionId) ?? throw new Exception("No such action.");
        if (a.Status != ActionStatus.Proposed) throw new Exception($"Action is {a.Status}, not awaiting approval.");
        ctx.Db.ResponseAction.Id.Update(a with { Status = ActionStatus.Denied, ApprovedBy = u.Username, ApprovalNote = note, UpdatedAt = ctx.Timestamp });
        WriteAudit(ctx, u.Username, "deny_action", Payload("id", actionId, "note", note));
    }

    // ===================== Executor =====================

    [SpacetimeDB.Reducer]
    public static void StartAction(ReducerContext ctx, ulong actionId)
    {
        var u = Require(ctx, Role.Executor);
        var a = ctx.Db.ResponseAction.Id.Find(actionId) ?? throw new Exception("No such action.");
        if (a.Status != ActionStatus.Approved) throw new Exception("Only approved actions can start.");
        ctx.Db.ResponseAction.Id.Update(a with { Status = ActionStatus.Running, UpdatedAt = ctx.Timestamp });
        WriteAudit(ctx, u.Username, "start_action", Payload("id", actionId, "hash", a.ContentHash));
    }

    [SpacetimeDB.Reducer]
    public static void FinishAction(ReducerContext ctx, ulong actionId, bool success, string output)
    {
        var u = Require(ctx, Role.Executor);
        var a = ctx.Db.ResponseAction.Id.Find(actionId) ?? throw new Exception("No such action.");
        if (a.Status != ActionStatus.Running) throw new Exception("Action is not running.");
        var trimmed = output.Length > 20000 ? output[..20000] + "\n[truncated]" : output;
        ctx.Db.ResponseAction.Id.Update(a with
        {
            Status = success ? ActionStatus.Succeeded : ActionStatus.Failed, Output = trimmed, UpdatedAt = ctx.Timestamp,
        });
        AddEntry(ctx, a.IncidentId, u.Username, AuthorKind.System, a.Tier == Tier.ReadOnly ? "Analysis" : "Containment",
                 $"{a.Kind} on {a.Host} {(success ? "succeeded" : "failed")} (approved by {a.ApprovedBy})", true);
        WriteAudit(ctx, u.Username, "finish_action", Payload("id", actionId, "success", success));
    }

    /// Executor writes pulled network data. High-severity events wake the agent automatically.
    [SpacetimeDB.Reducer]
    public static void RecordEvent(ReducerContext ctx, ulong incidentId, string host, string source, string raw, byte severity)
    {
        Require(ctx, Role.Executor);
        var clipped = raw.Length > 4000 ? raw[..4000] : raw;
        ctx.Db.NetEvent.Insert(new NetEvent { Id = 0, IncidentId = incidentId, Host = host, Source = source, At = ctx.Timestamp, Raw = clipped, Severity = severity });

        bool pending = ctx.Db.AgentRequest.IncidentId.Filter(incidentId).Any(r => r.Status == RequestStatus.Pending);
        if (severity >= 3 && !pending)
            ctx.Db.AgentRequest.Insert(new AgentRequest
            {
                Id = 0, IncidentId = incidentId, FromUser = "system", Text = $"New severity-{severity} event on {host} ({source}). Re-assess.",
                At = ctx.Timestamp, Status = RequestStatus.Pending,
            });
    }

    // ===================== Human terminal =====================

    [SpacetimeDB.Reducer]
    public static void RunTerminalCommand(ReducerContext ctx, string host, string command)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        if (ctx.Db.SshHost.Name.Find(host) is null) throw new Exception($"Unknown host '{host}'.");
        if (command.Length is < 1 or > 2000) throw new Exception("Command must be 1-2000 characters.");
        var row = ctx.Db.TerminalCommand.Insert(new TerminalCommand
        {
            Id = 0, Host = host, Command = command, IssuedBy = u.Username, Status = TerminalStatus.Queued, ExitCode = 0, At = ctx.Timestamp,
        });
        WriteAudit(ctx, u.Username, "run_terminal_command", Payload("id", row.Id, "host", host, "command", command));
    }

    [SpacetimeDB.Reducer]
    public static void StartTerminalCommand(ReducerContext ctx, ulong commandId)
    {
        Require(ctx, Role.Executor);
        var c = ctx.Db.TerminalCommand.Id.Find(commandId) ?? throw new Exception("No such command.");
        if (c.Status != TerminalStatus.Queued) throw new Exception("Command is not queued.");
        ctx.Db.TerminalCommand.Id.Update(c with { Status = TerminalStatus.Running });
    }

    [SpacetimeDB.Reducer]
    public static void AppendTerminalOutput(ReducerContext ctx, ulong commandId, string chunk)
    {
        Require(ctx, Role.Executor);
        ctx.Db.TerminalOutput.Insert(new TerminalOutput { Id = 0, CommandId = commandId, Chunk = chunk.Length > 8000 ? chunk[..8000] : chunk });
    }

    [SpacetimeDB.Reducer]
    public static void FinishTerminalCommand(ReducerContext ctx, ulong commandId, int exitCode, bool failed)
    {
        var u = Require(ctx, Role.Executor);
        var c = ctx.Db.TerminalCommand.Id.Find(commandId) ?? throw new Exception("No such command.");
        ctx.Db.TerminalCommand.Id.Update(c with { Status = failed ? TerminalStatus.Failed : TerminalStatus.Done, ExitCode = exitCode });
        WriteAudit(ctx, u.Username, "finish_terminal_command", Payload("id", commandId, "exit", exitCode));
    }

    // ===================== Internal =====================

    static void AddEntry(ReducerContext ctx, ulong incidentId, string author, AuthorKind kind, string category, string text, bool accepted) =>
        ctx.Db.TimelineEntry.Insert(new TimelineEntry
        {
            Id = 0, IncidentId = incidentId, At = ctx.Timestamp, Author = author, AuthorKind = kind,
            Category = category, Text = text, Accepted = accepted,
        });
}
