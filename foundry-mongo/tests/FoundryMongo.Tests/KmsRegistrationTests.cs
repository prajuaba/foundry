using System;
using Foundry.Core.Security;
using Foundry.Mongo.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FoundryMongo.Tests;

/// <summary>
/// Who supplies the key-management client for envelope encryption.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddFoundryMongo</c> used to register <c>LocalMockKmsClient</c> as a default. Its master key is
/// a constant in Foundry's published source, so an application that selected envelope encryption and
/// did not register a real client encrypted every <c>[Encrypt]</c> field under a key anyone can read
/// — with nothing said at startup, and nothing observable at rest.
/// </para>
/// <para>
/// Because it was registered with <c>TryAdd</c>, a real client registered after the call was
/// silently ignored: correctness depended on the order of two lines in Program.cs, and getting it
/// wrong looked exactly like getting it right.
/// </para>
/// </remarks>
public class KmsRegistrationTests
{
    private static ServiceCollection Services() => new();

    private static Action<FoundryMongoOptions> Envelope(string encryptedDek) => options =>
    {
        options.ConnectionString = "mongodb://localhost:27017";
        options.DatabaseName = "kms-registration-tests";
        options.EncryptedEncryptionKey = encryptedDek;
    };

    /// <summary>A DEK wrapped by the mock, so the provider has something well-formed to unwrap.</summary>
    private static string WrappedKey()
    {
        var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("12345678901234567890123456789012"));
        return new LocalMockKmsClient().EncryptKey(raw);
    }

    [Fact]
    public void NoKmsClientIsRegisteredByDefault()
    {
        var services = Services();
        services.AddFoundryMongo(Envelope(WrappedKey()));

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IKmsClient>());
    }

    [Fact]
    public void SelectingEnvelopeEncryptionWithoutAKmsClientThrowsWithInstructions()
    {
        var services = Services();
        services.AddFoundryMongo(Envelope(WrappedKey()));

        using var provider = services.BuildServiceProvider();

        // Resolution rather than registration, so the failure does not depend on whether the caller
        // registered their client before or after AddFoundryMongo.
        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IEncryptionProvider>());

        Assert.Contains("no IKmsClient is registered", error.Message);
        Assert.Contains("LocalMockKmsClient", error.Message);
    }

    /// <summary>
    /// A distinguishable client, so these tests can tell whose client was used.
    /// </summary>
    /// <remarks>
    /// Asserting only that an <c>IEncryptionProvider</c> was produced would pass whether the
    /// caller's client or a framework default answered — which is exactly the confusion being
    /// tested for. This one records that it was asked.
    /// </remarks>
    private sealed class RecordingKmsClient : IKmsClient
    {
        private readonly LocalMockKmsClient _inner = new("a-different-master-key-for-this-test");

        public bool WasAsked { get; private set; }

        public string DecryptKey(string encryptedDekBase64)
        {
            WasAsked = true;
            return _inner.DecryptKey(encryptedDekBase64);
        }

        public string EncryptKey(string plaintextDekBase64)
        {
            WasAsked = true;
            return _inner.EncryptKey(plaintextDekBase64);
        }
    }

    [Fact]
    public void ACallerSuppliedKmsClientIsTheOneUsed()
    {
        var kms = new RecordingKmsClient();
        var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("12345678901234567890123456789012"));

        var services = Services();
        services.AddSingleton<IKmsClient>(kms);
        services.AddFoundryMongo(Envelope(kms.EncryptKey(raw)));

        using var provider = services.BuildServiceProvider();
        var encryption = provider.GetRequiredService<IEncryptionProvider>();

        Assert.Equal("round-trips", encryption.Decrypt(encryption.Encrypt("round-trips")));
        Assert.True(kms.WasAsked, "the caller's IKmsClient was never consulted");
    }

    [Fact]
    public void AKmsClientRegisteredAfterAddFoundryMongoIsStillTheOneUsed()
    {
        // The ordering trap: TryAdd meant the framework's own default won this race, so a correct
        // registration placed one line too late was ignored in silence.
        var kms = new RecordingKmsClient();
        var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("12345678901234567890123456789012"));

        var services = Services();
        services.AddFoundryMongo(Envelope(kms.EncryptKey(raw)));
        services.AddSingleton<IKmsClient>(kms);

        using var provider = services.BuildServiceProvider();
        var encryption = provider.GetRequiredService<IEncryptionProvider>();

        Assert.Equal("round-trips", encryption.Decrypt(encryption.Encrypt("round-trips")));
        Assert.True(kms.WasAsked, "the caller's IKmsClient was never consulted");
    }

    [Fact]
    public void TheAesPathNeedsNoKmsClient()
    {
        var services = Services();
        services.AddFoundryMongo(options =>
        {
            options.ConnectionString = "mongodb://localhost:27017";
            options.DatabaseName = "kms-registration-tests";
            options.EncryptionKey = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("12345678901234567890123456789012"));
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<AesEncryptionProvider>(provider.GetRequiredService<IEncryptionProvider>());
    }
}
