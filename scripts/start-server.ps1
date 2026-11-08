# Loopback only. Nothing off this laptop can reach the database.
# Verify the flag name against `spacetime start --help` for 2.14.1.
spacetime start --listen-addr 127.0.0.1:3000
