using System.Text;
using SpacetimeDB;

public static partial class Module
{
    // ---------- Auth ----------

    /// Deny by default: the caller must be an active user with one of the allowed roles.
    static User Require(ReducerContext ctx, params Role[] allowed)
    {
        var found = ctx.Db.User.Identity.Find(ctx.Sender);
        if (found is null) throw new Exception("Unknown identity. Log in first.");
        var u = found.Value;
        if (!u.Active) throw new Exception("This account is deactivated.");
        if (!allowed.Contains(u.Role)) throw new Exception($"Role {u.Role} is not allowed to do this.");
        return u;
    }

    static Config GetConfig(ReducerContext ctx) =>
        ctx.Db.Config.Id.Find(0u) ?? throw new Exception("Module not initialised.");

    static bool IsHuman(Role r) => r is Role.Commander or Role.Responder or Role.Observer;

    // ---------- Audit chain ----------

    static void WriteAudit(ReducerContext ctx, string username, string reducer, string payloadJson)
    {
        var head = ctx.Db.AuditHead.Id.Find(0u) ?? throw new Exception("Audit head missing.");
        var at = ctx.Timestamp;
        var body = $"{head.LastHash}|{at.MicrosecondsSinceUnixEpoch}|{username}|{reducer}|{payloadJson}";
        var hash = Sha256Hex(body);
        ctx.Db.AuditEntry.Insert(new AuditEntry
        {
            Seq = 0,
            At = at,
            Actor = ctx.Sender,
            Username = username,
            Reducer = reducer,
            PayloadJson = payloadJson,
            PrevHash = head.LastHash,
            Hash = hash,
        });
        ctx.Db.AuditHead.Id.Update(head with { LastHash = hash, Count = head.Count + 1 });
    }

    /// Payload("id", 5, "host", "web-01") -> {"id":"5","host":"web-01"}
    static string Payload(params object[] kv)
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i + 1 < kv.Length; i += 2)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Esc(kv[i]?.ToString() ?? "")).Append("\":\"")
              .Append(Esc(kv[i + 1]?.ToString() ?? "")).Append('"');
        }
        return sb.Append('}').ToString();
    }

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    // ---------- Action catalog ----------
    // The ONLY things the executor will ever run for an action. Templates live in the executor;
    // the module decides what is allowed and at which tier.

    record CatalogEntry(Tier Tier, string[] Keys);

    static readonly Dictionary<string, CatalogEntry> Catalog = new()
    {
        ["collect_logs"]     = new(Tier.ReadOnly,    new[] { "log", "lines" }),
        ["check_egress"]     = new(Tier.ReadOnly,    new[] { "ip", "port" }),           // verifies containment worked
        ["quarantine_file"]  = new(Tier.Containment, new[] { "path" }),                 // eradication: implants, cron, keys
        ["restore_from_backup"] = new(Tier.Containment, new[] { "dir" }),               // recovery: encrypted data
        ["follow_logs"]      = new(Tier.ReadOnly,    new[] { "log" }),
        ["list_processes"]   = new(Tier.ReadOnly,    new string[0]),
        ["list_connections"] = new(Tier.ReadOnly,    new string[0]),
        ["capture_traffic"]  = new(Tier.ReadOnly,    new[] { "seconds" }),
        ["hash_files"]       = new(Tier.ReadOnly,    new[] { "dir" }),
        ["kill_process"]     = new(Tier.Containment, new[] { "pid" }),
        ["lock_account"]     = new(Tier.Containment, new[] { "username" }),
        ["block_ip"]         = new(Tier.Containment, new[] { "ip" }),
        ["isolate_host"]     = new(Tier.Containment, new string[0]),
        ["shutdown_host"]    = new(Tier.Destructive, new string[0]),
    };

    static readonly string[] AllowedLogs = { "auth", "syslog", "app" };

    /// Throws with a plain-language reason if anything is wrong. Returns the tier.
    static Tier Validate(ReducerContext ctx, string kind, string host, List<Param> ps)
    {
        if (!Catalog.TryGetValue(kind, out var entry))
            throw new Exception($"'{kind}' is not in the action catalog.");
        if (ctx.Db.SshHost.Name.Find(host) is null)
            throw new Exception($"Unknown host '{host}'.");

        var keys = ps.Select(p => p.Key).ToList();
        if (keys.Distinct().Count() != keys.Count) throw new Exception("Duplicate parameter.");
        foreach (var k in entry.Keys)
            if (!keys.Contains(k)) throw new Exception($"Missing parameter '{k}'.");
        foreach (var k in keys)
            if (!entry.Keys.Contains(k)) throw new Exception($"Unexpected parameter '{k}'.");

        var cfg = GetConfig(ctx);
        foreach (var p in ps)
        {
            switch (p.Key)
            {
                case "log":
                    if (!AllowedLogs.Contains(p.Value)) throw new Exception("log must be auth, syslog or app.");
                    break;
                case "lines":
                    if (!int.TryParse(p.Value, out var lines) || lines < 1 || lines > 2000) throw new Exception("lines must be 1-2000.");
                    break;
                case "seconds":
                    if (!int.TryParse(p.Value, out var s) || s < 1 || s > 120) throw new Exception("seconds must be 1-120.");
                    break;
                case "pid":
                    if (!int.TryParse(p.Value, out var pid) || pid < 2) throw new Exception("pid must be a number above 1.");
                    break;
                case "username":
                    if (!IsSafeUsername(p.Value)) throw new Exception("Invalid username.");
                    break;
                case "ip":
                    if (!IsIPv4(p.Value)) throw new Exception("ip must be an IPv4 address.");
                    if (p.Value == cfg.ResponderAddress) throw new Exception("Refusing to target the responder laptop.");
                    break;
                case "port":
                    if (!int.TryParse(p.Value, out var port) || port < 1 || port > 65535) throw new Exception("port must be 1-65535.");
                    break;
                case "path":
                    if (!IsSafePath(p.Value) || p.Value.EndsWith('/')) throw new Exception("path must be an absolute file path of letters, digits, / _ . -");
                    break;
                case "dir":
                    if (!IsSafePath(p.Value)) throw new Exception("dir must be an absolute path of letters, digits, / _ . -");
                    break;
            }
        }
        return entry.Tier;
    }

    static bool IsSafeUsername(string s) =>
        s.Length is >= 1 and <= 32 &&
        (char.IsAsciiLetterLower(s[0]) || s[0] == '_') &&
        s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '-');

    static bool IsSafePath(string s) =>
        s.StartsWith('/') && s.Length <= 200 && !s.Contains("..") &&
        s.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '.' or '-');

    static bool IsIPv4(string s)
    {
        var parts = s.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p) <= 255);
    }

    static string ContentHash(string kind, string host, List<Param> ps)
    {
        var canon = string.Join(";", ps.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        return Sha256Hex($"{kind}|{host}|{canon}");
    }

    // ---------- SHA-256 ----------
    // A small managed implementation, so we don't depend on System.Security.Cryptography
    // being available inside the WebAssembly module. Output: lowercase hex.

    static readonly uint[] K =
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

    public static string Sha256Hex(string text)
    {
        var msg = Encoding.UTF8.GetBytes(text);
        ulong bitLen = (ulong)msg.Length * 8;
        int padLen = ((msg.Length + 9 + 63) / 64) * 64;
        var data = new byte[padLen];
        Array.Copy(msg, data, msg.Length);
        data[msg.Length] = 0x80;
        for (int i = 0; i < 8; i++) data[padLen - 1 - i] = (byte)(bitLen >> (8 * i));

        uint h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a,
             h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;
        var w = new uint[64];

        for (int chunk = 0; chunk < padLen; chunk += 64)
        {
            for (int i = 0; i < 16; i++)
                w[i] = (uint)(data[chunk + 4 * i] << 24 | data[chunk + 4 * i + 1] << 16 | data[chunk + 4 * i + 2] << 8 | data[chunk + 4 * i + 3]);
            for (int i = 16; i < 64; i++)
            {
                uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
            for (int i = 0; i < 64; i++)
            {
                uint S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = h + S1 + ch + K[i] + w[i];
                uint S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = S0 + maj;
                h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            h0 += a; h1 += b; h2 += c; h3 += d; h4 += e; h5 += f; h6 += g; h7 += h;
        }

        var sb = new StringBuilder(64);
        foreach (var v in new[] { h0, h1, h2, h3, h4, h5, h6, h7 }) sb.Append(v.ToString("x8"));
        return sb.ToString();
    }
}
