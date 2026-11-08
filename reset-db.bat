@echo off
rem WIPES ALL DATA: republishes the module with --delete-data and removes local vaults and service tokens
rem (they belong to identities that no longer exist). The next app start runs first-run setup again.
setlocal
cd /d "%~dp0"

echo This deletes every incident, user and audit entry, plus all local logins and service tokens.
set /p ok=Type YES to continue: 
if /i not "%ok%"=="YES" (
  echo Cancelled.
  exit /b 0
)

for %%P in (IncidentCommand.exe Agent.exe Executor.exe) do taskkill /im %%P /f >nul 2>&1
call scripts\ensure-server.bat || goto :fail

spacetime publish incident-command --module-path server --server http://127.0.0.1:3000 --delete-data -y || goto :fail
del /q "%LOCALAPPDATA%\IncidentCommand\vaults\*.vault" >nul 2>&1
del /q "%LOCALAPPDATA%\IncidentCommand\services\*.token" >nul 2>&1

del /q /s "src\IncidentCommand.Client\module_bindings\*.cs" >nul 2>&1
spacetime generate --lang csharp --out-dir src\IncidentCommand.Client\module_bindings --module-path server || goto :fail

echo Database wiped. Run run-full.bat or run-ui.bat; the app will start with first-run setup.
pause
exit /b 0

:fail
pause
exit /b 1
