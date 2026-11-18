using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RepoRunner.Core;

/// <summary>AES-256-GCM sealing. Blob layout: version(1) | nonce(12) | tag(16) | ciphertext.</summary>
public static class Aead
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16;

    public static byte[] Seal(byte[] key, byte[] plaintext, string aad)
    {
        var blob = new byte[1 + NonceSize + TagSize + plaintext.Length];
        blob[0] = Version;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize),
            Encoding.UTF8.GetBytes(aad));
        return blob;
    }

    public static byte[] Open(byte[] key, byte[] blob, string aad)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != Version)
            throw new CryptographicException("Unrecognised ciphertext format.");
        var plaintext = new byte[blob.Length - 1 - NonceSize - TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize),
            plaintext, Encoding.UTF8.GetBytes(aad));
        return plaintext;
    }

    public static byte[] SealText(byte[] key, string text, string aad) => Seal(key, Encoding.UTF8.GetBytes(text), aad);
    public static string OpenText(byte[] key, byte[] blob, string aad) => Encoding.UTF8.GetString(Open(key, blob, aad));
}

public static class Kdf
{
    /// <summary>Argon2id cost. Lowered by tests only.</summary>
    public static int MemoryKb { get; set; } = 64 * 1024;
    public static int Iterations { get; set; } = 3;

    public static byte[] FromPassphrase(string passphrase, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            MemorySize = MemoryKb,
            Iterations = Iterations,
        };
        return argon.GetBytes(32);
    }

    public static byte[] Derive(byte[] keyMaterial, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, 32, salt: null, info: Encoding.UTF8.GetBytes(info));
}

/// <summary>Wraps the vault key with Windows DPAPI for the current user.</summary>
public static class KeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RepoRunner.Vault.v1");

    public static byte[] Wrap(byte[] key) => ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unwrap(byte[] wrapped) => ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.CurrentUser);
}
