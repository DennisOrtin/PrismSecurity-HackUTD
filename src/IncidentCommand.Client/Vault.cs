using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace IncidentCommand.Client;

public sealed record VaultContents(string Username, string Token, string TotpSecret);

/// One encrypted file per human user:
///   DPAPI( salt[16] | nonce[12] | tag[16] | AES-256-GCM(Argon2id(password, salt), json) )
/// Wrong password => GCM tag mismatch => null. Copying the file to another machine fails at DPAPI.
/// Honest limit: .NET strings are immutable, so the decrypted token string lingers in memory until GC.
public static class Vault
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IncidentCommand.Vault.v1");

    public static string PathFor(string username) => Path.Combine(AppPaths.Vaults, $"{username}.vault");

    public static IReadOnlyList<string> Usernames() =>
        Directory.GetFiles(AppPaths.Vaults, "*.vault").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(x => x).ToList();

    public static void Save(VaultContents contents, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(password, salt);
        var plain = JsonSerializer.SerializeToUtf8Bytes(contents);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Encrypt(nonce, plain, cipher, tag);
            var blob = salt.Concat(nonce).Concat(tag).Concat(cipher).ToArray();
            File.WriteAllBytes(PathFor(contents.Username), ProtectedData.Protect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static VaultContents? Unlock(string username, string password)
    {
        var path = PathFor(username);
        if (!File.Exists(path)) return null;
        var blob = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        var salt = blob[..16];
        var nonce = blob[16..28];
        var tag = blob[28..44];
        var cipher = blob[44..];
        var key = DeriveKey(password, salt);
        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return JsonSerializer.Deserialize<VaultContents>(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;   // wrong password
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    static byte[] DeriveKey(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = 65536,    // 64 MiB
            Iterations = 3,
            DegreeOfParallelism = 2,
        };
        return argon.GetBytes(32);
    }
}

/// Five failures lock a username for five minutes. In-memory: restarting the app resets it.
/// Every attempt is also written to the audit chain once someone logs in.
public static class LoginGuard
{
    static readonly Dictionary<string, (int Failures, DateTime LockedUntil)> State = new();

    public static TimeSpan? LockedFor(string username)
    {
        if (State.TryGetValue(username, out var s) && s.LockedUntil > DateTime.UtcNow) return s.LockedUntil - DateTime.UtcNow;
        return null;
    }

    public static void Fail(string username)
    {
        State.TryGetValue(username, out var s);
        var failures = s.Failures + 1;
        State[username] = failures >= 5 ? (0, DateTime.UtcNow.AddMinutes(5)) : (failures, DateTime.MinValue);
    }

    public static void Succeed(string username) => State.Remove(username);
}
