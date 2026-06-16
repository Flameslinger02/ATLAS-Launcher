namespace Atlas.Core.Services;

/// <summary>
/// Encrypts and decrypts small secret strings (Steam API key, Discord token, …) at rest.
/// The Windows DPAPI implementation binds ciphertext to the current Windows user on the current
/// machine, so a leaked <c>settings.json</c> is useless on any other account or PC.
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Encrypts <paramref name="plaintext"/> and returns a Base64 string suitable for JSON storage.
    /// Returns an empty string when <paramref name="plaintext"/> is null or empty.
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a Base64 string produced by <see cref="Encrypt"/>. Returns <c>null</c> when the input
    /// is null/empty or cannot be decrypted (e.g. the ciphertext came from another user/machine).
    /// </summary>
    string? Decrypt(string protectedBase64);
}
