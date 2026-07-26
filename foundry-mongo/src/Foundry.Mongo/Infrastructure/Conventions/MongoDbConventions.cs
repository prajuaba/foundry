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
                new IgnoreExtraElementsConvention(true)
            };

            ConventionRegistry.Register(
                "FoundryMongoConventions",
                conventionPack,
                t => true);

            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            _registered = true;
        }
    }
}
