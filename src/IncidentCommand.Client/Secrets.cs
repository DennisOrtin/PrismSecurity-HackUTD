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
/// The commander's app writes them automatically (ServiceProvisioner); the services only read them.
public static class ServiceToken
{
    public static readonly string[] Roles = { "agent", "reviewer", "parser", "executor" };
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IncidentCommand.Service.v1");
    public static string PathFor(string role) => Path.Combine(AppPaths.Services, $"{role}.token");
    static string IdPathFor(string role) => Path.Combine(AppPaths.Services, $"{role}.id");

    public static void Save(string role, string token, string identity = "")
    {
        File.WriteAllBytes(PathFor(role), ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        File.WriteAllText(IdPathFor(role), identity);
    }

    public static string? Load(string role)
    {
        try
        {
            return File.Exists(PathFor(role))
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathFor(role)), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (IOException) { return null; }   // being rewritten right now; try again shortly
    }

    /// The identity (hex) the saved token belongs to, so the app can tell if it's still the enrolled one.
    public static string? LoadIdentity(string role) =>
        File.Exists(IdPathFor(role)) ? File.ReadAllText(IdPathFor(role)).Trim() : null;

    public static DateTime Stamp(string role) =>
        File.Exists(PathFor(role)) ? File.GetLastWriteTimeUtc(PathFor(role)) : DateTime.MinValue;
}

public static class Hashing
{
    /// Same algorithm and output format as Module.Sha256Hex on the server.
    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
