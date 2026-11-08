# Prints what to enter in Settings > Hosts on the responder laptop.
$tail = (tailscale ip -4 2>$null | Select-Object -First 1)
$hosts = @(@{n='web-01';p=2201}, @{n='file-01';p=2202}, @{n='db-01';p=2203})
foreach ($h in $hosts) {
  $fp = (docker exec $h.n ssh-keygen -lf /etc/ssh/keys/ssh_host_ed25519_key.pub).Split(' ')[1]
  "{0,-8} address {1,-15} port {2}  fingerprint {3}" -f $h.n, $tail, $h.p, $fp
}
