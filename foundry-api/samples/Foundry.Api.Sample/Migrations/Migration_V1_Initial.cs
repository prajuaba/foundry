using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace Foundry.Api.Sample.Migrations;

public class Migration_V1_Initial : DatabaseMigration
{
    public override int Version => 1;

    public override Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        // Initial setup database schema (indexes, seed data, etc.)
        // This is version 1, does not perform changes to existing collections.
        return Task.CompletedTask;
    }
}
