@echo off
rem Patch 11 replaced the Actions, Timeline and Terminal pages. Run once after extracting patch 11.
cd /d "%~dp0"
for %%F in (
  "src\IncidentCommand.App\ViewModels\ActionsVm.cs"
  "src\IncidentCommand.App\ViewModels\TimelineVm.cs"
  "src\IncidentCommand.App\Views\ActionsView.xaml"
  "src\IncidentCommand.App\Views\ActionsView.xaml.cs"
  "src\IncidentCommand.App\Views\TimelineView.xaml"
  "src\IncidentCommand.App\Views\TimelineView.xaml.cs"
  "src\IncidentCommand.App\Views\TerminalView.xaml"
  "src\IncidentCommand.App\Views\TerminalView.xaml.cs"
) do if exist %%F del /q %%F
echo Old Actions, Timeline and Terminal pages removed. Now run reset-db.bat, then run-full.bat.
pause
