<#
  The judges' view of what's really happening inside the containers, refreshed every few seconds.
  Run it on the sim laptop (or a second screen) during the demo. Red = the attacker still has it;
  green = the response took it away. Every cell is checked live inside the container, not taken from
  the dashboard, so it proves the actions actually worked.

    .\status.ps1            refresh every 3 seconds (Ctrl+C to stop)
    .\status.ps1 -Every 1
#>
param([int]$Every = 3, [string[]]$Hosts = @('web-01', 'file-01', 'db-01'))

$c2 = (docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' c2 2>$null).Trim()

$probe = @'
c2=__C2__
beacon=BLOCKED; timeout 2 bash -c "echo > /dev/tcp/$c2/8080" 2>/dev/null && beacon=REACHABLE
implant=stopped; pgrep -f /usr/local/bin/kworker-update >/dev/null && implant=RUNNING
persist=removed; { [ -e /etc/cron.d/kworker-update ] || [ -e /usr/local/bin/kworker-update ]; } && persist=PRESENT
key=removed; grep -qs attacker@c2 /home/jdoe/.ssh/authorized_keys && key=PLANTED
iso=open; [ "$(iptables -S OUTPUT 2>/dev/null | head -1 | awk '{print $3}')" = "DROP" ] && iso=ISOLATED
lock=$(passwd -S jdoe 2>/dev/null | awk '{print $2}'); [ "$lock" = "L" ] && lock=LOCKED || lock=active
enc=$(ls /srv/hr/*.locked 2>/dev/null | wc -l)
echo "$beacon|$implant|$persist|$key|$iso|$lock|$enc"
'@
$probe = ($probe -replace "`r", "").Replace('__C2__', $c2) + "`n"

function Cell([string]$text, [bool]$bad, [int]$width) {
  $color = if ($bad) { 'Red' } else { 'Green' }
  Write-Host ($text.PadRight($width)) -ForegroundColor $color -NoNewline
}

while ($true) {
  Clear-Host
  Write-Host ("ATTACKER FOOTHOLD, checked live inside each container   {0:HH:mm:ss}   (c2 = {1})" -f (Get-Date), $c2) -ForegroundColor Cyan
  Write-Host "Red = attacker still has it. Green = taken away by the response.`n"
  Write-Host ("{0,-9}{1,-17}{2,-14}{3,-15}{4,-14}{5,-12}{6,-12}{7}" -f 'HOST', 'REACH C2', 'IMPLANT', 'PERSISTENCE', 'PLANTED KEY', 'NETWORK', 'jdoe', 'ENCRYPTED FILES')
  foreach ($h in $Hosts) {
    $line = ($probe | docker exec -i $h bash 2>$null) | Select-Object -Last 1
    if (-not $line) { Write-Host ("{0,-9}(container not running)" -f $h) -ForegroundColor DarkGray; continue }
    $f = $line.Trim().Split('|')
    Write-Host ($h.PadRight(9)) -NoNewline
    Cell $f[0] ($f[0] -eq 'REACHABLE') 17
    Cell $f[1] ($f[1] -eq 'RUNNING') 14
    Cell $f[2] ($f[2] -eq 'PRESENT') 15
    Cell $f[3] ($f[3] -eq 'PLANTED') 14
    Cell $f[4] ($f[4] -ne 'ISOLATED') 12
    Cell $f[5] ($f[5] -ne 'LOCKED') 12
    Cell $f[6] ([int]$f[6] -gt 0) 6
    Write-Host ""
  }
  Write-Host "`nContainment: REACH C2 -> BLOCKED (block_ip / isolate_host), IMPLANT -> stopped (kill_process), jdoe -> LOCKED"
  Write-Host "Eradication: PERSISTENCE + PLANTED KEY -> removed (quarantine_file)     Recovery: ENCRYPTED FILES -> 0 (restore_from_backup)"
  Start-Sleep $Every
}
