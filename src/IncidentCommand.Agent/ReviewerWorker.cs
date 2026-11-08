using System.Text.Json.Nodes;
using IncidentCommand.Client;
using SpacetimeDB.Types;

namespace IncidentCommand.Agent;

/// The second agent. Reviews every proposed action and every AI-suggested command, and can only
/// ADD warnings (AttachReview). It never blocks, approves or runs anything.
public sealed class ReviewerWorker
{
    readonly ServiceRunner _runner;
    readonly ModelClient _model;
    readonly HashSet<string> _done = new();
    bool _busy;

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
            var item = $"Proposed catalog action: {a.Kind} on {a.Host} with {string.Join(", ", a.Params.Select(p => $"{p.Key}={p.Value}"))}\n" +
                       $"Tier: {a.Tier}\nRationale: {a.Rationale}\nPlan reasoning: {plan}";
            Start("action", a.Id, key, item);
            return;
        }
        foreach (var s in conn.Db.SuggestionView.Iter().OrderBy(s => s.Id))
        {
            var key = $"suggestion:{s.Id}";
            if (reviewed.Contains(key) || _done.Contains(key)) continue;
            var patterns = RiskPatterns.Check(s.Text);
            var item = $"AI-suggested shell command for host {s.Host}:\n{s.Text}\nRationale: {s.Rationale}\n" +
                       (patterns.Count > 0 ? "Pattern checks flagged: " + string.Join("; ", patterns) : "Pattern checks flagged nothing.");
            Start("suggestion", s.Id, key, item);
            return;
        }
    }

    void Start(string kind, ulong id, string key, string item)
    {
        var provider = AiProvider.Local;
        var model = AiModel.Nano;
        foreach (var c in _runner.Conn!.Db.ConfigView.Iter()) { provider = c.AiProvider; model = c.ReviewerModel; }
        var label = AiNames.Label(model, provider);
        _busy = true;
        _done.Add(key);
        ServiceRunner.Log($"Reviewing {key} with {label}");
        Task.Run(async () =>
        {
            Risk risk = Risk.Caution;
            string reasons = "The reviewer's answer could not be read, so treat this with caution.";
            try
            {
                var reply = await _model.ChatAsync(provider, model, Prompts.ReviewerSystem, item, tools: null, temperature: 0);
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
                if (risk == Risk.Danger && kind == "suggestion")
                    foreach (var s in c.Db.SuggestionView.Iter().Where(s => s.Id == id))
                        c.Reducers.PostAiMessage(s.IncidentId, $"Reviewer: the suggested command for {s.Host} is DANGEROUS. {reasons}", label);
            });
        });
    }
}
