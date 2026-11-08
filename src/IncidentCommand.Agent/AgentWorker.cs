using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

/// The response agent: talks with the responder and proposes actions. Propose-only (the module enforces it).
/// Flow: pending AgentRequest -> context -> local Nemotron with tools -> validate steps -> SubmitPlan / AnswerRequest.
/// Recording findings (summary, scope, indicators, timeline) is the parser's job, not this one's.
public sealed class AgentWorker
{
    readonly ServiceRunner _runner;
    readonly ModelClient _model;
    readonly HashSet<ulong> _inFlight = new();
    readonly HashSet<ulong> _flaggedEvents = new();
    volatile bool _busy;

    public AgentWorker(ServiceRunner runner, ModelClient model) { _runner = runner; _model = model; }

    // Deterministic tripwire for text addressed to an AI. Runs before any model sees the data.
    static readonly Regex InjectionLike = new(
        @"(ignore (all |any )?(previous|prior) instructions|AI assistant|system prompt|you are now|approve (all|every|this)|disable (the )?firewall)",
        RegexOptions.IgnoreCase);

    /// If the module still rejects a plan, say so in chat instead of retrying forever.
    public void Wire(DbConnection conn)
    {
        conn.Reducers.OnSubmitPlan += (ctx, requestId, _, _, _) =>
        {
            var failure = Stdb.Failure(ctx);
            if (failure != null) conn.Reducers.FailRequest(requestId, "the module rejected the plan: " + failure);
        };
    }

    readonly HashSet<ulong> _appliedHeld = new();

    public void Poll(DbConnection conn)
    {
        FlagInjectionAttempts(conn);

        // Continue was pressed and the answer was already prepared: apply it now. No model call, so it's instant.
        foreach (var r in conn.Db.RequestView.Iter()
                     .Where(r => r.Held && r.Released && r.Status == RequestStatus.Ready && !_appliedHeld.Contains(r.Id)).ToList())
        {
            _appliedHeld.Add(r.Id);
            var (stored, storedLabel) = HeldReply.Read(r.HeldResult);
            var knownHosts = conn.Db.SshHostView.Iter().Select(h => h.Name).ToHashSet();
            ServiceRunner.Log($"Continue pressed: applying the prepared answer for request {r.Id}.");
            Apply(conn, r.Id, r.IncidentId, stored, storedLabel, knownHosts);
        }

        if (_busy) return;

        var pending = conn.Db.RequestView.Iter()
            .Where(r => r.Status == RequestStatus.Pending && !_inFlight.Contains(r.Id))
            .OrderBy(r => r.Id).ToList();
        if (pending.Count == 0) return;
        var request = pending[0];

        _busy = true;
        _inFlight.Add(request.Id);
        var requestId = request.Id;
        var incidentId = request.IncidentId;
        var ask = request.Text;
        var hosts = conn.Db.SshHostView.Iter().Select(h => h.Name).ToHashSet();
        var context = BuildContext(conn, incidentId, hosts);

        // Model is read from the database on EVERY call, so the commander's choice applies immediately.
        var model = Choose(conn, request.Model);
        var label = $"{AiNames.Name(model)} · agent";
        var held = request.Held;
        var purpose = request.Purpose ?? "";
        ServiceRunner.Log($"Request {requestId}{(held ? " (held: prepared in the background)" : "")} with {AiNames.Name(model)}: {ask}");

        Task.Run(async () =>
        {
            try
            {
                if (purpose == "closing_summary")
                {
                    var report = await _model.ChatAsync(model, Prompts.ClosingReportSystem, context,
                        maxTokens: Math.Max(1200, Settings.Current.Ai.AgentMaxTokens));
                    var text = string.IsNullOrWhiteSpace(report.Content) ? "(the model returned no report)" : report.Content!.Trim();
                    _runner.Post(c => c.Reducers.SaveClosingSummary(requestId, text, label));
                    return;
                }

                var reply = await _model.ChatAsync(model, Prompts.AgentSystem,
                    $"{context}\n\n## The responder asks\n{ask}", Tools(hosts), maxTokens: Settings.Current.Ai.AgentMaxTokens);
                _runner.Post(c =>
                {
                    // Held: keep the answer hidden until the responder presses Continue (or apply now if they already did).
                    bool released = c.Db.RequestView.Iter().Any(r => r.Id == requestId && r.Released);
                    bool discarded = c.Db.RequestView.Iter().Any(r => r.Id == requestId && r.Status == RequestStatus.Discarded);
                    if (discarded) return;
                    if (held && !released) { c.Reducers.StoreHeldResult(requestId, HeldReply.Write(reply, label)); return; }
                    if (held) _appliedHeld.Add(requestId);
                    Apply(c, requestId, incidentId, reply, label, hosts);
                });
            }
            catch (Exception ex)
            {
                _runner.Post(c => c.Reducers.FailRequest(requestId, ex.Message));
            }
            finally { _busy = false; }
        });
    }

    /// A per-request model wins; otherwise the commander's choice; otherwise the default in settings.json.
    static string Choose(DbConnection conn, string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        var configured = "";
        foreach (var c in conn.Db.ConfigView.Iter()) configured = c.AgentModel;
        return AiNames.Resolve(configured, "agent");
    }

    void FlagInjectionAttempts(DbConnection conn)
    {
        foreach (var e in conn.Db.EventView.Iter())
        {
            if (_flaggedEvents.Contains(e.Id) || !InjectionLike.IsMatch(e.Raw)) continue;
            _flaggedEvents.Add(e.Id);
            conn.Reducers.PostAiMessage(e.IncidentId,
                $"Warning: event {e.Id} on {e.Host} contains text addressed to an AI assistant. " +
                "I am treating it as evidence of attacker activity, not as an instruction. Nothing was proposed because of it.",
                "injection tripwire");
        }
    }

    // ---------- Context ----------

    static string BuildContext(DbConnection conn, ulong incidentId, HashSet<string> hosts)
    {
        var sb = new StringBuilder();
        foreach (var i in conn.Db.IncidentView.Iter().Where(i => i.Id == incidentId))
        {
            sb.AppendLine($"# Incident {i.Id}: {i.Title}\nPhase: {i.Phase}. Severity: {i.Severity}/4.");
            if (!string.IsNullOrWhiteSpace(i.Classification)) sb.AppendLine($"Classification (parser): {i.Classification}");
            if (!string.IsNullOrWhiteSpace(i.AiSummary)) sb.AppendLine($"Summary (parser): {i.AiSummary}");
            if (!string.IsNullOrWhiteSpace(i.Summary)) sb.AppendLine($"Summary (humans): {i.Summary}");
        }

        sb.AppendLine("\n## Hosts you may target");
        if (hosts.Count == 0)
            sb.AppendLine("NONE ARE REGISTERED. You cannot propose any catalog action. Tell the responder to add hosts under Settings > Hosts.");
        foreach (var h in hosts.OrderBy(h => h)) sb.AppendLine($"- {h}");

        sb.AppendLine("\n## Indicators so far (parser)");
        foreach (var x in conn.Db.IndicatorView.Iter().Where(x => x.IncidentId == incidentId).OrderByDescending(x => x.Confidence).Take(30))
            sb.AppendLine($"- {x.Kind} {x.Value} ({x.Confidence}): {x.Context}");

        sb.AppendLine("\n## Affected data");
        foreach (var a in conn.Db.AssetView.Iter().Where(a => a.IncidentId == incidentId))
            sb.AppendLine($"- {a.Host}: {a.DataClass}, {a.ScopeLevel} scope, {a.Status}{(a.Accepted ? "" : " (proposed, not yet accepted)")}");

        var mgmt = Settings.Current.Ai.ManagementAddresses.ToList();
        foreach (var c in conn.Db.ConfigView.Iter()) if (!string.IsNullOrWhiteSpace(c.ResponderAddress)) mgmt.Add(c.ResponderAddress);
        sb.AppendLine("\n## Your own footprint on the hosts (never treat as attacker activity)");
        sb.AppendLine($"- SSH connections from {string.Join(", ", mgmt.Distinct())} are the executor's own management sessions.");
        sb.AppendLine("- 'tail -n 0 -F' processes are YOUR log followers. Killing them stops the event stream.");
        sb.AppendLine("- Your containment changes system files: lock_account edits /etc/shadow and /etc/gshadow (and their '-' backups), " +
                      "block_ip/isolate_host change iptables. Changes you caused are not attacker activity.");
        sb.AppendLine("- Lines marked [baseline] or [responder] in outputs below are yours or normal for these hosts.");

        sb.AppendLine("\n## Actions so far (newest last)");
        foreach (var a in conn.Db.ActionView.Iter().Where(a => a.IncidentId == incidentId).OrderBy(a => a.Id).TakeLast(20))
        {
            var p = string.Join(", ", a.Params.Select(x => $"{x.Key}={x.Value}"));
            sb.AppendLine($"- action {a.Id} [{a.Status}] {a.Kind} on {a.Host} ({p})");
            if (!string.IsNullOrEmpty(a.Output))
                sb.AppendLine(Untrusted($"output of action {a.Id}", Clip(Annotate(a.Kind, a.Output, mgmt), 1500)));
        }

        sb.AppendLine("\n## Human terminal (commands the responder ran by hand, newest last)");
        var outputs = conn.Db.TerminalOutputView.Iter().ToList();
        foreach (var t in conn.Db.TerminalCommandView.Iter().Where(t => t.IncidentId == incidentId).OrderBy(t => t.Id).TakeLast(6))
        {
            var text = string.Concat(outputs.Where(o => o.CommandId == t.Id).OrderBy(o => o.Id).Select(o => o.Chunk));
            sb.AppendLine($"- {t.Host}$ {t.Command}  [{t.Status}, exit {t.ExitCode}]{(t.SuggestionId != 0 ? " (your suggestion)" : "")}");
            if (text.Length > 0) sb.AppendLine(Untrusted($"terminal output on {t.Host}", Clip(text, 1200)));
        }

        sb.AppendLine("\n## Recent network events (newest last)");
        foreach (var e in conn.Db.EventView.Iter().Where(e => e.IncidentId == incidentId).OrderBy(e => e.Id).TakeLast(40))
            sb.AppendLine(Untrusted($"event {e.Id}, {e.Host} {e.Source}", Clip(e.Raw, 400)));

        sb.AppendLine("\n## Recent conversation");
        foreach (var m in conn.Db.MessageView.Iter().Where(m => m.IncidentId == incidentId).OrderBy(m => m.Id).TakeLast(10))
            sb.AppendLine($"- {m.Author}: {Clip(m.Text, 300)}");

        return sb.ToString();
    }

    /// Marks lines that are the responder's own footprint, so a small model doesn't "contain" itself.
    public static string Annotate(string kind, string output, List<string> mgmt)
    {
        if (kind is not ("list_processes" or "list_connections")) return output;
        var baseline = Settings.Current.Ai.BaselineProcesses;
        var lines = output.Split('\n').Select(line =>
        {
            if (kind == "list_connections" && mgmt.Any(m => line.Contains($" {m}:"))) return line + "   [responder management SSH]";
            if (kind == "list_processes" && line.Contains("tail -n 0 -F")) return line + "   [responder: your log follower]";
            if (kind == "list_processes" && baseline.Any(b => line.Contains(b))) return line + "   [baseline]";
            return line;
        });
        return string.Join("\n", lines);
    }

    public static string Untrusted(string source, string text) =>
        $"<untrusted source=\"{source}\">\n{text.Replace("</untrusted>", "&lt;/untrusted&gt;")}\n</untrusted>";

    public static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + " …";

    // ---------- Tools ----------

    /// The catalog kinds and the registered hosts are listed as fixed choices, so in "json" mode the model
    /// literally cannot write an unknown action or invent a host name. With no hosts, only "answer" is offered.
    static JsonArray Tools(HashSet<string> hosts)
    {
        JsonObject OneOf(string description, IEnumerable<string> values) => new()
        {
            ["type"] = "string", ["description"] = description,
            ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()),
        };
        var answer = ModelClient.Tool("answer", "Reply to the responder without proposing actions.",
            ModelClient.Obj(new JsonObject { ["text"] = ModelClient.Str("Your answer, plain language.") }, "text"));
        if (hosts.Count == 0) return new JsonArray { answer };

        var hostChoice = OneOf("A registered host, exactly as written.", hosts.OrderBy(h => h));
        var step = ModelClient.Obj(new JsonObject
        {
            ["kind"] = OneOf("A catalog action.", Catalog.Keys),
            ["host"] = hostChoice.DeepClone(),
            ["params"] = new JsonObject { ["type"] = "object", ["description"] = "The kind's parameters as string values, e.g. {\"log\":\"auth\",\"lines\":\"200\"}. {} if none." },
            ["rationale"] = ModelClient.Str("One or two sentences: why this step, citing evidence ids."),
        }, "kind", "host", "params", "rationale");

        return new JsonArray
        {
            ModelClient.Tool("submit_plan", "Propose an ordered plan of catalog actions. Humans approve every step.",
                ModelClient.Obj(new JsonObject
                {
                    ["reasoning"] = ModelClient.Str("What you believe happened and what you still need to know. Plain language, under 120 words."),
                    ["steps"] = new JsonObject { ["type"] = "array", ["items"] = step, ["maxItems"] = 6 },
                }, "reasoning", "steps")),
            answer,
            ModelClient.Tool("suggest_command", "Put an exact shell command in the human-only terminal's suggestion list, for one registered host. Use when the responder asks for a command, or the catalog can't do what they want. The human reviews and runs it; it never runs automatically.",
                ModelClient.Obj(new JsonObject
                {
                    ["host"] = hostChoice.DeepClone(),
                    ["text"] = ModelClient.Str("The exact command."),
                    ["rationale"] = ModelClient.Str("Why."),
                }, "host", "text", "rationale")),
        };
    }

    // ---------- Step validation (mirrors the module, so one bad step doesn't sink the whole plan) ----------

    static readonly Dictionary<string, string[]> Catalog = new()
    {
        ["collect_logs"] = new[] { "log", "lines" }, ["follow_logs"] = new[] { "log" },
        ["list_processes"] = Array.Empty<string>(), ["list_connections"] = Array.Empty<string>(),
        ["capture_traffic"] = new[] { "seconds" }, ["hash_files"] = new[] { "dir" },
        ["kill_process"] = new[] { "pid" }, ["lock_account"] = new[] { "username" }, ["block_ip"] = new[] { "ip" },
        ["isolate_host"] = Array.Empty<string>(), ["shutdown_host"] = Array.Empty<string>(),
        ["check_egress"] = new[] { "ip", "port" }, ["quarantine_file"] = new[] { "path" }, ["restore_from_backup"] = new[] { "dir" },
    };

    static string? Problem(string kind, string host, Dictionary<string, string> ps, HashSet<string> hosts)
    {
        if (!Catalog.TryGetValue(kind, out var keys)) return $"'{kind}' is not a catalog action";
        if (!hosts.Contains(host)) return $"'{host}' is not a registered host";
        foreach (var k in keys) if (!ps.ContainsKey(k)) return $"missing parameter '{k}'";
        foreach (var (k, v) in ps)
        {
            bool ok = k switch
            {
                "log" => v is "auth" or "syslog" or "app",
                "lines" => int.TryParse(v, out var n) && n is >= 1 and <= 2000,
                "seconds" => int.TryParse(v, out var n) && n is >= 1 and <= 120,
                "pid" => int.TryParse(v, out var n) && n > 1,
                "username" => Regex.IsMatch(v, "^[a-z_][a-z0-9_-]{0,31}$"),
                "ip" => Regex.IsMatch(v, @"^(\d{1,3}\.){3}\d{1,3}$") && v.Split('.').All(o => int.Parse(o) <= 255),
                "dir" => Regex.IsMatch(v, @"^/[A-Za-z0-9/_.-]{0,199}$") && !v.Contains(".."),
                "path" => Regex.IsMatch(v, @"^/[A-Za-z0-9/_.-]{0,199}$") && !v.Contains("..") && !v.EndsWith("/"),
                "port" => int.TryParse(v, out var n) && n is >= 1 and <= 65535,
                _ => true,
            };
            if (!ok) return $"bad value for '{k}': {v}";
        }
        return null;
    }

    // ---------- Apply the model's answer (on the connection thread) ----------

    void Apply(DbConnection conn, ulong requestId, ulong incidentId, ModelReply reply, string label, HashSet<string> hosts)
    {
        bool handled = false;
        var suggested = new List<string>();
        var skipped = new List<string>();
        var calls = reply.ToolCalls.Count > 0 ? reply.ToolCalls : ModelClient.ToolCallsFromContent(reply.Content, "submit_plan");
        bool callsFromText = reply.ToolCalls.Count == 0 && calls.Count > 0;   // then Content IS the JSON: don't echo it

        foreach (var call in calls)
        {
            try
            {
                switch (call.Name)
                {
                    case "submit_plan":
                        SubmitValidatedPlan(conn, requestId, call.Args, label, hosts);
                        handled = true;
                        break;
                    case "answer":
                        conn.Reducers.AnswerRequest(requestId, S(call.Args, "text"), label);
                        handled = true;
                        break;
                    case "suggest_command":
                        var sh = S(call.Args, "host").Trim();
                        var text = S(call.Args, "text").Trim();
                        if (text.Length == 0) break;
                        if (hosts.Contains(sh))
                        {
                            conn.Reducers.SuggestCommand(incidentId, sh, text, S(call.Args, "rationale"));
                            suggested.Add($"{sh}: {text}");
                        }
                        else skipped.Add($"'{sh}' is not a registered host");
                        break;
                    default:
                        ServiceRunner.Log($"Ignored unknown tool '{call.Name}'.");
                        break;
                }
            }
            catch (Exception ex) { ServiceRunner.Log($"Tool {call.Name} failed: {ex.Message}"); }
        }

        // Tell the responder where suggested commands went, in plain words. Suggestions alone are a full answer.
        var terminalNote = suggested.Count == 0 ? "" :
            $"I put {suggested.Count} command(s) under Terminal > \"Commands the AI suggests\" " +
            $"({string.Join("; ", suggested)}). Check the reviewer's warning, then copy it into the terminal and run it yourself.";
        if (skipped.Count > 0)
            terminalNote += $" (Skipped {skipped.Count} suggestion(s): {string.Join("; ", skipped)}.)";

        // If the reply looked like a tool call but couldn't be read, say so clearly AND keep the raw reply,
        // so it can be copied out for debugging.
        if (!handled && suggested.Count == 0 && reply.Content?.TrimStart().StartsWith("{") == true)
        {
            var raw = reply.Content!.Trim();
            if (raw.Length > 4000) raw = raw[..4000] + " …[truncated]";
            conn.Reducers.FailRequest(requestId,
                $"its reply couldn't be read as a plan, so nothing was proposed. Ask again, or pick a different agent " +
                $"model in Settings > AI. Raw model reply ({label}):\n\n{raw}");
            ServiceRunner.Log($"Unreadable reply for request {requestId}: {raw}");
            return;
        }

        if (!handled)
        {
            var spoken = callsFromText ? null : reply.Content?.Trim();
            var text = string.Join("\n\n", new[] { spoken, terminalNote }.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (!string.IsNullOrWhiteSpace(text)) conn.Reducers.AnswerRequest(requestId, text, label);
            else conn.Reducers.FailRequest(requestId, "The model returned nothing usable.");
        }
        else if (terminalNote.Length > 0)
        {
            conn.Reducers.PostAiMessage(incidentId, terminalNote, label);
        }
    }

    static void SubmitValidatedPlan(DbConnection conn, ulong requestId, JsonObject args, string label, HashSet<string> hosts)
    {
        var reasoning = S(args, "reasoning");
        var steps = new List<PlannedStep>();
        var dropped = new List<string>();

        foreach (var s in args["steps"]?.AsArray() ?? new JsonArray())
        {
            var kind = S(s, "kind").Trim();
            var host = S(s, "host").Trim();
            var ps = new Dictionary<string, string>();
            if (s?["params"] is JsonObject po)
                foreach (var kv in po) ps[kv.Key] = kv.Value?.ToString() ?? "";
            // Drop parameters the kind doesn't take (common with small models).
            if (Catalog.TryGetValue(kind, out var keys))
                foreach (var extra in ps.Keys.Where(k => !keys.Contains(k)).ToList()) ps.Remove(extra);

            var problem = Problem(kind, host, ps, hosts);
            if (problem != null) { dropped.Add($"{kind} on {host}: {problem}"); continue; }
            steps.Add(new PlannedStep
            {
                Kind = kind, Host = host, Rationale = S(s, "rationale"),
                Params = ps.Select(kv => new Param { Key = kv.Key, Value = kv.Value }).ToList(),
            });
        }

        var note = dropped.Count == 0 ? "" : $"\n\n(Dropped {dropped.Count} step(s) that can't run: {string.Join("; ", dropped)}.)";
        if (steps.Count > 0)
        {
            var nextStep = "\n\nWaiting for your approval on the Actions screen.";
            conn.Reducers.SubmitPlan(requestId, reasoning + note + nextStep, steps, label);
        }
        else
        {
            var hint = hosts.Count == 0 ? "\n\nNo hosts are registered yet: add them under Settings > Hosts so actions can run." : "";
            conn.Reducers.AnswerRequest(requestId, reasoning + note + hint, label);
        }
    }

    public static string S(JsonNode? n, string key) => n?[key]?.ToString() ?? "";
}
