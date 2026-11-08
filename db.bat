@echo off
rem Inspect the whole database (all tables, including private ones). See scripts\db.ps1 for usage.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\db.ps1" %*
