# Publish (or update) the module on the local server. Add -Wipe to delete all data (dev only).
param([switch]$Wipe)
$extra = if ($Wipe) { @('--delete-data', '-y') } else { @() }
spacetime publish incident-command --module-path "$PSScriptRoot\..\server" --server http://127.0.0.1:3000 @extra
