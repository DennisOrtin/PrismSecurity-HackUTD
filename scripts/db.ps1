<#
  Database inspector. Reads EVERY table, including private ones, by running SQL as the database owner
  (the identity your spacetime CLI published with). Nothing here goes through the app or its views.

    db.bat                      list tables and row counts
    db.bat all                  dump every table
    db.bat <table>              dump one table (e.g. db.bat response_action)
    db.bat <table> -Watch       re-dump every 2 seconds (Ctrl+C to stop)
    db.bat -Sql "SELECT ..."    run any query
#>
param(
  [Parameter(Position = 0)] [string]$Table = "",
  [string]$Sql = "",
  [switch]$Watch,
  [int]$Every = 2
)
$server = "http://127.0.0.1:3000"
$db = "incident-command"

function Run-Sql([string]$q) {
  & spacetime sql $db $q --server $server 2>&1
}

# Table names come from the system catalog, so this keeps working as the schema changes.
function Get-Tables {
  $out = Run-Sql "SELECT table_name FROM st_table"
  $names = foreach ($line in $out) {
    $t = "$line".Trim().Trim('"')
    if ($t -and $t -notmatch '^-+$' -and $t -ne 'table_name' -and $t -notlike 'st_*' -and $t -notmatch '\s') { $t }
  }
  $names | Sort-Object -Unique
}

function Count-Rows([string]$t) {
  $out = Run-Sql "SELECT COUNT(*) AS n FROM $t"
  foreach ($line in $out) { if ("$line".Trim() -match '^\d+$') { return [int]"$line".Trim() } }
  return "?"
}

function Dump([string]$t) {
  Write-Host ""
  Write-Host "=== $t ===" -ForegroundColor Cyan
  Run-Sql "SELECT * FROM $t" | Out-Host
}

if ($Sql) { Run-Sql $Sql | Out-Host; exit }

$tables = Get-Tables
if (-not $tables) {
  Write-Host "Could not read the table list. Is the server running (scripts\ensure-server.bat) and the module published?" -ForegroundColor Red
  Run-Sql "SELECT table_name FROM st_table" | Out-Host
  exit 1
}

if (-not $Table) {
  Write-Host "Tables in '$db' (rows):" -ForegroundColor Cyan
  foreach ($t in $tables) { "{0,-22} {1}" -f $t, (Count-Rows $t) }
  Write-Host "`nUse: db.bat <table>   db.bat all   db.bat <table> -Watch   db.bat -Sql `"SELECT ...`""
  exit
}

$targets = if ($Table -eq "all") { $tables } else {
  $match = $tables | Where-Object { $_ -ieq $Table }
  if (-not $match) { Write-Host "No table '$Table'. Tables: $($tables -join ', ')" -ForegroundColor Red; exit 1 }
  $match
}

do {
  if ($Watch) { Clear-Host; Write-Host ("Refreshed {0:HH:mm:ss}   (Ctrl+C to stop)" -f (Get-Date)) -ForegroundColor DarkGray }
  foreach ($t in $targets) { Dump $t }
  if ($Watch) { Start-Sleep $Every }
} while ($Watch)
