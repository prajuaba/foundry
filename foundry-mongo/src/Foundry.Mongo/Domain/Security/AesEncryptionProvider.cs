using System;
using System.IO;
using System.Security.Cryptography;

namespace Foundry.Core.Security;

/// <summary>
/// Implements standard AES-256-CBC symmetric encryption with randomized IVs prepended to ciphertexts.
/// </summary>
public sealed class AesEncryptionProvider : IEncryptionProvider
{
    private readonly byte[] _key;

    public AesEncryptionProvider(string base64Key)
    {
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new ArgumentException("Symmetric key must be exactly 256 bits (32 bytes) base64 encoded.", nameof(base64Key));
        }
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream();
        
        // Write the IV first to the stream prefix
        ms.Write(aes.IV, 0, aes.IV.Length);

        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(plainText);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        byte[] fullCipher;
        try
        {
            fullCipher = Convert.FromBase64String(cipherText);
        }
        catch (FormatException)
        {
            // If the ciphertext is not a valid Base64 string, return it as-is (e.g. if it was written in plaintext before encryption was enabled)
            return cipherText;
        }

        using var aes = Aes.Create();
        aes.Key = _key;

        var ivLength = aes.BlockSize / 8;
        if (fullCipher.Length < ivLength) return cipherText;

        var iv = new byte[ivLength];
        var cipher = new byte[fullCipher.Length - ivLength];

        Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, cipher.Length);

        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var ms = new MemoryStream(cipher);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);

        return sr.ReadToEnd();
    }
}
