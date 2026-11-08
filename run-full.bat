@echo off
rem FULL RUN: server, module, bindings, build everything, start agent/reviewer/executor, open the app.
setlocal
cd /d "%~dp0"

call scripts\ensure-server.bat || goto :fail

echo.
echo === Publishing the module ===
spacetime publish incident-command --module-path server --server http://127.0.0.1:3000 || goto :fail

echo.
echo === Generating client bindings ===
del /q /s "src\IncidentCommand.Client\module_bindings\*.cs" >nul 2>&1
spacetime generate --lang csharp --out-dir src\IncidentCommand.Client\module_bindings --module-path server || goto :fail

echo.
echo === Stopping anything still running from the last build ===
for %%P in (IncidentCommand.exe Agent.exe Executor.exe PostureHelper.exe) do taskkill /im %%P /f >nul 2>&1

echo.
echo === Building into dist\ ===
for %%P in (App Agent Executor PostureHelper) do (
  dotnet publish src\IncidentCommand.%%P -c Release -r win-x64 --self-contained false -o dist --nologo -v q || goto :fail
)

echo.
echo === Starting services (each window stays open so you can read its log) ===
start "Agent" cmd /k dist\Agent.exe agent
start "Reviewer" cmd /k dist\Agent.exe reviewer
start "Parser" cmd /k dist\Agent.exe parser
start "Executor" cmd /k dist\Executor.exe

echo.
echo === Opening the dashboard ===
start "" dist\IncidentCommand.exe
echo Done. Services that aren't enrolled yet will say so in their window: enroll them from Settings.
exit /b 0

:fail
echo.
echo *** Stopped: the step above failed. Fix it and run again. ***
pause
exit /b 1
