using SpacetimeDB;

public static partial class Module
{
    const long InviteLifetimeMicros = 15L * 60 * 1_000_000;   // 15 minutes

    // ===================== Lifecycle =====================

    [SpacetimeDB.Reducer(ReducerKind.Init)]
    public static void Init(ReducerContext ctx)
    {
        ctx.Db.Config.Insert(new Config
        {
            Id = 0, BootstrapDone = false, AutoApproveReadOnly = false, UserCounter = 0, ResponderAddress = "",
            AgentModel = "", ReviewerModel = "", ParserModel = "",
            AgentEnabled = true, ReviewerEnabled = true, ExecutorEnabled = true, ParserEnabled = true,
        });
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
        bool active = true;
        string username;
        if (IsHuman(inv.Role))
        {
            username = $"{inv.Role.ToString().ToLowerInvariant()}-{n:D2}";
        }
        else
        {
            // Built-in service: exactly one identity per role, always named after the role.
            // Re-provisioning replaces (and so revokes) the previous identity.
            username = inv.Role.ToString().ToLowerInvariant();
            foreach (var old in ctx.Db.User.Iter().Where(x => x.Role == inv.Role).ToList())
                ctx.Db.User.Identity.Delete(old.Identity);
            active = ServiceEnabled(cfg, inv.Role);
        }

        ctx.Db.User.Insert(new User
        {
            Identity = ctx.Sender, Username = username, DisplayName = inv.DisplayName,
            Role = inv.Role, Active = active, CreatedAt = ctx.Timestamp,
        });
        ctx.Db.Invite.CodeHash.Update(inv with { Used = true });
        ctx.Db.Config.Id.Update(cfg with { UserCounter = n });
        WriteAudit(ctx, username, "claim_invite", Payload("role", inv.Role, "created_by", inv.CreatedBy));
    }

    static bool ServiceEnabled(Config cfg, Role role) => role switch
    {
        Role.Agent => cfg.AgentEnabled,
        Role.Reviewer => cfg.ReviewerEnabled,
        Role.Executor => cfg.ExecutorEnabled,
        Role.Parser => cfg.ParserEnabled,
        _ => true,
    };

    /// Commander switches a built-in service on or off. Off = its identity is inactive, so every reducer
    /// refuses it and every view returns nothing to it. The service process idles until switched back on.
    [SpacetimeDB.Reducer]
    public static void SetServiceEnabled(ReducerContext ctx, Role role, bool enabled)
    {
        var u = Require(ctx, Role.Commander);
        if (IsHuman(role)) throw new Exception("Only the agent, reviewer and executor can be switched on or off here.");
        var cfg = GetConfig(ctx);
        cfg = role switch
        {
            Role.Agent => cfg with { AgentEnabled = enabled },
            Role.Reviewer => cfg with { ReviewerEnabled = enabled },
            Role.Parser => cfg with { ParserEnabled = enabled },
            _ => cfg with { ExecutorEnabled = enabled },
        };
        ctx.Db.Config.Id.Update(cfg);
        foreach (var svc in ctx.Db.User.Iter().Where(x => x.Role == role).ToList())
            ctx.Db.User.Identity.Update(svc with { Active = enabled });
        WriteAudit(ctx, u.Username, "set_service_enabled", Payload("role", role, "enabled", enabled));
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

    /// Trust on first use: if the commander registered a host without a fingerprint, the executor pins the
    /// key it sees on its first connection. Only ever fills an EMPTY fingerprint; a mismatch later is refused.
    [SpacetimeDB.Reducer]
    public static void PinHostKey(ReducerContext ctx, string name, string fingerprint)
    {
        var u = Require(ctx, Role.Executor);
        var h = ctx.Db.SshHost.Name.Find(name) ?? throw new Exception($"Unknown host '{name}'.");
        if (!string.IsNullOrWhiteSpace(h.PinnedFingerprint)) throw new Exception($"{name} already has a pinned host key.");
        if (fingerprint.Length is < 20 or > 120) throw new Exception("That doesn't look like an SSH fingerprint.");
        ctx.Db.SshHost.Name.Update(h with { PinnedFingerprint = fingerprint });
        WriteAudit(ctx, u.Username, "pin_host_key", Payload("name", name, "fingerprint", fingerprint));
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

    /// Commander picks each local agent's model. Applied on the agents' next call.
    [SpacetimeDB.Reducer]
    public static void SetAiConfig(ReducerContext ctx, string agentModel, string reviewerModel, string parserModel)
    {
        var u = Require(ctx, Role.Commander);
        if (new[] { agentModel, reviewerModel, parserModel }.Any(m => m.Length > 200)) throw new Exception("Model ID too long.");
        ctx.Db.Config.Id.Update(GetConfig(ctx) with { AgentModel = agentModel, ReviewerModel = reviewerModel, ParserModel = parserModel });
        WriteAudit(ctx, u.Username, "set_ai_config", Payload("agent_model", agentModel, "reviewer_model", reviewerModel, "parser_model", parserModel));
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
            AiSummary = "", Classification = "", AiUpdatedAt = ctx.Timestamp,
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

        // An AI entry filed under a phase (Detected, Analysis, Containment…) is a proposed phase change.
        // Accepting it moves the incident, exactly like the Overview phase buttons. Commander only, as there.
        if (accept && Enum.TryParse<Phase>(e.Category, out var phase))
        {
            var inc = ctx.Db.Incident.Id.Find(e.IncidentId) ?? throw new Exception("No such incident.");
            if (inc.Phase != phase)
            {
                if (u.Role != Role.Commander)
                    throw new Exception($"Accepting this moves the incident to {phase}. Only the incident commander can change the phase.");
                ctx.Db.Incident.Id.Update(inc with { Phase = phase });
                AddEntry(ctx, e.IncidentId, u.Username, AuthorKind.System, phase.ToString(),
                         $"Phase changed to {phase} by {u.Username} (accepted an AI timeline entry)", true);
                WriteAudit(ctx, u.Username, "set_phase", Payload("id", e.IncidentId, "phase", phase, "via", $"timeline entry {entryId}"));
            }
        }

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
    public static void SubmitPrompt(ReducerContext ctx, ulong incidentId, string text, string model)
    {
        var u = Require(ctx, Role.Commander, Role.Responder);
        if (ctx.Db.Incident.Id.Find(incidentId) is null) throw new Exception("No such incident.");
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = incidentId, Author = u.Username, AuthorKind = AuthorKind.Human, Text = text, At = ctx.Timestamp, ModelLabel = "" });
        ctx.Db.AgentRequest.Insert(new AgentRequest
        {
            Id = 0, IncidentId = incidentId, FromUser = u.Username, Text = text, At = ctx.Timestamp,
            Status = RequestStatus.Pending, Model = model, Depth = 0,
        });
        WriteAudit(ctx, u.Username, "submit_prompt", Payload("incident", incidentId, "text", text, "model", model));
    }

    /// Agent -> DB. A plan with ordered steps. Older active plans for the incident are superseded
    /// and their unapproved steps marked Stale.
    [SpacetimeDB.Reducer]
    public static void SubmitPlan(ReducerContext ctx, ulong requestId, string reasoning, List<PlannedStep> steps, string modelLabel)
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
            ModelLabel = modelLabel,
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
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = req.IncidentId, Author = u.Username, AuthorKind = AuthorKind.Assistant, Text = reasoning, At = ctx.Timestamp, ModelLabel = modelLabel });
        WriteAudit(ctx, u.Username, "submit_plan", Payload("plan", plan.Id, "request", requestId, "steps", steps.Count));
    }

    /// Agent answers a question without proposing anything (doesn't supersede the active plan).
    [SpacetimeDB.Reducer]
    public static void AnswerRequest(ReducerContext ctx, ulong requestId, string text, string modelLabel)
    {
        var u = Require(ctx, Role.Agent);
        var req = ctx.Db.AgentRequest.Id.Find(requestId) ?? throw new Exception("No such request.");
        ctx.Db.AgentRequest.Id.Update(req with { Status = RequestStatus.Planned });
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = req.IncidentId, Author = u.Username, AuthorKind = AuthorKind.Assistant, Text = text, At = ctx.Timestamp, ModelLabel = modelLabel });
    }

    [SpacetimeDB.Reducer]
    public static void FailRequest(ReducerContext ctx, ulong requestId, string reason)
    {
        var u = Require(ctx, Role.Agent);
        var req = ctx.Db.AgentRequest.Id.Find(requestId) ?? throw new Exception("No such request.");
        ctx.Db.AgentRequest.Id.Update(req with { Status = RequestStatus.Failed });
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = req.IncidentId, Author = u.Username, AuthorKind = AuthorKind.System, Text = $"Agent could not plan: {reason}", At = ctx.Timestamp, ModelLabel = "" });
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
        var u = Require(ctx, Role.Agent, Role.Parser);
        AddEntry(ctx, incidentId, u.Username, AuthorKind.Assistant, category, text, false);
    }

    [SpacetimeDB.Reducer]
    public static void ProposeScopeUpdate(ReducerContext ctx, ulong incidentId, string host, DataClass dataClass,
                                          ScopeLevel scopeLevel, DataStatus status, string evidence)
    {
        var u = Require(ctx, Role.Agent, Role.Parser);
        if (ctx.Db.Incident.Id.Find(incidentId) is null) throw new Exception("No such incident.");
        var same = ctx.Db.Asset.IncidentId.Filter(incidentId).Where(a => a.Host == host && a.DataClass == dataClass).ToList();

        // Already accepted by a human with the same status and scope: nothing new to propose.
        if (same.Any(a => a.Accepted && a.Status == status && a.ScopeLevel == scopeLevel)) return;

        // An earlier AI proposal for the same host + data is still pending: update it instead of piling up.
        var pending = same.Where(a => !a.Accepted).ToList();
        if (pending.Count > 0)
        {
            ctx.Db.Asset.Id.Update(pending[0] with
            {
                ScopeLevel = scopeLevel, Status = status, Evidence = evidence, UpdatedBy = u.Username, UpdatedAt = ctx.Timestamp,
            });
            return;
        }
        ctx.Db.Asset.Insert(new Asset
        {
            Id = 0, IncidentId = incidentId, Host = host, DataClass = dataClass, ScopeLevel = scopeLevel, Status = status,
            Evidence = evidence, UpdatedBy = u.Username, UpdatedAt = ctx.Timestamp, Accepted = false,
        });
    }

    // ===================== Parser agent =====================
    // The parser turns everything in the dashboard into structured data. Its writes are always labelled
    // as AI-derived and never trigger actions; scope changes still need a human to accept them.

    [SpacetimeDB.Reducer]
    public static void UpdateIncidentAnalysis(ReducerContext ctx, ulong incidentId, string aiSummary, string classification)
    {
        Require(ctx, Role.Parser);
        var inc = ctx.Db.Incident.Id.Find(incidentId) ?? throw new Exception("No such incident.");
        ctx.Db.Incident.Id.Update(inc with
        {
            AiSummary = Clip(aiSummary, 4000), Classification = Clip(classification, 120), AiUpdatedAt = ctx.Timestamp,
        });
    }

    [SpacetimeDB.Reducer]
    public static void UpsertIndicator(ReducerContext ctx, ulong incidentId, IndicatorKind kind, string value,
                                       string context, Confidence confidence, string source)
    {
        Require(ctx, Role.Parser);
        if (ctx.Db.Incident.Id.Find(incidentId) is null) throw new Exception("No such incident.");
        var v = value.Trim();
        if (v.Length is < 1 or > 300) throw new Exception("Indicator value must be 1-300 characters.");
        var existing = ctx.Db.Indicator.IncidentId.Filter(incidentId).Where(i => i.Kind == kind && i.Value == v).ToList();
        if (existing.Count > 0)
        {
            var e = existing[0];
            var sources = e.Sources.Contains(source) ? e.Sources : Clip($"{e.Sources}, {source}", 500);
            ctx.Db.Indicator.Id.Update(e with
            {
                Context = Clip(context, 300), Confidence = (Confidence)Math.Max((int)e.Confidence, (int)confidence),
                Sources = sources, LastSeen = ctx.Timestamp,
            });
            return;
        }
        ctx.Db.Indicator.Insert(new Indicator
        {
            Id = 0, IncidentId = incidentId, Kind = kind, Value = v, Context = Clip(context, 300), Confidence = confidence,
            Sources = Clip(source, 500), FirstSeen = ctx.Timestamp, LastSeen = ctx.Timestamp,
        });
    }

    /// The parser records how far it has read each source, so every row is processed exactly once.
    [SpacetimeDB.Reducer]
    public static void AdvanceParserCursor(ReducerContext ctx, string source, ulong lastId)
    {
        Require(ctx, Role.Parser);
        var row = ctx.Db.ParserCursor.Source.Find(source);
        if (row is null) ctx.Db.ParserCursor.Insert(new ParserCursor { Source = source, LastId = lastId });
        else if (lastId > row.Value.LastId) ctx.Db.ParserCursor.Source.Update(row.Value with { LastId = lastId });
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n];

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
    public static void PostAiMessage(ReducerContext ctx, ulong incidentId, string text, string modelLabel)
    {
        var u = Require(ctx, Role.Agent, Role.Reviewer);
        ctx.Db.Message.Insert(new Message { Id = 0, IncidentId = incidentId, Author = u.Username, AuthorKind = AuthorKind.Assistant, Text = text, At = ctx.Timestamp, ModelLabel = modelLabel });
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

        // Order only matters for steps that CHANGE something: read-only steps can be approved (and run)
        // together, but a containment or destructive step waits until every earlier step has finished.
        if (a.PlanId != 0 && a.Tier != Tier.ReadOnly)
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
        MaybeFollowUp(ctx, a.PlanId);
    }

    const byte MaxAutoFollowUps = 4;

    /// Multi-step loop: when the last step of the active plan finishes, wake the agent with the results.
    /// Capped, so an agent proposing read-only steps can't loop forever with auto-approve on.
    static void MaybeFollowUp(ReducerContext ctx, ulong planId)
    {
        if (planId == 0) return;
        var plan = ctx.Db.Plan.Id.Find(planId);
        if (plan is null || !plan.Value.Active) return;
        bool unfinished = ctx.Db.ResponseAction.PlanId.Filter(planId).Any(x =>
            x.Status is ActionStatus.Proposed or ActionStatus.Approved or ActionStatus.Running);
        if (unfinished) return;

        var req = ctx.Db.AgentRequest.Id.Find(plan.Value.RequestId);
        byte depth = (byte)((req?.Depth ?? 0) + 1);
        var model = req?.Model ?? "";
        var incidentId = plan.Value.IncidentId;
        if (depth > MaxAutoFollowUps)
        {
            ctx.Db.Message.Insert(new Message
            {
                Id = 0, IncidentId = incidentId, Author = "system", AuthorKind = AuthorKind.System, At = ctx.Timestamp, ModelLabel = "",
                Text = $"The agent has re-planned {MaxAutoFollowUps} times on its own. Ask it to continue when you're ready.",
            });
            return;
        }
        ctx.Db.AgentRequest.Insert(new AgentRequest
        {
            Id = 0, IncidentId = incidentId, FromUser = "system", At = ctx.Timestamp, Status = RequestStatus.Pending,
            Text = "All steps of your last plan have finished. Review their output and decide the next steps.",
            Model = model, Depth = depth,
        });
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
                At = ctx.Timestamp, Status = RequestStatus.Pending, Model = "", Depth = 0,
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
