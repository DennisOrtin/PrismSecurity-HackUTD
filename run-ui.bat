@echo off
rem UI ONLY: make sure the server is up, then run the WPF app straight from source (fast rebuild).
rem Skips publishing the module and the services. Use run-full.bat after any change in server\.
rem Note: the Laptop posture screen needs PostureHelper.exe next to the app, which only run-full.bat sets up.
setlocal
cd /d "%~dp0"

call scripts\ensure-server.bat || goto :fail

dir /b "src\IncidentCommand.Client\module_bindings\*.cs" >nul 2>&1
if errorlevel 1 (
  echo No client bindings yet. Run run-full.bat once first.
  goto :fail
)

taskkill /im IncidentCommand.exe /f >nul 2>&1
dotnet run --project src\IncidentCommand.App || goto :fail
exit /b 0

:fail
pause
exit /b 1
