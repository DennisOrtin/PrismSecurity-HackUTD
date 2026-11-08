#!/bin/bash
set -e
[ -f /etc/ssh/keys/ssh_host_ed25519_key ] || ssh-keygen -q -t ed25519 -f /etc/ssh/keys/ssh_host_ed25519_key -N ""
cp /keys/authorized_keys /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys
touch /var/log/auth.log /var/log/syslog /var/log/app.log
ts() { date '+%b %d %H:%M:%S'; }
echo "$(ts) $(hostname) app[211]: service started" >> /var/log/app.log
# Background noise so the logs look alive.
( while true; do echo "$(ts) $(hostname) CRON[$RANDOM]: (root) CMD (run-parts /etc/cron.hourly)" >> /var/log/syslog; sleep 45; done ) &
exec /usr/sbin/sshd -D -e
