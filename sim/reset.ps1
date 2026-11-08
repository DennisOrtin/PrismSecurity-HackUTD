# Recreate every container from the image: files restored, accounts unlocked, iptables flushed,
# logs cleared. Host keys survive (named volumes), so pinned fingerprints stay valid.
docker compose -f "$PSScriptRoot\docker-compose.yml" up -d --build --force-recreate
Write-Host "Sim network reset."
