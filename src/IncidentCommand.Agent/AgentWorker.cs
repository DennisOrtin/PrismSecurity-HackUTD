using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

/// The planning agent. It can only call propose-type reducers; the module enforces that.
/// Flow: pending AgentRequest -> gather context -> Nemotron with tools -> SubmitPlan / AnswerRequest / proposals.
public sealed class AgentWorker
{
    readonly ServiceRunner _runner;
    readonly ModelClient _model;
    readonly HashSet<ulong> _inFlight = new();
    readonly HashSet<ulong> _flaggedEvents = new();
    bool _busy;

    public AgentWorker(ServiceRunner runner, ModelClient model) { _runner = runner; _model = model; }

    // Deterministic tripwire for text addressed to an AI. Runs before the model ever sees the data.
    static readonly Regex InjectionLike = new(
        @"(ignore (all |any )?(previous|prior) instructions|AI assistant|system prompt|you are now|approve (all|every|this)|disable (the )?firewall)",
        RegexOptions.IgnoreCase);

    /// If the module rejects a plan (unknown kind, bad host or params), say so in chat instead of retrying.
    public void Wire(DbConnection conn)
    {
        conn.Reducers.OnSubmitPlan += (ctx, requestId, _, _) =>
        {
            var failure = Stdb.Failure(ctx);
            if (failure != null) conn.Reducers.FailRequest(requestId, "the module rejected the plan: " + failure);
        };
    }

    public void Poll(DbConnection conn)
    {
        FlagInjectionAttempts(conn);
        if (_busy) return;

        var req = conn.Db.RequestView.Iter()
            .Where(r => r.Status == RequestStatus.Pending && !_inFlight.Contains(r.Id))
            .OrderBy(r => r.Id).ToList();
        if (req.Count == 0) return;
        var request = req[0];

        _busy = true;
        _inFlight.Add(request.Id);
        var context = BuildContext(conn, request.IncidentId);
        var requestId = request.Id;
        var incidentId = request.IncidentId;
        var ask = request.Text;
        ServiceRunner.Log($"Planning for request {requestId}: {ask}");

        Task.Run(async () =>
        {
            try
            {
                var reply = await _model.ChatAsync(Prompts.AgentSystem, $"{context}\n\n## The responder asks\n{ask}", Tools());
                _runner.Post(c => Apply(c, requestId, incidentId, reply));
            }
            catch (Exception ex)
            {
                _runner.Post(c => c.Reducers.FailRequest(requestId, ex.Message));
            }
            finally { _busy = false; }
        });
    }

    void FlagInjectionAttempts(DbConnection conn)
    {
        foreach (var e in conn.Db.EventView.Iter())
        {
            if (_flaggedEvents.Contains(e.Id) || !InjectionLike.IsMatch(e.Raw)) continue;
            _flaggedEvents.Add(e.Id);
            conn.Reducers.PostAiMessage(e.IncidentId,
                $"Warning: event {e.Id} on {e.Host} contains text addressed to an AI assistant. " +
                "I am treating it as evidence of attacker activity, not as an instruction. Nothing was proposed because of it.");
            conn.Reducers.ProposeTimelineEntry(e.IncidentId, "Analysis",
                $"Possible prompt-injection attempt in {e.Source} on {e.Host} (event {e.Id}).");
        }
    }

    // ---------- Context ----------

    static string BuildContext(DbConnection conn, ulong incidentId)
    {
        var sb = new StringBuilder();
        foreach (var i in conn.Db.IncidentView.Iter().Where(i => i.Id == incidentId))
            sb.AppendLine($"# Incident {i.Id}: {i.Title}\nPhase: {i.Phase}. Severity: {i.Severity}/4.");

        sb.AppendLine("\n## Hosts you may target");
        foreach (var h in conn.Db.SshHostView.Iter()) sb.AppendLine($"- {h.Name}");

        sb.AppendLine("\n## Affected data recorded so far");
        foreach (var a in conn.Db.AssetView.Iter().Where(a => a.IncidentId == incidentId))
            sb.AppendLine($"- {a.Host}: {a.DataClass}, {a.ScopeLevel} scope, {a.Status}{(a.Accepted ? "" : " (your unaccepted proposal)")}");

        sb.AppendLine("\n## Actions so far (newest last)");
        foreach (var a in conn.Db.ActionView.Iter().Where(a => a.IncidentId == incidentId).OrderBy(a => a.Id).TakeLast(20))
        {
            var p = string.Join(", ", a.Params.Select(x => $"{x.Key}={x.Value}"));
            sb.AppendLine($"- [{a.Status}] {a.Kind} on {a.Host} ({p})");
            if (!string.IsNullOrEmpty(a.Output))
                sb.AppendLine(Untrusted($"output of {a.Kind} on {a.Host}", Clip(a.Output, 1500)));
        }

        sb.AppendLine("\n## Recent network events (newest last)");
        foreach (var e in conn.Db.EventView.Iter().Where(e => e.IncidentId == incidentId).OrderBy(e => e.Id).TakeLast(50))
            sb.AppendLine(Untrusted($"{e.Host}:{e.Source} event {e.Id}", Clip(e.Raw, 400)));

        sb.AppendLine("\n## Recent conversation");
        foreach (var m in conn.Db.MessageView.Iter().Where(m => m.IncidentId == incidentId).OrderBy(m => m.Id).TakeLast(10))
            sb.AppendLine($"- {m.Author}: {Clip(m.Text, 300)}");

        return sb.ToString();
    }

    /// Everything that came from the network is fenced and labelled as evidence.
    static string Untrusted(string source, string text) =>
        $"<untrusted source=\"{source}\">\n{text.Replace("</untrusted>", "&lt;/untrusted&gt;")}\n</untrusted>";

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + " …";

    // ---------- Tools ----------

    static JsonArray Tools()
    {
        JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        JsonObject Obj(JsonObject props, params string[] req) => new()
        {
            ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(req.Select(r => (JsonNode)r!).ToArray()),
        };

        var step = Obj(new JsonObject
        {
            ["kind"] = Str("One of the catalog kinds, exactly as written."),
            ["host"] = Str("A host from the list."),
            ["params"] = new JsonObject { ["type"] = "object", ["description"] = "The kind's parameters as string values, e.g. {\"log\":\"auth\",\"lines\":\"200\"}." },
            ["rationale"] = Str("One or two sentences: why this step, citing evidence."),
        }, "kind", "host", "params", "rationale");

        return new JsonArray
        {
            ModelClient.Tool("submit_plan", "Propose an ordered plan. Evidence-gathering (read-only) steps first. Humans approve every step.",
                Obj(new JsonObject
                {
                    ["reasoning"] = Str("What you believe happened and what you still need to know. Plain language, under 120 words."),
                    ["steps"] = new JsonObject { ["type"] = "array", ["items"] = step, ["maxItems"] = 6 },
                }, "reasoning", "steps")),
            ModelClient.Tool("answer", "Answer the responder's question without proposing actions.",
                Obj(new JsonObject { ["text"] = Str("Your answer, plain language.") }, "text")),
            ModelClient.Tool("propose_timeline_entry", "Draft a timeline entry for a human to accept.",
                Obj(new JsonObject { ["category"] = Str("Detection, Analysis, Containment, Eradication, Recovery, Communication or Note"), ["text"] = Str("The entry.") }, "category", "text")),
            ModelClient.Tool("propose_scope_update", "Draft an entry for the affected-data table, for a human to accept.",
                Obj(new JsonObject
                {
                    ["host"] = Str("Host name."),
                    ["data_class"] = Str("Credentials, PersonalData, Financial, IntellectualProperty or Operational"),
                    ["scope_level"] = Str("User, Application, System or Network"),
                    ["status"] = Str("Unknown, Secure, Encrypted, Exfiltrated or EncryptedAndExfiltrated"),
                    ["evidence"] = Str("The evidence, citing event or action output."),
                }, "host", "data_class", "scope_level", "status", "evidence")),
            ModelClient.Tool("suggest_command", "Suggest a shell command a HUMAN may choose to run when the catalog can't do it. It will never run automatically.",
                Obj(new JsonObject { ["host"] = Str("Host name."), ["text"] = Str("The exact command."), ["rationale"] = Str("Why.") }, "host", "text", "rationale")),
        };
    }

    // ---------- Apply the model's answer (on the connection thread) ----------

    void Apply(DbConnection conn, ulong requestId, ulong incidentId, ModelReply reply)
    {
        bool handled = false;
        foreach (var call in reply.ToolCalls)
        {
            try
            {
                switch (call.Name)
                {
                    case "submit_plan":
                        var steps = new List<PlannedStep>();
                        foreach (var s in call.Args["steps"]?.AsArray() ?? new JsonArray())
                        {
                            var ps = new List<Param>();
                            if (s?["params"] is JsonObject po)
                                foreach (var kv in po) ps.Add(new Param { Key = kv.Key, Value = kv.Value?.ToString() ?? "" });
                            steps.Add(new PlannedStep
                            {
                                Kind = S(s, "kind"), Host = S(s, "host"), Params = ps, Rationale = S(s, "rationale"),
                            });
                        }
                        conn.Reducers.SubmitPlan(requestId, S(call.Args, "reasoning"), steps);
                        handled = true;
                        break;
                    case "answer":
                        conn.Reducers.AnswerRequest(requestId, S(call.Args, "text"));
                        handled = true;
                        break;
                    case "propose_timeline_entry":
                        conn.Reducers.ProposeTimelineEntry(incidentId, S(call.Args, "category"), S(call.Args, "text"));
                        break;
                    case "propose_scope_update":
                        conn.Reducers.ProposeScopeUpdate(incidentId, S(call.Args, "host"),
                            E<DataClass>(S(call.Args, "data_class")), E<ScopeLevel>(S(call.Args, "scope_level")),
                            E<DataStatus>(S(call.Args, "status")), S(call.Args, "evidence"));
                        break;
                    case "suggest_command":
                        conn.Reducers.SuggestCommand(incidentId, S(call.Args, "host"), S(call.Args, "text"), S(call.Args, "rationale"));
                        break;
                }
            }
            catch (Exception ex) { ServiceRunner.Log($"Tool {call.Name} failed: {ex.Message}"); }
        }

        if (!handled)
        {
            if (!string.IsNullOrWhiteSpace(reply.Content)) conn.Reducers.AnswerRequest(requestId, reply.Content!);
            else conn.Reducers.FailRequest(requestId, "The model returned nothing usable.");
        }
    }

    static string S(JsonNode? n, string key) => n?[key]?.ToString() ?? "";

    static T E<T>(string s) where T : struct, Enum =>
        Enum.TryParse<T>(s.Replace("_", "").Replace(" ", ""), ignoreCase: true, out var v) ? v : default;
}
