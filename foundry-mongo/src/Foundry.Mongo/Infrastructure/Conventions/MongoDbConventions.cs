using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace Foundry.Mongo.Infrastructure.Conventions;

/// <summary>
/// Configures global MongoDB conventions for serialization, naming casing, and Guid representations.
/// </summary>
public static class MongoDbConventions
{
    private static bool _registered;
    private static readonly object RegistryLock = new();

    /// <summary>
    /// Registers camelCase property names, Enum-to-String storage, ignores extra elements on read, and enforces Standard GUID representation.
    /// Thread-safe and safe to call multiple times (idempotent).
    /// </summary>
    public static void Register()
    {
        if (_registered) return;

        lock (RegistryLock)
        {
            if (_registered) return;

            var conventionPack = new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new EnumRepresentationConvention(BsonType.String),
                new IgnoreExtraElementsConvention(true),
                new UnspecifiedAsUtcDateTimeConvention()
            };

            ConventionRegistry.Register(
                "FoundryMongoConventions",
                conventionPack,
                t => true);

            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            _registered = true;
        }
    }

    /// <summary>
    /// Applies <see cref="UnspecifiedAsUtcDateTimeSerializer"/> to every <see cref="DateTime"/> and
    /// nullable <see cref="DateTime"/> member as its class map is built.
    /// </summary>
    /// <remarks>
    /// Deliberately a member-map convention rather than a global
    /// <c>BsonSerializer.RegisterSerializer(typeof(DateTime), ...)</c>. The registry creates and
    /// caches a serializer for a type the first time one is asked for, and then refuses to have it
    /// replaced -- "There is already a serializer registered for type DateTime". A global
    /// registration therefore only succeeds if it happens before anything in the process has
    /// serialized a date, which is not something a library can rely on: a host that builds several
    /// providers, or a test assembly running classes in parallel, will sometimes lose the race and
    /// throw out of DI setup. A convention runs while each class map is built and has no such
    /// ordering constraint.
    /// </remarks>
    private sealed class UnspecifiedAsUtcDateTimeConvention : ConventionBase, IMemberMapConvention
    {
        public void Apply(BsonMemberMap memberMap)
        {
            if (memberMap.MemberType == typeof(DateTime))
            {
                memberMap.SetSerializer(new UnspecifiedAsUtcDateTimeSerializer());
            }
            else if (memberMap.MemberType == typeof(DateTime?))
            {
                memberMap.SetSerializer(new NullableSerializer<DateTime>(new UnspecifiedAsUtcDateTimeSerializer()));
            }
        }
    }

    /// <summary>
    /// Stores a <see cref="DateTimeKind.Unspecified"/> <see cref="DateTime"/> as the instant it
    /// already names, rather than converting it from the host's local time.
    /// </summary>
    /// <remarks>
    /// The driver's default serializer calls <c>DateTime.ToUniversalTime()</c>, which treats an
    /// Unspecified value as local. Almost every DateTime a generated application produces is
    /// Unspecified -- model binding yields it, and so does <c>DateOnly.ToDateTime()</c> -- so on any
    /// host that is not UTC every date was persisted shifted by the host's offset: a value meaning
    /// the calendar date 2026-01-05 landed as 2026-01-04T17:00:00Z at UTC+07. Reads already returned
    /// Utc, so the two sides disagreed, and a rule comparing an unconverted request value against a
    /// converted stored one rejected windows sitting exactly on a boundary while reporting both
    /// sides formatted identically.
    /// <para>
    /// Passing <see cref="DateTimeKind.Utc"/> to the stock <see cref="DateTimeSerializer"/> is not
    /// sufficient: that argument governs the Kind returned on read, not the conversion applied on
    /// write.
    /// </para>
    /// <para>
    /// This was invisible to the framework's own suites because every DateTime fixture in them is
    /// constructed with <see cref="DateTimeKind.Utc"/> -- the one kind the default never touched.
    /// </para>
    /// </remarks>
    private sealed class UnspecifiedAsUtcDateTimeSerializer : StructSerializerBase<DateTime>
    {
        // DateTimeSerializer is sealed in the 3.x driver, so this wraps it rather than deriving.
        private static readonly DateTimeSerializer Inner = new(DateTimeKind.Utc);

        public override DateTime Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
            => Inner.Deserialize(context, args);

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTime value)
        {
            if (value.Kind == DateTimeKind.Unspecified)
            {
                value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }

            Inner.Serialize(context, args, value);
        }
    }
}
