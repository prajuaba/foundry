using System;

namespace Foundry.Core.Entities;

/// <summary>
/// Dictates how a sensitive field should be masked.
/// </summary>
public enum MaskingType
{
    /// <summary>Masks the entire value (e.g., "secret" becomes "******").</summary>
    Full,

    /// <summary>Preserves the last N characters of the value (e.g., "123456789" becomes "*****6789").</summary>
    Partial,

    /// <summary>Masks the username part of an email, preserving the domain (e.g., "john.doe@example.com" becomes "j***e@example.com").</summary>
    Email
}

/// <summary>
/// Dictates whether the sensitive field is masked in presentation/logs or encrypted at rest in MongoDB.
/// </summary>
public enum ProtectionType
{
    /// <summary>Masks the sensitive data for presentation/logs, storing it as plaintext in MongoDB.</summary>
    Mask,

    /// <summary>Encrypts the sensitive data at rest in MongoDB using AES-256 and decrypts it on read.</summary>
    Encrypt
}

/// <summary>
/// Decorates a property to identify it as containing sensitive information.
/// The DAL automatically masks this property in audit diff logs and either masks or encrypts the data at rest.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveDataAttribute : Attribute
{
    /// <summary>The protection strategy to apply (default is Mask).</summary>
    public ProtectionType Protection { get; set; } = ProtectionType.Mask;

    /// <summary>The masking strategy to apply.</summary>
    public MaskingType MaskingType { get; set; } = MaskingType.Full;

    /// <summary>The number of characters to preserve when using Partial masking.</summary>
    public int PreserveCount { get; set; } = 4;

    /// <summary>The character used to mask sensitive data (default is '*').</summary>
    public char MaskChar { get; set; } = '*';

    /// <summary>
    /// Masks a raw value using the configuration of this attribute.
    /// </summary>
    public string MaskValue(object? value)
    {
        if (value == null) return string.Empty;
        var str = value.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(str)) return string.Empty;

        return MaskingType switch
        {
            MaskingType.Full => new string(MaskChar, Math.Min(str.Length, 15)), // Cap full mask display to 15 chars for brevity
            MaskingType.Partial => MaskPartial(str, PreserveCount, MaskChar),
            MaskingType.Email => MaskEmail(str, MaskChar),
            _ => new string(MaskChar, str.Length)
        };
    }

    private static string MaskPartial(string str, int preserveCount, char maskChar)
    {
        if (str.Length <= preserveCount) return str;
        var maskLength = str.Length - preserveCount;
        return new string(maskChar, Math.Min(maskLength, 10)) + str[^preserveCount..];
    }

    private static string MaskEmail(string email, char maskChar)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 0) return new string(maskChar, Math.Min(email.Length, 10));

        var username = email[..atIndex];
        var domain = email[atIndex..];

        if (username.Length <= 2)
        {
            return username[0] + new string(maskChar, Math.Max(0, username.Length - 1)) + domain;
        }

        return username[0] + new string(maskChar, Math.Min(username.Length - 2, 5)) + username[^1] + domain;
    }
}
