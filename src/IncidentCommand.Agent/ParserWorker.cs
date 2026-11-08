using System.Text;
using System.Text.Json.Nodes;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

/// The parser: reads EVERYTHING added to the incident (events, chat from humans and AIs, timeline entries,
/// finished action output, terminal output) and keeps the structured record current: AI summary and
/// classification, indicators, proposed scope, key timeline moments. Each row is read exactly once,
/// tracked by per-source cursors stored in the database. Its writes never trigger actions.
public sealed class ParserWorker
{
    readonly ServiceRunner _runner;
    readonly ModelClient _model;
    readonly HashSet<string> _seen = new();      // items already sent to the model this session
    volatile bool _busy;
    DateTime _lastPass = DateTime.MinValue;

    static readonly TimeSpan MinGap = TimeSpan.FromSeconds(8);   // batch bursts of events into one pass
    const int MaxItems = 40;
    const int MaxChars = 12000;

    public ParserWorker(ServiceRunner runner, ModelClient model) { _runner = runner; _model = model; }

    sealed record Item(string Source, ulong Id, ulong IncidentId, string Text);

    public void Poll(DbConnection conn)
    {
        if (_busy || DateTime.UtcNow - _lastPass < MinGap) return;

        var items = NewItems(conn);
        if (items.Count == 0) return;

        // One incident per pass (normally there is only one open).
        var incidentId = items[0].IncidentId;
        var batch = new List<Item>();
        int chars = 0;
        foreach (var it in items.Where(i => i.IncidentId == incidentId))
        {
            if (batch.Count >= MaxItems || chars + it.Text.Length > MaxChars) break;
            batch.Add(it);
            chars += it.Text.Length;
        }

        var configured = "";
        foreach (var c in conn.Db.ConfigView.Iter()) configured = c.ParserModel;
        var model = AiNames.Resolve(configured, "parser");
        var prompt = $"{CurrentRecord(conn, incidentId)}\n\n## NEW ITEMS ({batch.Count})\n{string.Join("\n", batch.Select(b => b.Text))}";

        _busy = true;
        _lastPass = DateTime.UtcNow;
        foreach (var b in batch) _seen.Add($"{b.Source}:{b.Id}");
        ServiceRunner.Log($"Parsing {batch.Count} new item(s) for incident {incidentId} with {AiNames.Name(model)}");

        Task.Run(async () =>
        {
            try
            {
                var reply = await _model.ChatAsync(model, Prompts.ParserSystem, prompt, Tools(), temperature: 0.1,
                    maxTokens: Settings.Current.Ai.ParserMaxTokens);
                _runner.Post(c => Apply(c, incidentId, reply, batch));
            }
            catch (Exception ex)
            {
                ServiceRunner.Log("Parser pass failed (will retry these items): " + ex.Message);
                _runner.Post(_ => { foreach (var b in batch) _seen.Remove($"{b.Source}:{b.Id}"); });
            }
            finally { _busy = false; }
        });
    }

    // ---------- What's new since the cursors ----------

    List<Item> NewItems(DbConnection conn)
    {
        var cursor = conn.Db.ParserCursorView.Iter().ToDictionary(c => c.Source, c => c.LastId);
        ulong After(string s) => cursor.TryGetValue(s, out var v) ? v : 0;
        bool Fresh(string s, ulong id) => id > After(s) && !_seen.Contains($"{s}:{id}");
        var list = new List<Item>();

        foreach (var e in conn.Db.EventView.Iter().Where(e => Fresh("event", e.Id)))
            list.Add(new Item("event", e.Id, e.IncidentId,
                AgentWorker.Untrusted($"event {e.Id}, {e.Host} {e.Source}, severity {e.Severity}", AgentWorker.Clip(e.Raw, 500))));

        foreach (var m in conn.Db.MessageView.Iter().Where(m => Fresh("message", m.Id)))
        {
            var who = m.AuthorKind switch { AuthorKind.Human => $"human {m.Author}", AuthorKind.Assistant => $"AI {m.Author}", _ => "system" };
            list.Add(new Item("message", m.Id, m.IncidentId, $"- message {m.Id} from {who}: {AgentWorker.Clip(m.Text, 800)}"));
        }

        foreach (var t in conn.Db.TimelineView.Iter().Where(t => t.Author != "parser" && Fresh("timeline", t.Id)))
            list.Add(new Item("timeline", t.Id, t.IncidentId,
                $"- timeline {t.Id} [{t.Category}] by {t.Author}{(t.AuthorKind == AuthorKind.Assistant ? " (AI)" : "")}: {AgentWorker.Clip(t.Text, 500)}"));

        // Actions only once they have finished (their output is the interesting part).
        foreach (var a in conn.Db.ActionView.Iter().Where(a =>
                     (a.Status is ActionStatus.Succeeded or ActionStatus.Failed or ActionStatus.Denied) && Fresh("action", a.Id)))
        {
            var p = string.Join(", ", a.Params.Select(x => $"{x.Key}={x.Value}"));
            var head = $"- action {a.Id}: {a.Kind} on {a.Host} ({p}) {a.Status}, approved by {a.ApprovedBy}";
            list.Add(new Item("action", a.Id, a.IncidentId,
                a.Status == ActionStatus.Denied ? head : head + "\n" + AgentWorker.Untrusted($"output of action {a.Id}", AgentWorker.Clip(a.Output, 1500))));
        }

        // Terminal commands once done. They aren't tied to an incident, so attach them to the open one.
        var open = conn.Db.IncidentView.Iter().Where(i => i.Phase != Phase.Closed).OrderByDescending(i => i.Id).Select(i => i.Id).FirstOrDefault();
        if (open != 0)
        {
            var outputs = conn.Db.TerminalOutputView.Iter().ToList();
            foreach (var c in conn.Db.TerminalCommandView.Iter().Where(c =>
                         (c.Status is TerminalStatus.Done or TerminalStatus.Failed) && Fresh("terminal", c.Id)))
            {
                var output = string.Concat(outputs.Where(o => o.CommandId == c.Id).OrderBy(o => o.Id).Select(o => o.Chunk));
                list.Add(new Item("terminal", c.Id, open,
                    $"- terminal {c.Id}: {c.IssuedBy} ran on {c.Host}: {c.Command}\n" +
                    AgentWorker.Untrusted($"output of terminal {c.Id}", AgentWorker.Clip(output, 1500))));
            }
        }

        return list.Where(i => i.IncidentId != 0).OrderBy(i => i.Source == "event" ? 0 : 1).ThenBy(i => i.Id).ToList();
    }

    static string CurrentRecord(DbConnection conn, ulong incidentId)
    {
        var sb = new StringBuilder("## CURRENT RECORD\n");
        foreach (var i in conn.Db.IncidentView.Iter().Where(i => i.Id == incidentId))
        {
            sb.AppendLine($"Incident {i.Id}: {i.Title}. Phase {i.Phase}. Severity {i.Severity}/4.");
            sb.AppendLine($"Classification: {(string.IsNullOrWhiteSpace(i.Classification) ? "(none yet)" : i.Classification)}");
            sb.AppendLine($"AI summary: {(string.IsNullOrWhiteSpace(i.AiSummary) ? "(none yet)" : i.AiSummary)}");
            if (!string.IsNullOrWhiteSpace(i.Summary)) sb.AppendLine($"Human summary: {i.Summary}");
        }
        sb.AppendLine("Registered hosts: " + string.Join(", ", conn.Db.SshHostView.Iter().Select(h => h.Name)));
        sb.AppendLine("Affected data:");
        foreach (var a in conn.Db.AssetView.Iter().Where(a => a.IncidentId == incidentId))
            sb.AppendLine($"- {a.Host}: {a.DataClass}, {a.ScopeLevel}, {a.Status}{(a.Accepted ? " (accepted)" : " (proposed)")}");
        sb.AppendLine("Indicators:");
        foreach (var x in conn.Db.IndicatorView.Iter().Where(x => x.IncidentId == incidentId))
            sb.AppendLine($"- {x.Kind} {x.Value} ({x.Confidence}): {x.Context}");
        sb.AppendLine("Timeline so far (newest last):");
        foreach (var t in conn.Db.TimelineView.Iter().Where(t => t.IncidentId == incidentId && t.Accepted).OrderBy(t => t.Id).TakeLast(15))
            sb.AppendLine($"- [{t.Category}] {AgentWorker.Clip(t.Text, 160)}");
        return sb.ToString();
    }

    // ---------- Tools ----------

    static JsonArray Tools()
    {
        var Str = ModelClient.Str;
        return new JsonArray
        {
            ModelClient.Tool("update_incident", "Rewrite the full AI summary and classification of the incident.",
                ModelClient.Obj(new JsonObject
                {
                    ["summary"] = Str("Under 150 words: what happened in order, what is affected, what has been done, what is unknown."),
                    ["classification"] = Str("Short label, e.g. 'Ransomware with data exfiltration'."),
                }, "summary", "classification")),
            ModelClient.Tool("upsert_indicator", "Record or update one indicator of compromise or key fact.",
                ModelClient.Obj(new JsonObject
                {
                    ["kind"] = Str("IpAddress, Account, Host, Process, File, Hash, Domain, Url or Other"),
                    ["value"] = Str("The indicator itself, e.g. 185.220.101.47 or jdoe."),
                    ["context"] = Str("One line: what it is and why it matters."),
                    ["confidence"] = Str("Low, Medium or High"),
                    ["source"] = Str("Source ids, e.g. 'event 12, action 4'."),
                }, "kind", "value", "context", "confidence", "source")),
            ModelClient.Tool("propose_scope", "Record affected data for a human to accept.",
                ModelClient.Obj(new JsonObject
                {
                    ["host"] = Str("Host name."),
                    ["data_class"] = Str("Credentials, PersonalData, Financial, IntellectualProperty or Operational"),
                    ["scope_level"] = Str("User, Application, System or Network"),
                    ["status"] = Str("Unknown, Secure, Encrypted, Exfiltrated or EncryptedAndExfiltrated"),
                    ["evidence"] = Str("The evidence, citing source ids."),
                }, "host", "data_class", "scope_level", "status", "evidence")),
            ModelClient.Tool("add_timeline_entry", "Add one key moment to the timeline, as a draft a human accepts.",
                ModelClient.Obj(new JsonObject
                {
                    ["category"] = Str("Detected, Analysis, Containment, Eradication, Recovery, Communication or Note"),
                    ["text"] = Str("One sentence, citing source ids."),
                }, "category", "text")),
        };
    }

    // ---------- Apply (on the connection thread), then advance the cursors ----------

    static void Apply(DbConnection conn, ulong incidentId, ModelReply reply, List<Item> batch)
    {
        var calls = reply.ToolCalls.Count > 0 ? reply.ToolCalls : ModelClient.ToolCallsFromContent(reply.Content);
        int applied = 0, timeline = 0;
        foreach (var call in calls)
        {
            try
            {
                var a = call.Args;
                switch (call.Name)
                {
                    case "update_incident":
                        conn.Reducers.UpdateIncidentAnalysis(incidentId, S(a, "summary"), S(a, "classification"));
                        applied++;
                        break;
                    case "upsert_indicator":
                        if (S(a, "value").Trim().Length == 0) break;
                        conn.Reducers.UpsertIndicator(incidentId, E(S(a, "kind"), IndicatorKind.Other), S(a, "value"),
                            S(a, "context"), E(S(a, "confidence"), Confidence.Low), S(a, "source"));
                        applied++;
                        break;
                    case "propose_scope":
                        if (S(a, "host").Trim().Length == 0) break;
                        conn.Reducers.ProposeScopeUpdate(incidentId, S(a, "host").Trim(),
                            E(S(a, "data_class"), DataClass.Operational), E(S(a, "scope_level"), ScopeLevel.System),
                            E(S(a, "status"), DataStatus.Unknown), S(a, "evidence"));
                        applied++;
                        break;
                    case "add_timeline_entry":
                        if (++timeline > 3 || S(a, "text").Trim().Length == 0) break;   // the prompt says sparingly; enforce it
                        conn.Reducers.ProposeTimelineEntry(incidentId, S(a, "category"), S(a, "text"));
                        applied++;
                        break;
                    default:
                        ServiceRunner.Log($"Parser ignored unknown tool '{call.Name}'.");
                        break;
                }
            }
            catch (Exception ex) { ServiceRunner.Log($"Parser tool {call.Name} failed: {ex.Message}"); }
        }
        ServiceRunner.Log($"Parser applied {applied} update(s).");

        // Mark the batch as read, per source. Cursors only move forward. Actions and terminal commands
        // finish out of order, so their cursor never passes one that is still unfinished.
        foreach (var g in batch.GroupBy(b => b.Source))
        {
            var target = g.Max(b => b.Id);
            var unfinished = g.Key switch
            {
                "action" => conn.Db.ActionView.Iter()
                    .Where(x => x.Status is ActionStatus.Proposed or ActionStatus.Approved or ActionStatus.Running).Select(x => x.Id).ToList(),
                "terminal" => conn.Db.TerminalCommandView.Iter()
                    .Where(x => x.Status is TerminalStatus.Queued or TerminalStatus.Running).Select(x => x.Id).ToList(),
                _ => new List<ulong>(),
            };
            if (unfinished.Count > 0) target = Math.Min(target, unfinished.Min() - 1);
            if (target > 0) conn.Reducers.AdvanceParserCursor(g.Key, target);
        }
    }

    static string S(JsonNode? n, string key) => n?[key]?.ToString() ?? "";

    static T E<T>(string s, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(s.Replace("_", "").Replace(" ", ""), ignoreCase: true, out var v) ? v : fallback;
}
