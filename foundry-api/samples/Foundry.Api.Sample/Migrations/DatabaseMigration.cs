using System.Threading.Tasks;
using MongoDB.Driver;

namespace Foundry.Api.Sample.Migrations;

public abstract class DatabaseMigration
{
    public abstract int Version { get; }
    public abstract Task UpAsync(IMongoDatabase database, System.Threading.CancellationToken ct);
}
