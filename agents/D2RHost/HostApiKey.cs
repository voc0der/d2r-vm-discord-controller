using System.Security.Cryptography;
using System.Text;

namespace D2RHost;

/// <summary>
/// Mints and checks the single API key that guards the host's HTTP command surface.
/// </summary>
/// <remarks>
/// Only the hash is ever persisted, so the config file cannot leak a working key the way it would
/// if the plaintext were stored beside it - the operator is shown the key once and that is the only
/// time it exists anywhere but their clipboard.
///
/// The hash is a plain SHA-256, deliberately, not PBKDF2 or Argon2. Those exist to make guessing a
/// low-entropy human password expensive; this key is 256 bits from a CSPRNG, so there is nothing to
/// guess and an iterated hash would only buy a per-request cost on a check that runs on every API
/// call. What does matter is comparing in constant time, which is done below.
/// </remarks>
internal static class HostApiKey
{
    /// <summary>Marks a string as one of ours in logs and in an operator's password manager.</summary>
    public const string Prefix = "d2rk_";

    private const int SecretBytes = 32;

    public static GeneratedApiKey Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var key = Prefix + Base64UrlEncode(secret);
        return new GeneratedApiKey(key, DescribeKey(key), ComputeHash(key));
    }

    public static string ComputeHash(string key)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    /// <summary>
    /// A non-secret handle for one key - its prefix plus the first few characters - so logs and
    /// <c>/d2r config show</c> can say which key is installed without printing a usable one.
    /// </summary>
    public static string DescribeKey(string key)
    {
        var body = key.StartsWith(Prefix, StringComparison.Ordinal) ? key[Prefix.Length..] : key;
        var visible = body.Length <= 6 ? body : body[..6];
        return $"{Prefix}{visible}...";
    }

    public static bool Matches(string? presented, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        // Fixed-length hex on both sides, compared without an early exit, so a caller cannot learn
        // the stored hash one character at a time from how long the comparison took.
        var presentedHash = Encoding.UTF8.GetBytes(ComputeHash(presented));
        var expected = Encoding.UTF8.GetBytes(storedHash.Trim().ToLowerInvariant());
        return presentedHash.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(presentedHash, expected);
    }

    /// <summary>
    /// Pulls the key out of either header form: <c>Authorization: Bearer &lt;key&gt;</c>, which is
    /// what most HTTP clients and agent frameworks send by default, or a plain <c>X-API-Key</c>,
    /// which is easier to type into a curl one-liner.
    /// </summary>
    public static string? ReadPresentedKey(string? authorizationHeader, string? apiKeyHeader)
    {
        if (!string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            return apiKeyHeader.Trim();
        }

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        var value = authorizationHeader.Trim();
        const string bearer = "Bearer ";
        return value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            ? value[bearer.Length..].Trim()
            : value;
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

internal sealed record GeneratedApiKey(string Key, string KeyId, string Hash);
