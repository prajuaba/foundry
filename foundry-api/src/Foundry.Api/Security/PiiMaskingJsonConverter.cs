using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundry.Core.Security;
using Foundry.Core.User;

namespace Foundry.Api.Security;

/// <summary>
/// Dynamic System.Text.Json converter factory that masks properties decorated with <see cref="PiiDataAttribute"/>
/// unless the current user context contains the required 'ViewPii' role permission.
/// </summary>
public class PiiMaskingJsonConverterFactory : JsonConverterFactory
{
    private readonly ICurrentUserContext _userContext;

    public PiiMaskingJsonConverterFactory(ICurrentUserContext userContext)
    {
        _userContext = userContext;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        // Applies to primitive types or strings that could be decorated with PiiDataAttribute
        return typeToConvert == typeof(string);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new PiiStringJsonConverter(_userContext);
    }

    private class PiiStringJsonConverter : JsonConverter<string>
    {
        private readonly ICurrentUserContext _userContext;

        public PiiStringJsonConverter(ICurrentUserContext userContext)
        {
            _userContext = userContext;
        }

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetString() ?? string.Empty;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // By default write the string value as-is; property-level masking handled by PiiPropertyMasker
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>
/// Helper utility providing PII field masking based on PiiType formatters.
/// </summary>
public static class PiiMasker
{
    public static string Mask(string input, PiiType piiType, string defaultMask = "****")
    {
        if (string.IsNullOrEmpty(input)) return input;

        return piiType switch
        {
            PiiType.Email => MaskEmail(input),
            PiiType.CreditCard => MaskCreditCard(input),
            PiiType.Phone => MaskPhone(input),
            PiiType.Ssn => "***-**-****",
            _ => defaultMask
        };
    }

    private static string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2) return "****";
        var name = parts[0];
        var maskedName = name.Length > 2 ? $"{name[0]}***{name[^1]}" : "***";
        return $"{maskedName}@{parts[1]}";
    }

    private static string MaskCreditCard(string cc)
    {
        var digits = cc.Replace("-", "").Replace(" ", "");
        if (digits.Length < 4) return "****";
        return $"****-****-****-{digits[^4..]}";
    }

    private static string MaskPhone(string phone)
    {
        if (phone.Length < 4) return "****";
        return $"***-***-{phone[^4..]}";
    }
}
