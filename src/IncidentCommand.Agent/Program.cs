using IncidentCommand.Agent;
using IncidentCommand.Client;

// Agent.exe agent                     -> the planning agent (Nemotron, propose-only)
// Agent.exe reviewer                  -> the reviewer agent (Nemotron, warn-only)
// Agent.exe enroll agent <code>       -> one-time enrollment with an invite code from the commander
// Agent.exe enroll reviewer <code>
if (args.Length >= 3 && args[0] == "enroll") return ServiceRunner.Enroll(args[1], args[2]);

var role = args.FirstOrDefault() ?? "agent";
var model = new ModelClient(Settings.Current);
var runner = new ServiceRunner();
ServiceRunner.Log($"Starting {role}. Model endpoint: {Settings.Current.ModelBaseUrl}");

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
        Console.WriteLine("Usage: Agent.exe agent | reviewer | enroll <agent|reviewer> <code>");
        return 1;
}
return 0;
