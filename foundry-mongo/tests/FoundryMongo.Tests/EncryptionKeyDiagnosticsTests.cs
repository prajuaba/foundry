using System;
using System.Text;
using Foundry.Core.Security;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// The message a misconfigured field-encryption key produces.
/// </summary>
/// <remarks>
/// A raw 32-character passphrase is the obvious thing to supply for something called
/// <c>EncryptionKey</c>, and it produced a bare <see cref="FormatException"/> — "The input is not a
/// valid Base-64 string as it contains a non-base 64 character" — thrown from inside DI resolution
/// during startup. The stack named <c>AesEncryptionProvider..ctor</c> and neither the option that
/// was wrong nor the encoding it wanted.
/// </remarks>
public class EncryptionKeyDiagnosticsTests
{
    [Fact]
    public void RawTextSaysItMustBeBase64()
    {
        var raw = "DEVELOPMENT-ONLY-resourcify-key3"; // 32 characters, not base64

        var ex = Assert.Throws<ArgumentException>(() => new AesEncryptionProvider(raw));

        Assert.Contains("base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMessageShowsHowToGenerateAValidKey()
    {
        // An error that says what is wrong but not how to fix it costs a search; this one is a
        // single command away from correct.
        var ex = Assert.Throws<ArgumentException>(() => new AesEncryptionProvider("not-base64!!"));

        Assert.Contains("openssl rand -base64 32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedButShortKeySaysHowLongItDecodedTo()
    {
        // Valid base64, wrong length -- a different mistake that deserves a different message.
        var short16 = Convert.ToBase64String(Encoding.UTF8.GetBytes("1234567890123456"));

        var ex = Assert.Throws<ArgumentException>(() => new AesEncryptionProvider(short16));

        Assert.Contains("32 bytes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("16", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorrectKeyIsAccepted()
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));

        var provider = new AesEncryptionProvider(key);

        var cipher = provider.Encrypt("hello");
        Assert.NotEqual("hello", cipher);
        Assert.Equal("hello", provider.Decrypt(cipher));
    }

    [Fact]
    public void ANullKeyIsRejectedWithTheSameGuidance()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AesEncryptionProvider(null!));

        Assert.Contains("base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
