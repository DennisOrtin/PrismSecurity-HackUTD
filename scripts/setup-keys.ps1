# Responder laptop: create the break-glass SSH key the executor uses. Copy the .pub to the sim laptop
# as sim\keys\authorized_keys. Rotate (delete and recreate) after every real incident.
$dir = Join-Path $env:LOCALAPPDATA 'IncidentCommand\keys'
New-Item -ItemType Directory -Force $dir | Out-Null
$key = Join-Path $dir 'breakglass_ed25519'
if (-not (Test-Path $key)) { ssh-keygen -t ed25519 -f $key -N '""' -C 'incident-command-breakglass' }
icacls $dir /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null
Get-Content "$key.pub"
