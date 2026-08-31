using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Yemekhane.Application.Settings;

namespace Yemekhane.Infrastructure.Settings;

[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OkulYemek.SystemSettings.v1");

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy,
            DataProtectionScope.LocalMachine));
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy,
            DataProtectionScope.LocalMachine));
    }
}
