using System.Security.Cryptography;

namespace AgentCommon;

public static class SecretGenerator
{
    public static string Create(int byteCount = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
