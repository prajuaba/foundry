using Foundry.Core.Entities;
using Foundry.Core.Outbox;
using Xunit;

namespace Foundry.Core.Tests;

/// <summary>
/// The outbox captures the entity from the caller's command, before the repository has encrypted
/// anything, so a field declared Encrypt reached Kafka as the plaintext the caller sent. These pin
/// the redaction that closes that, and pin that it leaves everything else alone.
/// </summary>
public class SensitiveFieldRedactorTests
{
    private sealed record Person
    {
        public string Name { get; init; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Encrypt)]
        public string Email { get; init; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Mask)]
        public string Phone { get; init; } = string.Empty;

        public int Age { get; init; }
    }

    private sealed record Plain
    {
        public string Name { get; init; } = string.Empty;
    }

    [Fact]
    public void EncryptedAndMaskedFieldsAreEmptied()
    {
        var source = new Person { Name = "Ada", Email = "ada@example.com", Phone = "+1 555 0100", Age = 36 };

        var redacted = SensitiveFieldRedactor.Redact(source);

        Assert.Equal(string.Empty, redacted.Email);
        Assert.Equal(string.Empty, redacted.Phone);
    }

    [Fact]
    public void EverythingElseSurvives()
    {
        // A redactor that empties the whole entity protects the data and destroys the event: a
        // consumer needs to know which record changed.
        var source = new Person { Name = "Ada", Email = "ada@example.com", Phone = "+1 555 0100", Age = 36 };

        var redacted = SensitiveFieldRedactor.Redact(source);

        Assert.Equal("Ada", redacted.Name);
        Assert.Equal(36, redacted.Age);
    }

    [Fact]
    public void TheOriginalIsNotMutated()
    {
        // The entity handed in belongs to the caller's in-flight command. Redacting it in place
        // would strip the values before the repository ever got to store them.
        var source = new Person { Name = "Ada", Email = "ada@example.com", Phone = "+1 555 0100", Age = 36 };

        SensitiveFieldRedactor.Redact(source);

        Assert.Equal("ada@example.com", source.Email);
        Assert.Equal("+1 555 0100", source.Phone);
    }

    [Fact]
    public void AnEntityWithNothingSensitiveIsReturnedUnchanged()
    {
        var source = new Plain { Name = "Ada" };

        var redacted = SensitiveFieldRedactor.Redact(source);

        Assert.Same(source, redacted);
    }
}
