// This suite is only meaningful when the host machine's local timezone is NOT UTC.
// The defect it pins is a host-UTC-offset shift: the driver's default DateTimeSerializer
// calls DateTime.ToUniversalTime() on write, which treats DateTimeKind.Unspecified as local
// time. Under TZ=UTC the offset is zero, so test (a) would pass whether or not the fix is
// present and the suite has no teeth. Run under a non-UTC TZ (e.g. TZ=Asia/Bangkok) to
// exercise the real bug. This is the same reasoning used for the TZ pin in
// vitest.config.ts and TimezoneDatePersistenceTests.cs in the sibling Resourcify repo.

using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Foundry.Mongo.Infrastructure.Conventions;

namespace Foundry.Mongo.Tests;

public class DateTimeSerializationTests
{
    public DateTimeSerializationTests()
    {
        // Idempotent and thread-safe; the assembly-level module initializer already calls
        // this once, but calling it here makes the dependency explicit and keeps this file
        // self-documenting if it is ever run in isolation.
        MongoDbConventions.Register();
    }

    private sealed class DateTimeHolder
    {
        public DateTime Value { get; set; }
    }

    [Fact]
    public void UnspecifiedDateTime_SerializesToTheSameUtcInstant_NotShiftedByHostOffset()
    {
        var holder = new DateTimeHolder
        {
            Value = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified)
        };

        var bsonDoc = holder.ToBsonDocument();
        var actualBsonDateTime = bsonDoc["value"].AsBsonDateTime;
        var actualMillis = actualBsonDateTime.MillisecondsSinceEpoch;
        var actualUtc = actualBsonDateTime.ToUniversalTime();

        var expectedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var expectedMillis = BsonUtils.ToMillisecondsSinceEpoch(expectedUtc);

        Assert.True(actualMillis == expectedMillis,
            $"Value was persisted as {actualUtc:yyyy-MM-ddTHH:mm:ss}Z ({actualMillis} ms since "
            + $"epoch), but the calendar date the writer was asked to store is 2026-01-05, "
            + $"which must be stored as {expectedUtc:yyyy-MM-ddTHH:mm:ss}Z "
            + $"({expectedMillis} ms since epoch). The {(expectedUtc - actualUtc).TotalHours:0.##}-hour "
            + $"gap is this host's UTC offset applied to an Unspecified DateTime that never meant a "
            + $"local time in the first place.");
    }

    [Fact]
    public void UtcDateTime_SerializesToTheSameInstant_AsTheUnspecifiedCase()
    {
        // This guards the already-correct path that the framework's existing Utc-only fixtures
        // rely on. If someone were to "fix" the Unspecified case in a way that inadvertently
        // changes how a genuinely Utc-tagged DateTime is written, this test will catch it.
        // It must never regress.
        var holder = new DateTimeHolder
        {
            Value = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
        };

        var bsonDoc = holder.ToBsonDocument();
        var actualMillis = bsonDoc["value"].AsBsonDateTime.MillisecondsSinceEpoch;

        var expectedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var expectedMillis = BsonUtils.ToMillisecondsSinceEpoch(expectedUtc);

        Assert.Equal(expectedMillis, actualMillis);
    }

    [Fact]
    public void UnspecifiedDateTime_RoundTrips_ThroughDeserializationAsUtc()
    {
        var holder = new DateTimeHolder
        {
            Value = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified)
        };

        var bsonDoc = holder.ToBsonDocument();
        var deserialized = BsonSerializer.Deserialize<DateTimeHolder>(bsonDoc);

        var expectedUtc = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expectedUtc, deserialized.Value);
        Assert.Equal(DateTimeKind.Utc, deserialized.Value.Kind);
    }
}
