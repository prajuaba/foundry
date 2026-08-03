using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Foundry.Core.Entities;
using Foundry.Core.Security;

namespace Foundry.Rules;

/// <summary>
/// Serialises a transition request for the activity log, with declared-sensitive values removed.
/// </summary>
/// <remarks>
/// <para>
/// The log used to store <c>JsonSerializer.Serialize(request)</c> — the whole command, as sent. The
/// framework goes to some length to protect these values on the entity: <c>[Encrypt]</c> encrypts
/// them at rest, <c>[Mask]</c> and <c>[SensitiveData]</c> withhold them from anyone without the
/// scope, and the PII converter masks them on the way out. None of that applied here, so a card
/// number or a national identifier travelling through a transition was written to a second
/// collection in clear text, where none of the entity's protections reach it.
/// </para>
/// <para>
/// Redaction is by declaration, not by guessing at names. A property is withheld when it carries
/// <see cref="SensitiveDataAttribute"/> or <see cref="PiiDataAttribute"/> — the same declarations the
/// entity's own protections key off, so the two cannot disagree about what is sensitive.
/// </para>
/// <para>
/// Top-level properties only. Transition commands are flat records by construction; a nested object
/// is serialised whole, and a sensitive value hidden inside one would not be caught. That is a real
/// limit rather than an oversight, and the alternative — a recursive walk with cycle detection over
/// arbitrary caller types — is a larger thing than this needs to be.
/// </para>
/// </remarks>
public static class WorkflowPayloadRedactor
{
    /// <summary>What a withheld value is replaced with.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>Serialises <paramref name="request"/>, withholding values declared sensitive.</summary>
    public static string Serialize(object? request)
    {
        if (request is null) return "{}";

        try
        {
            var projection = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var property in request.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                projection[property.Name] = IsSensitive(property)
                    ? Redacted
                    : SafeRead(property, request);
            }

            return JsonSerializer.Serialize(projection);
        }
        catch (Exception ex)
        {
            // The log entry is written whatever happens. Losing the record of a transition because
            // its payload could not be serialised would be a worse outcome than losing the payload.
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["_error"] = "The request payload could not be serialised for the activity log.",
                ["_exception"] = ex.GetType().Name
            });
        }
    }

    private static bool IsSensitive(PropertyInfo property)
        => property.GetCustomAttribute<SensitiveDataAttribute>() is not null
        || property.GetCustomAttribute<PiiDataAttribute>() is not null;

    private static object? SafeRead(PropertyInfo property, object source)
    {
        try
        {
            var value = property.GetValue(source);

            // Anything that is not a plain scalar is rendered as text rather than serialised
            // structurally, so an unexpected graph cannot make the log entry unbounded.
            return value switch
            {
                null => null,
                string or bool or DateTime or DateTimeOffset or Guid => value,
                _ when value.GetType().IsPrimitive || value is decimal => value,
                _ => value.ToString()
            };
        }
        catch (TargetInvocationException)
        {
            return "[unreadable]";
        }
    }
}
