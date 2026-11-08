using IncidentCommand.Agent;
using IncidentCommand.Client;
using SpacetimeDB.Types;

// Agent.exe agent                     -> the planning agent (Nemotron, propose-only)
// Agent.exe reviewer                  -> the reviewer agent (Nemotron, warn-only)
// Agent.exe test [local|cloud]        -> check both models answer on that provider (no database needed)
// Agent.exe enroll agent <code>       -> one-time enrollment with an invite code from the commander
// Agent.exe enroll reviewer <code>
if (args.Length >= 3 && args[0] == "enroll") return ServiceRunner.Enroll(args[1], args[2]);

var role = args.FirstOrDefault() ?? "agent";
var model = new ModelClient();

if (role == "test")
{
    var providers = args.Length > 1
        ? new[] { args[1].Equals("cloud", StringComparison.OrdinalIgnoreCase) ? AiProvider.Cloud : AiProvider.Local }
        : new[] { AiProvider.Local, AiProvider.Cloud };
    int failures = 0;
    foreach (var p in providers)
    {
        Console.WriteLine($"\n== {p} ==");
        try { Console.WriteLine("Loaded models: " + await model.ListModelsAsync(p)); }
        catch (Exception ex) { Console.WriteLine("Could not list models: " + ex.Message); failures++; continue; }
        foreach (var m in new[] { AiModel.Nano, AiModel.Lightning })
        {
            var id = ModelClient.ModelId(p, m);
            try
            {
                var started = DateTime.UtcNow;
                var reply = await model.ChatAsync(p, m, "Reply with exactly: OK", "Say OK.", temperature: 0);
                Console.WriteLine($"{AiNames.Model(m),-24} [{id}] -> \"{reply.Content}\" in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            }
            catch (Exception ex) { Console.WriteLine($"{AiNames.Model(m),-24} [{id}] FAILED: {ex.Message}"); failures++; }
        }
    }
    Console.WriteLine($"\nSettings file: {Settings.FilePath}");
    return failures == 0 ? 0 : 1;
}

var runner = new ServiceRunner();
ServiceRunner.Log($"Starting {role}. Local: {Settings.Current.Ai.Local.BaseUrl}  Cloud: {Settings.Current.Ai.Cloud.BaseUrl}");

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
    default:
        Console.WriteLine("Usage: Agent.exe agent | reviewer | test [local|cloud] | enroll <agent|reviewer> <code>");
        return 1;
}
return 0;
