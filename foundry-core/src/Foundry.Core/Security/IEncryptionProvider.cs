using System;

namespace Foundry.Core.Security;

/// <summary>
/// Defines symmetric encryption and decryption operations used for protecting sensitive data fields.
/// </summary>
public interface IEncryptionProvider
{
    /// <summary>Encrypts a plaintext string and returns a base64-encoded ciphertext.</summary>
    string Encrypt(string plainText);

    /// <summary>Decrypts a base64-encoded ciphertext string and returns the plaintext.</summary>
    string Decrypt(string cipherText);
}
