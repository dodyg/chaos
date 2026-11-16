using System.Security.Cryptography;
using Chaos.Application.Abstractions;

namespace Chaos.Desktop.Windows.Security;

/// <summary>Protects secrets with the Windows data protection API, scoped to the current user.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Chaos.Secrets.v1"u8.ToArray();

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}
