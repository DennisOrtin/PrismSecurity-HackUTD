using System.Text;
using System.Text.Json.Nodes;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

/// The reviewer: checks every command the response agent proposes (catalog actions and suggested shell
/// commands) BEFORE a human can send it, and attaches a warning. It can only ADD warnings; it never
/// blocks, approves or runs anything. It also sees the evidence the agent saw, to spot injection.
public sealed class ReviewerWorker
{
    readonly ServiceRunner _runner;
    readonly ModelClient _model;
    readonly HashSet<string> _done = new();
    volatile bool _busy;

    public ReviewerWorker(ServiceRunner runner, ModelClient model) { _runner = runner; _model = model; }

    public void Poll(DbConnection conn)
    {
        if (_busy) return;
        var reviewed = conn.Db.ReviewView.Iter().Select(r => $"{r.TargetKind}:{r.TargetId}").ToHashSet();

        // Proposed actions first (a human may be about to approve them), then suggested commands.
        foreach (var a in conn.Db.ActionView.Iter().Where(a => a.Status == ActionStatus.Proposed).OrderBy(a => a.Id))
        {
            var key = $"action:{a.Id}";
            if (reviewed.Contains(key) || _done.Contains(key)) continue;
            var plan = conn.Db.PlanView.Iter().Where(p => p.Id == a.PlanId).Select(p => p.Reasoning).FirstOrDefault() ?? "";
            var item = new StringBuilder()
                .AppendLine($"PROPOSED CATALOG ACTION: {a.Kind} on host {a.Host}")
                .AppendLine($"Parameters: {(a.Params.Count == 0 ? "(none)" : string.Join(", ", a.Params.Select(p => $"{p.Key}={p.Value}")))}")
                .AppendLine($"Tier: {a.Tier}")
                .AppendLine($"Agent's rationale: {a.Rationale}")
                .AppendLine($"Agent's plan reasoning: {plan}")
                .Append(Evidence(conn, a.IncidentId, a.Host))
                .ToString();
            Start(conn, "action", a.Id, a.IncidentId, key, item);
            return;
        }
        foreach (var s in conn.Db.SuggestionView.Iter().OrderBy(s => s.Id))
        {
            var key = $"suggestion:{s.Id}";
            if (reviewed.Contains(key) || _done.Contains(key)) continue;
            var patterns = RiskPatterns.Check(s.Text);
            var item = new StringBuilder()
                .AppendLine($"PROPOSED SHELL COMMAND for host {s.Host} (a human would run it by hand):")
                .AppendLine(s.Text)
                .AppendLine($"Agent's rationale: {s.Rationale}")
                .AppendLine(patterns.Count > 0 ? "Automatic pattern checks flagged: " + string.Join("; ", patterns) : "Automatic pattern checks flagged nothing.")
                .Append(Evidence(conn, s.IncidentId, s.Host))
                .ToString();
            Start(conn, "suggestion", s.Id, s.IncidentId, key, item);
            return;
        }
    }

    /// The most recent evidence from the same host, fenced as untrusted, so injection-shaped proposals stand out.
    static string Evidence(DbConnection conn, ulong incidentId, string host)
    {
        var sb = new StringBuilder("\nRECENT EVIDENCE FROM THIS HOST (what the agent was looking at):\n");
        foreach (var e in conn.Db.EventView.Iter().Where(e => e.IncidentId == incidentId && e.Host == host).OrderBy(e => e.Id).TakeLast(12))
            sb.AppendLine(AgentWorker.Untrusted($"event {e.Id}, {e.Source}", AgentWorker.Clip(e.Raw, 300)));
        return sb.ToString();
    }

    void Start(DbConnection conn, string kind, ulong id, ulong incidentId, string key, string item)
    {
        var configured = "";
        foreach (var c in conn.Db.ConfigView.Iter()) configured = c.ReviewerModel;
        var model = AiNames.Resolve(configured, "reviewer");
        var label = $"{AiNames.Name(model)} · reviewer";
        _busy = true;
        _done.Add(key);
        ServiceRunner.Log($"Reviewing {key} with {AiNames.Name(model)}");

        Task.Run(async () =>
        {
            Risk risk = Risk.Caution;
            string reasons = "The reviewer's answer could not be read, so treat this with caution.";
            try
            {
                var reply = await _model.ChatAsync(model, Prompts.ReviewerSystem, item, tools: null, temperature: 0, maxTokens: Settings.Current.Ai.ReviewerMaxTokens);
                var text = reply.Content ?? "";
                var start = text.IndexOf('{');
                var end = text.LastIndexOf('}');
                if (start >= 0 && end > start && JsonNode.Parse(text[start..(end + 1)]) is JsonObject o)
                {
                    if (Enum.TryParse<Risk>(o["risk"]?.ToString(), true, out var r)) risk = r;
                    reasons = o["reasons"]?.ToString() ?? reasons;
                }
            }
            catch (Exception ex) { reasons = "Reviewer unavailable: " + ex.Message; }
            finally { _busy = false; }

            _runner.Post(c =>
            {
                c.Reducers.AttachReview(kind, id, risk, $"{reasons} ({label})");
                if (risk == Risk.Danger)
                    c.Reducers.PostAiMessage(incidentId,
                        $"Reviewer: the proposed {(kind == "action" ? "action" : "command")} #{id} is DANGEROUS. {reasons}", label);
            });
        });
    }
}
