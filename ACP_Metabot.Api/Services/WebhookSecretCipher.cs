using System.Security.Cryptography;
using System.Text;

namespace ACP_Metabot.Api.Services;

/// AES-256-GCM cipher for webhook secrets at rest. Pre-2026-05-24 Metabot
/// stored webhook_secret in plaintext across 3 subscription tables
/// (watchOffering, marketplacePulseSub, dailyRiskWatch); DB compromise
/// would hand attackers HMAC keys to forge tick deliveries to every buyer.
///
/// Encryption is opt-in via WEBHOOK_SECRET_ENCRYPTION_KEY (base64 32 bytes).
/// When unset, the cipher is a no-op (plaintext passthrough). Migration is
/// lazy via "v1:" prefix sentinel — legacy plaintext rows continue to read
/// unchanged. Portfolio pattern.
public sealed class WebhookSecretCipher
{
    private const string V1Prefix = "v1:";
    private readonly byte[]? _key;

    public WebhookSecretCipher(IConfiguration cfg)
    {
        var rawKey = Environment.GetEnvironmentVariable("WEBHOOK_SECRET_ENCRYPTION_KEY")
            ?? cfg["WebhookSecretEncryptionKey"];
        if (string.IsNullOrWhiteSpace(rawKey)) { _key = null; return; }
        try
        {
            var bytes = Convert.FromBase64String(rawKey.Trim());
            if (bytes.Length != 32)
                throw new InvalidOperationException(
                    $"WEBHOOK_SECRET_ENCRYPTION_KEY must decode to exactly 32 bytes (AES-256); got {bytes.Length}.");
            _key = bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "WEBHOOK_SECRET_ENCRYPTION_KEY must be base64-encoded (32 bytes).", ex);
        }
    }

    public bool IsEncryptionEnabled => _key is not null;

    public string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (_key is null) return plaintext;
        Span<byte> iv = stackalloc byte[12];
        RandomNumberGenerator.Fill(iv);
        var ptBytes = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[ptBytes.Length];
        Span<byte> tag = stackalloc byte[16];
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(iv, ptBytes, ct, tag);
        return $"{V1Prefix}{Convert.ToBase64String(iv)}." +
               $"{Convert.ToBase64String(tag)}." +
               $"{Convert.ToBase64String(ct)}";
    }

    public string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith(V1Prefix, StringComparison.Ordinal)) return stored;
        if (_key is null)
            throw new InvalidOperationException(
                "Stored webhook secret is encrypted (v1: prefix) but " +
                "WEBHOOK_SECRET_ENCRYPTION_KEY is not configured. Cannot decrypt.");
        var parts = stored.AsSpan(V1Prefix.Length).ToString().Split('.');
        if (parts.Length != 3)
            throw new InvalidOperationException("Malformed encrypted webhook secret (expected iv.tag.ct).");
        var iv = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var ct = Convert.FromBase64String(parts[2]);
        if (iv.Length != 12 || tag.Length != 16)
            throw new InvalidOperationException("Encrypted webhook secret IV or tag length wrong.");
        var pt = new byte[ct.Length];
        using var gcm = new AesGcm(_key, 16);
        gcm.Decrypt(iv, ct, tag, pt);
        return Encoding.UTF8.GetString(pt);
    }
}
