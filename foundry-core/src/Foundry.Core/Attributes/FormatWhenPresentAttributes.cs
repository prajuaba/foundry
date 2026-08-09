using System;
using System.ComponentModel.DataAnnotations;

namespace Foundry.Core.Attributes;

/// <summary>
/// Format validators that check the shape of a value only when one is present.
/// </summary>
/// <remarks>
/// <para>
/// The compiler used to emit the stock <see cref="UrlAttribute"/>, <see cref="PhoneAttribute"/> and
/// <see cref="EmailAddressAttribute"/> directly. Those return <c>true</c> for <c>null</c> and
/// <c>false</c> for the empty string — and a generated property is a non-nullable <c>string</c>
/// initialised to <see cref="string.Empty"/>, so an optional field that the caller simply did not
/// send arrived as <c>""</c> and failed validation.
/// </para>
/// <para>
/// The effect was that declaring <c>Url</c> or <c>Phone</c> on an optional property made it
/// mandatory: <c>POST /api/resources</c> without an <c>avatarUrl</c> answered 400, naming a field
/// the schema never said was required. An optional field that cannot be omitted is a worse defect
/// than an unvalidated one, so the attribute was simply dropped from the schema instead — which
/// bought the optionality back by giving up the validation entirely.
/// </para>
/// <para>
/// Presence and shape are separate questions, and DataAnnotations already composes them that way:
/// <c>[Required]</c> says a value must be supplied, <c>[StringLength]</c> and the format validators
/// say what a supplied value must look like. These restore that composition. A property declaring
/// both <c>Required</c> and <c>Url</c> still rejects an empty value — through <c>[Required]</c>,
/// which is the attribute whose job that is.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class UrlWhenPresentAttribute : ValidationAttribute
{
    private static readonly UrlAttribute Inner = new();

    /// <inheritdoc />
    public override bool IsValid(object? value)
        => IsAbsent(value) || Inner.IsValid(value);

    /// <inheritdoc />
    public override string FormatErrorMessage(string name)
        => ErrorMessage ?? $"The {name} field is not a valid fully-qualified http, https, or ftp URL.";

    internal static bool IsAbsent(object? value)
        => value is null || (value is string s && string.IsNullOrWhiteSpace(s));
}

/// <inheritdoc cref="UrlWhenPresentAttribute"/>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class PhoneWhenPresentAttribute : ValidationAttribute
{
    private static readonly PhoneAttribute Inner = new();

    /// <inheritdoc />
    public override bool IsValid(object? value)
        => UrlWhenPresentAttribute.IsAbsent(value) || Inner.IsValid(value);

    /// <inheritdoc />
    public override string FormatErrorMessage(string name)
        => ErrorMessage ?? $"The {name} field is not a valid phone number.";
}

/// <inheritdoc cref="UrlWhenPresentAttribute"/>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class EmailAddressWhenPresentAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute Inner = new();

    /// <inheritdoc />
    public override bool IsValid(object? value)
        => UrlWhenPresentAttribute.IsAbsent(value) || Inner.IsValid(value);

    /// <inheritdoc />
    public override string FormatErrorMessage(string name)
        => ErrorMessage ?? $"The {name} field is not a valid e-mail address.";
}
