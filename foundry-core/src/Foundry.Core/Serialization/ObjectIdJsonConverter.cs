using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MongoDB.Bson;

namespace Foundry.Core.Serialization;

/// <summary>
/// Serializes <see cref="ObjectId"/> as its 24-character hexadecimal string.
/// </summary>
/// <remarks>
/// <para>
/// Without this, System.Text.Json treats <see cref="ObjectId"/> as a plain struct and writes its
/// public members instead: <c>{"Timestamp":1785031211,"CreationTime":"2026-07-26T02:00:11Z"}</c>.
/// That does not round-trip — deserialising it yields <c>ObjectId.Empty</c>, because there is no
/// constructor STJ can bind those members to.
/// </para>
/// <para>
/// The consequence was silent and severe. A client POSTing an entity had its id decoded as
/// <c>ObjectId.Empty</c>; the MongoDB driver's id generator then treats an empty id as "unset"
/// and assigns a fresh one at insert time. The document persisted under an id the caller had
/// never seen, so a subsequent GET by the original id returned 404 while the record sat in the
/// collection. Responses were equally affected, returning an id shape no client could use.
/// </para>
/// <para>
/// Register on both sides of the wire — <c>ConfigureHttpJsonOptions</c> for request binding, and
/// any explicit <see cref="JsonSerializer"/> call used to write responses.
/// </para>
/// </remarks>
public sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    /// <inheritdoc />
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var value = reader.GetString();

                // An absent or blank id means "let the database assign one", which is the
                // driver's behaviour for a default ObjectId.
                if (string.IsNullOrWhiteSpace(value)) return ObjectId.Empty;

                if (!ObjectId.TryParse(value, out var parsed))
                    throw new JsonException($"'{value}' is not a valid 24-character ObjectId.");

                return parsed;
            }

            case JsonTokenType.Null:
                return ObjectId.Empty;

            // Tolerate the legacy object shape this converter exists to replace, so documents
            // and clients produced before it existed still deserialise rather than throwing.
            case JsonTokenType.StartObject:
            {
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.TryGetProperty("$oid", out var oid)
                    && oid.ValueKind == JsonValueKind.String
                    && ObjectId.TryParse(oid.GetString(), out var fromOid))
                {
                    return fromOid;
                }

                return ObjectId.Empty;
            }

            default:
                throw new JsonException($"Cannot read an ObjectId from token '{reader.TokenType}'.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    /// <inheritdoc />
    /// <remarks>Ids appearing as dictionary keys must serialise to the same hex string.</remarks>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());

    /// <inheritdoc />
    public override ObjectId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return ObjectId.TryParse(value, out var parsed)
            ? parsed
            : throw new JsonException($"'{value}' is not a valid ObjectId property name.");
    }
}

/// <summary>
/// Shared JSON settings for Foundry HTTP payloads.
/// </summary>
public static class FoundryJsonDefaults
{
    /// <summary>
    /// Options carrying every converter Foundry entities need on the wire.
    /// </summary>
    /// <remarks>
    /// Exposed so generated endpoint code serialises responses identically to how the host
    /// deserialises requests. Two independently-configured serializers is how the ObjectId
    /// mismatch went unnoticed.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Builds a fresh options instance with Foundry's converters applied.</summary>
    /// <remarks>
    /// Case-insensitive reads mirror how ASP.NET Core binds request bodies, so a payload the
    /// API would accept also deserialises through these options. <see cref="Apply"/> deliberately
    /// does not set this, because there it would override the host's own configuration.
    /// </remarks>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        Apply(options);
        return options;
    }

    /// <summary>Adds Foundry's converters to an existing options instance.</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Converters.Any(c => c is ObjectIdJsonConverter))
            options.Converters.Add(new ObjectIdJsonConverter());

        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
            options.Converters.Add(new JsonStringEnumConverter());

        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
            .WithAddedModifier(MakeAuditTimestampsReadOnly);
    }

    /// <summary>
    /// Audit timestamps the server owns outright. Emitted to clients, never read from them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately excludes <c>Version</c>. That is the optimistic-concurrency token: the
    /// repository reads it from the incoming entity and filters the update on it, so a client
    /// must send it back for a conflicting write to be detected. Making it read-only would
    /// silently bind it to 0, and every update would filter on a version no document has.
    /// It behaves like an ETag, and round-tripping it is the intended contract.
    /// </para>
    /// <para>
    /// These two are different: the repository overwrites <c>CreatedAtUtc</c> from the stored
    /// document and stamps <c>UpdatedAtUtc</c> with the current time on every write, so anything
    /// a client sends is discarded regardless. Accepting them only invites callers to believe
    /// they can set audit history.
    /// </para>
    /// </remarks>
    private static readonly string[] ServerOwnedTimestamps = ["CreatedAtUtc", "UpdatedAtUtc"];

    /// <summary>
    /// Clears the setter for server-owned audit timestamps on entity types, so they serialize
    /// out but are ignored on the way in.
    /// </summary>
    private static void MakeAuditTimestampsReadOnly(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
        if (!IsEntity(typeInfo.Type)) return;

        foreach (var property in typeInfo.Properties)
        {
            if (ServerOwnedTimestamps.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                property.Set = null;
        }
    }

    private static bool IsEntity(Type type)
        => type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Foundry.Core.Entities.IEntity<>));
}
