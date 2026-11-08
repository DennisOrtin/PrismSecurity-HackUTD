# Start the three background services, each in its own window so you can watch their logs.
$dist = "$PSScriptRoot\..\dist"
Start-Process -FilePath "$dist\Agent.exe" -ArgumentList 'agent'
Start-Process -FilePath "$dist\Agent.exe" -ArgumentList 'reviewer'
Start-Process -FilePath "$dist\Executor.exe"
