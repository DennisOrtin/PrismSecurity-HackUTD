# Build everything into one folder. Executor.exe must sit next to PostureHelper.exe (egress rule).
$dist = "$PSScriptRoot\..\dist"
foreach ($p in 'IncidentCommand.App','IncidentCommand.Agent','IncidentCommand.Executor','IncidentCommand.PostureHelper') {
  dotnet publish "$PSScriptRoot\..\src\$p" -c Release -r win-x64 --self-contained false -o $dist
  if ($LASTEXITCODE -ne 0) { throw "Build failed: $p" }
}
Write-Host "Built into $dist"
