@echo off
rem Starts SpacetimeDB on 127.0.0.1:3000 in its own window if it isn't already running,
rem then waits (up to 30 s) until it is listening. Exit code 1 if it never comes up.
netstat -ano | findstr /r /c:"127.0.0.1:3000 .*LISTENING" >nul
if not errorlevel 1 (
  echo SpacetimeDB already running on 127.0.0.1:3000.
  exit /b 0
)
echo === Starting SpacetimeDB on 127.0.0.1:3000 ===
start "SpacetimeDB" cmd /k spacetime start --listen-addr 127.0.0.1:3000
powershell -NoProfile -Command "$t=0; while(-not (Get-NetTCPConnection -State Listen -LocalPort 3000 -ErrorAction SilentlyContinue)){ Start-Sleep 1; $t++; if($t -gt 30){ exit 1 } }"
if errorlevel 1 (
  echo SpacetimeDB did not start within 30 seconds. Check the SpacetimeDB window.
  exit /b 1
)
echo SpacetimeDB is up.
exit /b 0
