@echo off
rem Restart the agent, reviewer and executor from dist\ (after enrolling them, or after a rebuild).
cd /d "%~dp0"
for %%P in (Agent.exe Executor.exe) do taskkill /im %%P /f >nul 2>&1
start "Agent" cmd /k dist\Agent.exe agent
start "Reviewer" cmd /k dist\Agent.exe reviewer
start "Executor" cmd /k dist\Executor.exe
