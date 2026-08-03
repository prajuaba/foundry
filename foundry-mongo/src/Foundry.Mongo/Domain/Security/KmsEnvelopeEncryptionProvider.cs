using System;
using System.Text;

namespace Foundry.Core.Security
{
    /// <summary>
    /// Interface for KMS client operations.
    /// </summary>
    public interface IKmsClient
    {
        /// <summary>
        /// Decrypts an encrypted data encryption key.
        /// </summary>
        /// <param name="encryptedDekBase64">The base64 encoded encrypted data encryption key.</param>
        /// <returns>The decrypted data encryption key.</returns>
        string DecryptKey(string encryptedDekBase64);

        /// <summary>
        /// Encrypts a data encryption key.
        /// </summary>
        /// <param name="plaintextDekBase64">The base64 encoded plaintext data encryption key.</param>
        /// <returns>The base64 encoded encrypted data encryption key.</returns>
        string EncryptKey(string plaintextDekBase64);
    }

    /// <summary>
    /// Mock KMS client for local development and testing.
    /// </summary>
    /// <summary>
    /// A stand-in for a key management service, for local development and tests only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This provides no security.</b> Its default master key is a constant in Foundry's published
    /// source, so anything it protects is readable by anyone who has read this file. It exists so the
    /// envelope-encryption path can be exercised without a cloud dependency.
    /// </para>
    /// <para>
    /// <c>AddFoundryMongo</c> deliberately does not register it. It did once, as a default that
    /// applied whenever the caller had not registered anything else, which meant an application could
    /// select production envelope encryption and get this instead without a word being said.
    /// </para>
    /// </remarks>
    public class LocalMockKmsClient : IKmsClient
    {
        private readonly string _masterKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalMockKmsClient"/> class.
        /// </summary>
        /// <param name="masterKey">
        /// The master key to use. Defaults to a value published in Foundry's source, which protects
        /// nothing; supply your own only to make tests deterministic, never to secure real data.
        /// </param>
        public LocalMockKmsClient(string masterKey = "mock-master-key-for-local-testing")
        {
            _masterKey = masterKey;
        }

        /// <summary>
        /// Decrypts the encrypted data encryption key.
        /// </summary>
        /// <param name="encryptedDekBase64">The base64 encoded encrypted data encryption key.</param>
        /// <returns>The decrypted data encryption key.</returns>
        public string DecryptKey(string encryptedDekBase64)
        {
            var encryptedBytes = Convert.FromBase64String(encryptedDekBase64);
            var encryptedString = Encoding.UTF8.GetString(encryptedBytes);
            
            if (encryptedString.StartsWith("ENC:"))
            {
                return encryptedString.Substring(4);
            }
            
            return encryptedString;
        }

        /// <summary>
        /// Encrypts the data encryption key.
        /// </summary>
        /// <param name="plaintextDekBase64">The base64 encoded plaintext data encryption key.</param>
        /// <returns>The base64 encoded encrypted data encryption key.</returns>
        public string EncryptKey(string plaintextDekBase64)
        {
            var prefixedKey = "ENC:" + plaintextDekBase64;
            var bytes = Encoding.UTF8.GetBytes(prefixedKey);
            return Convert.ToBase64String(bytes);
        }
    }

    /// <summary>
    /// Encryption provider that uses KMS for envelope encryption.
    /// </summary>
    public class KmsEnvelopeEncryptionProvider : IEncryptionProvider
    {
        private readonly IEncryptionProvider _aesEncryptionProvider;
        private readonly string _decryptedDek;

        /// <summary>
        /// Initializes a new instance of the <see cref="KmsEnvelopeEncryptionProvider"/> class.
        /// </summary>
        /// <param name="kmsClient">The KMS client to use for decrypting the data encryption key.</param>
        /// <param name="encryptedDekBase64">The base64 encoded encrypted data encryption key.</param>
        public KmsEnvelopeEncryptionProvider(IKmsClient kmsClient, string encryptedDekBase64)
        {
            _decryptedDek = kmsClient.DecryptKey(encryptedDekBase64);
            _aesEncryptionProvider = new AesEncryptionProvider(_decryptedDek);
        }

        /// <summary>
        /// Encrypts the provided plain text.
        /// </summary>
        /// <param name="plainText">The plain text to encrypt.</param>
        /// <returns>The base64 encoded encrypted cipher text.</returns>
        public string Encrypt(string plainText)
        {
            return _aesEncryptionProvider.Encrypt(plainText);
        }

        /// <summary>
        /// Decrypts the provided cipher text.
        /// </summary>
        /// <param name="cipherText">The base64 encoded encrypted cipher text to decrypt.</param>
        /// <returns>The decrypted plain text.</returns>
        public string Decrypt(string cipherText)
        {
            return _aesEncryptionProvider.Decrypt(cipherText);
        }
    }
}
