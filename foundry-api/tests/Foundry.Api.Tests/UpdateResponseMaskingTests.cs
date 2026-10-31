using System.Threading;
using System.Threading.Tasks;
using Foundry.Api.MediatR;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using NSubstitute;
using Xunit;

namespace Foundry.Api.Tests;

/// <summary>
/// The generic PUT answers with the caller's masked view of the row it wrote.
/// </summary>
/// <remarks>
/// <c>UpdateAsync</c> restores a masked field the caller may not read into the caller's own
/// instance, so the write keeps the stored value; the handler then returned that instance, real
/// value included, to a caller the schema says may not see it.
/// </remarks>
public class UpdateResponseMaskingTests
{
    public class Person : IEntity<ObjectId>
    {
        public ObjectId Id { get; init; }
        public int Version { get; set; }
        public System.DateTime CreatedAtUtc { get; set; }
        public System.DateTime UpdatedAtUtc { get; set; }
        public decimal Salary { get; set; }
    }

    [Fact]
    public async Task ThePutResponseIsMaskedForTheCaller()
    {
        var repository = Substitute.For<IRepository<Person>>();
        var written = new Person { Salary = 0m };
        repository.UpdateAsync(written, Arg.Any<MongoDB.Driver.IClientSessionHandle?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { written.Salary = 9999m; return Task.CompletedTask; });
        repository.MaskSensitiveFields(Arg.Any<Person>()).Returns(c => new Person { Id = c.Arg<Person>().Id, Salary = 0m });

        var response = await new UpdateCommandHandler<Person>(repository)
            .Handle(new UpdateCommand<Person>(written), CancellationToken.None);

        Assert.Equal(0m, response.Salary);
        repository.Received(1).MaskSensitiveFields(written);
    }
}
