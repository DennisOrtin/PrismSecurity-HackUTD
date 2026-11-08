using IncidentCommand.Agent;
using IncidentCommand.Client;
using SpacetimeDB.Types;

// Three local agents and a test, all in one executable. All AI runs on this laptop via LM Studio.
//   Agent.exe agent      -> response agent: chat with the responder, propose actions (propose-only)
//   Agent.exe reviewer   -> checks every proposed command before a human can send it (warn-only)
//   Agent.exe parser     -> turns everything in the dashboard into structured data
//   Agent.exe test       -> lists LM Studio's loaded models and times a reply from each configured one
//   Agent.exe enroll <agent|reviewer|parser> <code>   -> manual fallback; normally automatic
if (args.Length >= 3 && args[0] == "enroll") return ServiceRunner.Enroll(args[1], args[2]);

var role = args.FirstOrDefault() ?? "agent";
var model = new ModelClient();

if (role == "test")
{
    Console.WriteLine($"LM Studio: {Settings.Current.Ai.BaseUrl}");
    try { Console.WriteLine("Loaded models: " + await model.ListModelsAsync()); }
    catch (Exception ex) { Console.WriteLine("Could not reach LM Studio: " + ex.Message); return 1; }
    int failures = 0;
    var ai = Settings.Current.Ai;
    Console.WriteLine($"Defaults: agent={ai.DefaultAgentModel}  reviewer={ai.DefaultReviewerModel}  parser={ai.DefaultParserModel}\n");
    var ids = ai.Models.Select(m => m.Id)
        .Concat(new[] { ai.DefaultAgentModel, ai.DefaultReviewerModel, ai.DefaultParserModel })
        .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct();
    foreach (var id in ids)
    {
        try
        {
            var started = DateTime.UtcNow;
            var reply = await model.ChatAsync(id, "Reply with exactly: OK", "Say OK.", temperature: 0, maxTokens: 64);
            Console.WriteLine($"{AiNames.Name(id),-26} [{id}] -> \"{reply.Content}\" in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
        }
        catch (Exception ex) { Console.WriteLine($"{AiNames.Name(id),-26} [{id}] FAILED: {ex.Message}"); failures++; }
    }
    Console.WriteLine($"\nModel IDs come from: {Settings.FilePath}");
    return failures == 0 ? 0 : 1;
}

var runner = new ServiceRunner();
ServiceRunner.Log($"Starting {role}. LM Studio: {Settings.Current.Ai.BaseUrl}");

switch (role)
{
    case "agent":
        var agent = new AgentWorker(runner, model);
        runner.Run("agent", agent.Wire, agent.Poll);
        break;
    case "reviewer":
        var reviewer = new ReviewerWorker(runner, model);
        runner.Run("reviewer", _ => { }, reviewer.Poll);
        break;
    case "parser":
        var parser = new ParserWorker(runner, model);
        runner.Run("parser", _ => { }, parser.Poll);
        break;
    default:
        Console.WriteLine("Usage: Agent.exe agent | reviewer | parser | test | enroll <agent|reviewer|parser> <code>");
        return 1;
}
return 0;
