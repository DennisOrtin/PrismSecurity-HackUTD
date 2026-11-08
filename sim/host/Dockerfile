# One image plays every corporate host. Real sshd, real processes, real iptables.
FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      openssh-server iptables tcpdump iproute2 procps curl netcat-openbsd ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Only an ed25519 host key, kept in a per-container volume so its fingerprint survives resets.
RUN rm -f /etc/ssh/ssh_host_* && mkdir -p /run/sshd /root/.ssh /etc/ssh/keys && chmod 700 /root/.ssh \
 && printf "HostKey /etc/ssh/keys/ssh_host_ed25519_key\nPermitRootLogin prohibit-password\nPasswordAuthentication no\n" \
      > /etc/ssh/sshd_config.d/sim.conf

# A normal user who will be "compromised", and data worth stealing.
RUN useradd -m -s /bin/bash jdoe \
 && mkdir -p /srv/finance /srv/hr \
 && for q in q1 q2 q3; do printf "account,amount\n1001,52000\n1002,18000\n" > /srv/finance/ledger_2026_$q.csv; done \
 && for f in payroll_2026 benefits employees_ssn reviews_2026; do printf "name,id\nAlex Doe,0001\nSam Roe,0002\n" > /srv/hr/$f.csv; done

COPY entrypoint.sh /entrypoint.sh
RUN sed -i 's/\r$//' /entrypoint.sh && chmod +x /entrypoint.sh
ENTRYPOINT ["/entrypoint.sh"]
