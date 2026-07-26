using MongoDB.Driver;

namespace Foundry.Mongo.UnitOfWork;

/// <summary>
/// Concrete factory for creating UnitOfWork scopes using an injected IMongoClient.
/// </summary>
public sealed class UnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly IMongoClient _client;

    public UnitOfWorkFactory(IMongoClient client)
    {
        _client = client;
    }

    public IUnitOfWork Create() => new UnitOfWork(_client);
}
