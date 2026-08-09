using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MongoDB.Bson;

namespace Foundry.Api.Endpoints;

/// <summary>
/// Converts one query-string or route value into the CLR type a property expects.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the two places that read values off a request: the entity filter built by
/// <see cref="DynamicEndpointRouteBuilder"/> and the request records bound by
/// <see cref="CustomRequestBinder"/>. One conversion table rather than two, because the failure
/// mode of disagreement is that a value is accepted on one route and rejected on the other for no
/// reason a caller can see.
/// </para>
/// <para>
/// The set of types is the IR's own scalar vocabulary, which is what a schema can put on a property
/// and therefore what can turn up here. The previous implementation handled ObjectId, int, decimal,
/// bool and enums and fell through to <see cref="Convert.ChangeType(object, Type)"/> for the rest —
/// which does not know <see cref="Guid"/>, <see cref="DateOnly"/> or <see cref="TimeOnly"/>, so
/// filtering an entity on a property of any of those three answered 400 for a value that was
/// perfectly valid.
/// </para>
/// <para>
/// Parsing is invariant. A query string is a wire format and the server's locale has no business
/// deciding whether <c>1.5</c> is one-and-a-half; the previous code used the ambient culture, so a
/// host set to a comma-decimal locale read <c>1.5</c> as 15.
/// </para>
/// </remarks>
public static class QueryValueBinder
{
    /// <summary>
    /// Converts <paramref name="raw"/> to <paramref name="targetType"/>.
    /// </summary>
    /// <exception cref="ValidationException">
    /// The value is not valid for the type. Rejected rather than skipped: a parameter the caller
    /// could not have known was unparseable, silently dropped, returns 200 with a result set
    /// <em>wider</em> than the one asked for. <see cref="ValidationException"/> maps to 400 in
    /// GlobalExceptionHandler, which is what this is.
    /// </exception>
    public static object? Convert(string raw, Type targetType, string parameterName, string propertyName)
    {
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (Nullable.GetUnderlyingType(targetType) is not null && string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            if (type == typeof(string)) return raw;
            if (type == typeof(ObjectId)) return ObjectId.Parse(raw);
            if (type.IsEnum) return Enum.Parse(type, raw, ignoreCase: true);
            if (type == typeof(bool)) return bool.Parse(raw);
            if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return decimal.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(Guid)) return Guid.Parse(raw);

            // Normalised to UTC, because the data layer stores UTC: an instant given with an
            // offset is converted rather than compared as written, and one given without a zone is
            // taken as UTC rather than as the server's local time, which would make the same query
            // mean different things on two hosts.
            //
            // Not RoundtripKind: it is mutually exclusive with AdjustToUniversal and pairing them
            // throws ArgumentException, which this method's own catch then reports as a malformed
            // value -- so a perfectly valid '2026-10-01T00:00:00Z' came back 400.
            if (type == typeof(DateTime))
            {
                return DateTime.Parse(
                    raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            }

            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(DateOnly)) return DateOnly.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(TimeOnly)) return TimeOnly.Parse(raw, CultureInfo.InvariantCulture);

            return System.Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ValidationException(
                $"Query parameter '{parameterName}' has value '{raw}', which is not valid for "
                + $"{propertyName} ({type.Name}).");
        }
    }
}
