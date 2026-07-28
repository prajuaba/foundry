using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Foundry.Core.Entities;
using Foundry.Core.Paging;
using Foundry.Core.User;
using Foundry.Mongo.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Foundry.Mongo.Tests;

/// <summary>
/// Field-level restriction: what a caller sees inside a row they are allowed to read.
/// </summary>
/// <remarks>
/// <para>
/// The masking machinery was written, unit-tested in isolation, and called by nothing. An entity
/// declaring <c>[SensitiveData(Protection = Mask)]</c> returned the value in the clear on every
/// transport — REST, GraphQL, the SDKs — because no read path applied it. That is this codebase's
/// signature shape: a protection that exists, reads as careful, and performs nothing.
/// </para>
/// <para>
/// It is applied in the repository rather than per endpoint so one rule covers every transport.
/// Masking per transport is how the route prefix came to be wrong in six separate places.
/// </para>
/// </remarks>
public class MaskingTests : IDisposable
{
    private readonly string _dbName;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;

    public record Person : BaseEntity<ObjectId>, IVersionable
    {
        [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]
        public string Email { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Partial, PreserveCount = 4)]
        public string CardNumber { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;
    }

    /// <summary>Two categories, so entitlement to one can be told apart from entitlement to both.</summary>
    public record Claim : BaseEntity<ObjectId>, IVersionable
    {
        [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Partial,
            PreserveCount = 4, Category = "policy")]
        public string PolicyNumber { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Partial,
            PreserveCount = 4, Category = "financial")]
        public string CardNumber { get; set; } = string.Empty;

        public string Reference { get; set; } = string.Empty;
    }

    /// <summary>Declares nothing sensitive, so it must be handed back untouched.</summary>
    public record Plain : BaseEntity<ObjectId>, IVersionable
    {
        public string Note { get; set; } = string.Empty;
    }

    private sealed class FixedUser(params string[] scopes) : ICurrentUserContext
    {
        public string OperatorId => "tester";
        public string? OperatorName => "tester";

        public ClaimsPrincipal? User
        {
            get
            {
                var claims = new List<System.Security.Claims.Claim> { new("sub", "tester") };
                claims.AddRange(scopes.Select(s => new System.Security.Claims.Claim("scope", s)));
                return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "sub", "role"));
            }
        }
    }

    public MaskingTests()
    {
        Foundry.Mongo.Infrastructure.Conventions.MongoDbConventions.Register();
        _dbName = $"FoundryMongo_Masking_{Guid.NewGuid():N}";
        _client = new MongoClient("mongodb://localhost:27017");
        _db = _client.GetDatabase(_dbName);
    }

    public void Dispose()
    {
        try { _client.DropDatabase(_dbName); } catch { /* cleanup is best effort */ }
    }

    private Repository<Person> PeopleFor(bool mayViewPii)
        => new(_db, userContext: mayViewPii ? new FixedUser("view:pii") : new FixedUser());

    private Repository<Plain> PlainFor(bool mayViewPii)
        => new(_db, userContext: mayViewPii ? new FixedUser("view:pii") : new FixedUser());

    private Repository<Claim> ClaimsFor(params string[] scopes)
        => new(_db, userContext: new FixedUser(scopes));

    private async Task<ObjectId> SeedClaimAsync()
    {
        var claim = new Claim
        {
            Id = ObjectId.GenerateNewId(),
            PolicyNumber = "POL-000012345678",
            CardNumber = "4111111111111234",
            Reference = "CLM-1"
        };

        await ClaimsFor("view:policy", "view:financial").InsertAsync(claim);
        return claim.Id;
    }

    private async Task<ObjectId> SeedAsync(string email = "john.doe@example.com", string card = "4111111111111234")
    {
        var person = new Person { Id = ObjectId.GenerateNewId(), Email = email, CardNumber = card, FullName = "John Doe" };
        await PeopleFor(mayViewPii: true).InsertAsync(person);
        return person.Id;
    }

    // ── The value is stored in full and shown masked ────────────────────────

    [Fact]
    public async Task AReadByIdIsMasked()
    {
        var id = await SeedAsync();

        var person = await PeopleFor(mayViewPii: false).GetByIdAsync(id);

        Assert.NotNull(person);
        Assert.DoesNotContain("john.doe", person!.Email);
        Assert.EndsWith("@example.com", person.Email);
        Assert.EndsWith("1234", person.CardNumber);
        Assert.DoesNotContain("4111", person.CardNumber);
    }

    [Fact]
    public async Task AListIsMasked()
    {
        // The by-id path and the list path have diverged before -- the tenant filter was applied by
        // one and not the other for as long as it existed. Masking one and not the other would be
        // worse than masking neither, because the protection would read as present.
        await SeedAsync();

        var people = await PeopleFor(mayViewPii: false).FindManyAsync();

        Assert.Single(people);
        Assert.DoesNotContain("john.doe", people[0].Email);
    }

    [Fact]
    public async Task APagedReadIsMasked()
    {
        await SeedAsync();

        var page = await PeopleFor(mayViewPii: false).GetPagedAsync(new PagedRequest { PageNumber = 1, PageSize = 10 });

        Assert.Single(page.Items);
        Assert.DoesNotContain("john.doe", page.Items[0].Email);
    }

    [Fact]
    public async Task ASearchIsMasked()
    {
        await SeedAsync();

        var found = await PeopleFor(mayViewPii: false).FindByCriteriaAsync(
            [new Foundry.Core.Search.SearchCriterion { Field = "FullName", Operator = Foundry.Core.Search.SearchOperator.Equals, Value = "John Doe" }]);

        Assert.Single(found);
        Assert.DoesNotContain("john.doe", found[0].Email);
    }

    [Fact]
    public async Task TheStoredValueIsNotChangedByMasking()
    {
        // Masking is a projection, not an edit. If the row itself were altered the protection would
        // be indistinguishable from data loss.
        var id = await SeedAsync();

        await PeopleFor(mayViewPii: false).GetByIdAsync(id);

        var raw = await PeopleFor(mayViewPii: true).GetByIdAsync(id);
        Assert.Equal("john.doe@example.com", raw!.Email);
    }

    // ── The entitled caller sees it in full ─────────────────────────────────

    [Fact]
    public async Task ACallerHoldingTheScopeSeesTheValue()
    {
        var id = await SeedAsync();

        var person = await PeopleFor(mayViewPii: true).GetByIdAsync(id);

        Assert.Equal("john.doe@example.com", person!.Email);
        Assert.Equal("4111111111111234", person.CardNumber);
    }

    [Fact]
    public async Task AnEntityDeclaringNothingSensitiveIsUntouched()
    {
        var plain = new Plain { Id = ObjectId.GenerateNewId(), Note = "nothing to hide" };
        await PlainFor(mayViewPii: true).InsertAsync(plain);

        var read = await PlainFor(mayViewPii: false).GetByIdAsync(plain.Id);

        Assert.Equal("nothing to hide", read!.Note);
    }

    // ── Writing back a masked read is refused, not persisted ────────────────

    [Fact]
    public async Task WritingBackAMaskedEntityIsRefused()
    {
        // The hazard masking creates, and the reason it is worth guarding rather than documenting:
        // read-modify-write is an ordinary pattern, and persisting the masked clone would replace a
        // real email with "j***e@example.com" silently and irreversibly. A change made to protect
        // data would have destroyed it.
        var id = await SeedAsync();
        var repository = PeopleFor(mayViewPii: false);

        var masked = await repository.GetByIdAsync(id);

        // `with` produces a new instance, which is exactly how a caller modifies a record -- so the
        // guard compares against the stored value rather than tracking the objects handed out.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpdateAsync(masked! with { FullName = "Johnny Doe" }));

        Assert.Contains("masked form", error.Message);
        Assert.Contains("view:pii", error.Message);

        var stored = await PeopleFor(mayViewPii: true).GetByIdAsync(id);
        Assert.Equal("john.doe@example.com", stored!.Email);
    }

    [Fact]
    public async Task AnEntityReadWithTheScopeCanBeWrittenBack()
    {
        // The other half: the guard must not make the ordinary path impossible.
        var id = await SeedAsync();
        var repository = PeopleFor(mayViewPii: true);

        var person = await repository.GetByIdAsync(id);
        await repository.UpdateAsync(person! with { FullName = "Johnny Doe" });

        var reread = await repository.GetByIdAsync(id);
        Assert.Equal("Johnny Doe", reread!.FullName);
        Assert.Equal("john.doe@example.com", reread.Email);
    }

    [Fact]
    public async Task AFreshlyConstructedEntityIsNotRefused()
    {
        // The guard fires only on the mask of the value currently stored, so an entity built from a
        // request body -- which is what a generated PUT binds -- is unaffected.
        var id = await SeedAsync();

        await PeopleFor(mayViewPii: false).UpdateAsync(new Person
        {
            Id = id,
            Email = "new.address@example.com",
            CardNumber = "4111111111119999",
            FullName = "Rebuilt",
            Version = 1
        });

        var reread = await PeopleFor(mayViewPii: true).GetByIdAsync(id);
        Assert.Equal("new.address@example.com", reread!.Email);
    }

    // ── Masking is a policy, not one switch ─────────────────────────────────
    //
    // `view:pii` unmasked every masked property on every entity, so "a claims handler may see a
    // policy number but not a card number" could not be expressed: letting someone read one field
    // meant letting them read all of them. A property names the category that unmasks it, and a
    // property naming none is `pii` -- so view:pii still means exactly what it meant.

    [Fact]
    public async Task AScopeUnmasksItsOwnCategoryAndNoOther()
    {
        // The case the single switch could not express.
        var id = await SeedClaimAsync();

        var claim = await ClaimsFor("view:policy").GetByIdAsync(id);

        Assert.Equal("POL-000012345678", claim!.PolicyNumber);
        Assert.DoesNotContain("4111", claim.CardNumber);
        Assert.EndsWith("1234", claim.CardNumber);
    }

    [Fact]
    public async Task TheOtherScopeUnmasksTheOtherCategory()
    {
        var id = await SeedClaimAsync();

        var claim = await ClaimsFor("view:financial").GetByIdAsync(id);

        Assert.Equal("4111111111111234", claim!.CardNumber);
        Assert.DoesNotContain("000012345678", claim.PolicyNumber);
    }

    [Fact]
    public async Task HoldingBothScopesUnmasksBoth()
    {
        var id = await SeedClaimAsync();

        var claim = await ClaimsFor("view:policy", "view:financial").GetByIdAsync(id);

        Assert.Equal("POL-000012345678", claim!.PolicyNumber);
        Assert.Equal("4111111111111234", claim.CardNumber);
    }

    [Fact]
    public async Task HoldingNeitherMasksBoth()
    {
        var id = await SeedClaimAsync();

        var claim = await ClaimsFor().GetByIdAsync(id);

        Assert.DoesNotContain("000012345678", claim!.PolicyNumber);
        Assert.DoesNotContain("4111", claim.CardNumber);
    }

    [Fact]
    public async Task ViewPiiDoesNotUnmaskANamedCategory()
    {
        // The direction that matters. If view:pii still unmasked everything, naming a category would
        // be decoration -- the switch would remain a switch and the finer grant would mean nothing.
        var id = await SeedClaimAsync();

        var claim = await ClaimsFor("view:pii").GetByIdAsync(id);

        Assert.DoesNotContain("000012345678", claim!.PolicyNumber);
        Assert.DoesNotContain("4111", claim.CardNumber);
    }

    [Fact]
    public async Task APropertyNamingNoCategoryStillAnswersToViewPii()
    {
        // Back-compatibility, asserted rather than assumed: every existing declaration names no
        // category, and view:pii has to keep meaning what it meant for those.
        var id = await SeedAsync();

        var person = await PeopleFor(mayViewPii: true).GetByIdAsync(id);

        Assert.Equal("john.doe@example.com", person!.Email);
    }

    [Fact]
    public async Task PerCategoryMaskingAppliesToListsToo()
    {
        await SeedClaimAsync();

        var claims = await ClaimsFor("view:policy").FindManyAsync();

        Assert.Single(claims);
        Assert.Equal("POL-000012345678", claims[0].PolicyNumber);
        Assert.DoesNotContain("4111", claims[0].CardNumber);
    }
}