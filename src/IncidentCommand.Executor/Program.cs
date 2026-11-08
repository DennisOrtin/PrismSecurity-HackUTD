using IncidentCommand.Client;
using IncidentCommand.Executor;

if (args.Length >= 2 && args[0] == "enroll") return ServiceRunner.Enroll("executor", args[1]);

var runner = new ServiceRunner();
var worker = new ExecutorWorker(runner);
ServiceRunner.Log("Starting executor. It runs approved catalog actions and human terminal commands, nothing else.");
runner.Run("executor", worker.Wire, worker.Poll);
return 0;
