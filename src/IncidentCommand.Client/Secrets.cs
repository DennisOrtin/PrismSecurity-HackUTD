using System.Security.Cryptography;
using System.Text;
using OtpNet;

namespace IncidentCommand.Client;

public static class TotpHelper
{
    public static string NewSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    /// Allows one 30-second step of clock drift either way.
    public static bool Verify(string secret, string code) =>
        new Totp(Base32Encoding.ToBytes(secret)).VerifyTotp(code.Trim(), out _, new VerificationWindow(1, 1));

    public static string ProvisioningUri(string secret, string label) =>
        $"otpauth://totp/IncidentCommand:{Uri.EscapeDataString(label)}?secret={secret}&issuer=IncidentCommand";
}

public static class InviteCodes
{
    const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I/L

    public static string Generate(int length = 10) =>
        new(Enumerable.Range(0, length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    /// Must match Module.Sha256Hex: lowercase hex of SHA-256 over UTF-8.
    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()))).ToLowerInvariant();
}

/// Tokens for the agent, reviewer and executor services: DPAPI only, since they start without a human.
public static class ServiceToken
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IncidentCommand.Service.v1");
    static string PathFor(string role) => Path.Combine(AppPaths.Services, $"{role}.token");

    public static void Save(string role, string token) =>
        File.WriteAllBytes(PathFor(role), ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));

    public static string? Load(string role) =>
        File.Exists(PathFor(role))
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathFor(role)), Entropy, DataProtectionScope.CurrentUser))
            : null;
}

public static class Hashing
{
    /// Same algorithm and output format as Module.Sha256Hex on the server.
    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
