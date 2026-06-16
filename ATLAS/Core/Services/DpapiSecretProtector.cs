using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Atlas.Core.Services;

/// <inheritdoc cref="ISecretProtector"/>
/// <remarks>
/// Uses Windows DPAPI (<see cref="ProtectedData"/>) with <see cref="DataProtectionScope.CurrentUser"/>.
/// Windows-only; the consuming app targets Windows so the platform guard is satisfied by the
/// <see cref="SupportedOSPlatformAttribute"/> annotation.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <inheritdoc/>
    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            var data = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DPAPI encryption failed.");
            return "";
        }
    }

    /// <inheritdoc/>
    public string? Decrypt(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(protectedBase64);
            var data = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            // Wrong user/machine, tampered data, or non-Base64 input — secret needs re-entry.
            Log.Warning(ex, "DPAPI decryption failed; secret could not be recovered.");
            return null;
        }
    }
}
