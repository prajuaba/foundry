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

    /// <summary>Similar to Claim but properties declare Roles that entitle access.</summary>
    public record ClaimWithRoles : BaseEntity<ObjectId>, IVersionable
    {
        [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Partial,
            PreserveCount = 4, Category = "policy", Roles = ["PolicyReader"])]
        public string PolicyNumber { get; set; } = string.Empty;

        [SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Partial,
            PreserveCount = 4, Category = "financial", Roles = ["FinanceViewer"])]
        public string CardNumber { get; set; } = string.Empty;

        public string Reference { get; set; } = string.Empty;
    }

    private sealed class FixedUser(string[] scopes, string[] roles) : ICurrentUserContext
    {
        public FixedUser(params string[] scopesAndRoles)
            : this(
                Array.FindAll(scopesAndRoles, s => s.StartsWith("view:")),
                Array.FindAll(scopesAndRoles, r => !r.StartsWith("view:")))
        {
        }

        public string OperatorId => "tester";
        public string? OperatorName => "tester";

        public ClaimsPrincipal? User
        {
            get
            {
                var claims = new List<System.Security.Claims.Claim> { new("sub", "tester") };
                claims.AddRange(scopes.Select(s => new System.Security.Claims.Claim("scope", s)));
                claims.AddRange(roles.Select(r => new System.Security.Claims.Claim("role", r)));
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

    private Repository<ClaimWithRoles> ClaimsWithRolesFor(params string[] scopesAndRoles)
        => new(_db, userContext: new FixedUser(scopesAndRoles));

    private async Task<ObjectId> SeedClaimWithRolesAsync()
    {
        var claim = new ClaimWithRoles
        {
            Id = ObjectId.GenerateNewId(),
            PolicyNumber = "POL-000012345678",
            CardNumber = "4111111111111234",
            Reference = "CLM-1"
        };

        await ClaimsWithRolesFor("view:policy", "view:financial").InsertAsync(claim);
        return claim.Id;
    }

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

    // ── Writing back a masked read is now preserved, not persisted ───────────

    [Fact]
    public async Task WritingBackAMaskedEntityPreservesIt()
    {
        // The hazard masking creates: read-modify-write is an ordinary pattern, and persisting the
        // masked clone would replace a real email with "j***e@example.com" silently and irreversibly.
        // Now instead of refusing and throwing, we preserve: a caller who cannot read the field
        // cannot change it either.
        var id = await SeedAsync();
        var repository = PeopleFor(mayViewPii: false);

        var masked = await repository.GetByIdAsync(id);

        // `with` produces a new instance, which is exactly how a caller modifies a record -- so the
        // guard compares against the stored value rather than tracking the objects handed out.
        // With the fix, this succeeds instead of throwing, and the masked field is preserved.
        await repository.UpdateAsync(masked! with { FullName = "Johnny Doe" });

        var stored = await PeopleFor(mayViewPii: true).GetByIdAsync(id);
        Assert.Equal("john.doe@example.com", stored!.Email); // Preserved, not corrupted
        Assert.Equal("Johnny Doe", stored.FullName); // Other field was updated
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
    public async Task AFreshlyConstructedEntityPreservesMaskedFields()
    {
        // A caller without view:pii constructs a fresh entity with new values for masked fields.
        // With the fix, masked fields the caller cannot read are preserved regardless of what
        // was supplied -- even in a freshly constructed entity from a request body.
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
        Assert.Equal("john.doe@example.com", reread!.Email); // Preserved, not overwritten
        Assert.Equal("4111111111111234", reread.CardNumber); // Preserved, not overwritten
        Assert.Equal("Rebuilt", reread.FullName); // Non-masked field was updated
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

    // ── Role-based masking mirrors scope-based behavior ──────────────────────

    [Fact]
    public async Task ARoleUnmasksItsOwnCategoryAndNoOther()
    {
        var id = await SeedClaimWithRolesAsync();

        var claim = await ClaimsWithRolesFor("PolicyReader").GetByIdAsync(id);

        Assert.Equal("POL-000012345678", claim!.PolicyNumber);
        Assert.DoesNotContain("4111", claim.CardNumber);
        Assert.EndsWith("1234", claim.CardNumber);
    }

    [Fact]
    public async Task NotHoldingTheRoleKeepsItMasked()
    {
        var id = await SeedClaimWithRolesAsync();

        var claim = await ClaimsWithRolesFor().GetByIdAsync(id);

        Assert.DoesNotContain("000012345678", claim!.PolicyNumber);
        Assert.DoesNotContain("4111", claim.CardNumber);
    }

    [Fact]
    public async Task HoldingARoleForOneCategoryAndScopeForAnotherUnmasksBothIndependently()
    {
        var id = await SeedClaimWithRolesAsync();

        var claim = await ClaimsWithRolesFor("PolicyReader", "view:financial").GetByIdAsync(id);

        Assert.Equal("POL-000012345678", claim!.PolicyNumber);
        Assert.Equal("4111111111111234", claim.CardNumber);
    }

    [Fact]
    public async Task HoldingNeitherRoleNorScopeMasksBoth()
    {
        var id = await SeedClaimWithRolesAsync();

        var claim = await ClaimsWithRolesFor().GetByIdAsync(id);

        Assert.DoesNotContain("000012345678", claim!.PolicyNumber);
        Assert.DoesNotContain("4111", claim.CardNumber);
    }

    // ── PreserveMaskedFieldsCallerCannotRead: prevent data loss when unreadable fields are updated ─

    [Fact]
    public async Task CallerWithoutScopeSubmittingMaskVerbatim_PreservesStoredValue()
    {
        // Caller without scope reads entity (gets masked values), modifies other field, writes back.
        // The mask echoed back should be caught by the old guard, but now the guard also preserves
        // by checking per-category entitlement.
        var id = await SeedClaimAsync();
        var unentitledRepository = ClaimsFor();

        var masked = await unentitledRepository.GetByIdAsync(id);
        var originalPolicy = "POL-000012345678";
        var originalCard = "4111111111111234";

        // Echo back the masked values as-is, change something else
        await unentitledRepository.UpdateAsync(masked! with { Reference = "UPDATED" });

        var stored = await ClaimsFor("view:policy", "view:financial").GetByIdAsync(id);
        Assert.Equal(originalPolicy, stored!.PolicyNumber);
        Assert.Equal(originalCard, stored.CardNumber);
        Assert.Equal("UPDATED", stored.Reference); // Only this should change
    }

    [Fact]
    public async Task CallerWithoutScopeOmittingMaskedField_PreservesStoredValue()
    {
        // The reported defect: a well-behaved client that omits the field to avoid echoing
        // the mask ends up wiping it. Now it should be preserved. We simulate omission by
        // sending default values (empty strings) for those fields.
        var id = await SeedClaimAsync();
        var unentitledRepository = ClaimsFor();

        var masked = await unentitledRepository.GetByIdAsync(id);
        var originalPolicy = "POL-000012345678";
        var originalCard = "4111111111111234";

        // Build a fresh entity with empty string fields (simulating an omitted field in a PUT)
        await unentitledRepository.UpdateAsync(new Claim
        {
            Id = id,
            PolicyNumber = string.Empty,
            CardNumber = string.Empty,
            Reference = "UPDATED",
            Version = masked!.Version
        });

        var stored = await ClaimsFor("view:policy", "view:financial").GetByIdAsync(id);
        Assert.Equal(originalPolicy, stored!.PolicyNumber);
        Assert.Equal(originalCard, stored.CardNumber);
    }

    [Fact]
    public async Task CallerWithoutScopeSubmittingEmptyString_PreservesStoredValue()
    {
        // Another way to wipe the field: send empty string instead of null/mask/actual value
        var id = await SeedClaimAsync();
        var unentitledRepository = ClaimsFor();

        var masked = await unentitledRepository.GetByIdAsync(id);
        var originalPolicy = "POL-000012345678";
        var originalCard = "4111111111111234";

        await unentitledRepository.UpdateAsync(masked! with { PolicyNumber = "", CardNumber = "" });

        var stored = await ClaimsFor("view:policy", "view:financial").GetByIdAsync(id);
        Assert.Equal(originalPolicy, stored!.PolicyNumber);
        Assert.Equal(originalCard, stored.CardNumber);
    }

    [Fact]
    public async Task CallerWithoutScopeSubmittingNewValue_PreservesStoredValue()
    {
        // Caller without scope reads an entity (gets masked values). Because the real values
        // are stored and shown as masked, the caller has no way to know a genuinely new value.
        // Permitting them to overwrite would let them corrupt data in the name of updating it.
        var id = await SeedClaimAsync();
        var unentitledRepository = ClaimsFor();

        var masked = await unentitledRepository.GetByIdAsync(id);
        var originalPolicy = "POL-000012345678";
        var originalCard = "4111111111111234";

        // Attempt to replace with new values
        await unentitledRepository.UpdateAsync(masked! with
        {
            PolicyNumber = "POL-ATTACKER123456",
            CardNumber = "5555555555555555"
        });

        var stored = await ClaimsFor("view:policy", "view:financial").GetByIdAsync(id);
        Assert.Equal(originalPolicy, stored!.PolicyNumber);
        Assert.Equal(originalCard, stored.CardNumber);
    }

    [Fact]
    public async Task CallerWithScopeSubmittingNewValue_UpdatesStoredValue()
    {
        // Privileged caller may update. This is the normal case.
        var id = await SeedClaimAsync();
        var entitledRepository = ClaimsFor("view:policy", "view:financial");

        var unmasked = await entitledRepository.GetByIdAsync(id);
        await entitledRepository.UpdateAsync(unmasked! with
        {
            PolicyNumber = "POL-NEWVALUE123456",
            CardNumber = "6666666666666666"
        });

        var stored = await entitledRepository.GetByIdAsync(id);
        Assert.Equal("POL-NEWVALUE123456", stored!.PolicyNumber);
        Assert.Equal("6666666666666666", stored.CardNumber);
    }

    [Fact]
    public async Task CallerWithScopeSubmittingEmptyString_ClearsStoredValue()
    {
        // Privileged caller retains full control, including clearing the field.
        var id = await SeedClaimAsync();
        var entitledRepository = ClaimsFor("view:policy", "view:financial");

        var unmasked = await entitledRepository.GetByIdAsync(id);
        await entitledRepository.UpdateAsync(unmasked! with
        {
            PolicyNumber = "",
            CardNumber = ""
        });

        var stored = await entitledRepository.GetByIdAsync(id);
        Assert.Equal("", stored!.PolicyNumber);
        Assert.Equal("", stored.CardNumber);
    }

    [Fact]
    public async Task CallerWithScopeCanUpdateWithoutRestriction()
    {
        // Privileged caller reading with full scope gets actual values (never masks), so they can
        // always write whatever they want. The old guard still applies if they somehow send back
        // a mask value (though in practice they never see masks), but that's a rare edge case.
        // Normal case: privileged caller reads, modifies, and writes back. All good.
        var id = await SeedClaimAsync();
        var entitledRepository = ClaimsFor("view:policy", "view:financial");

        var unmasked = await entitledRepository.GetByIdAsync(id);

        // Privileged caller can freely update
        await entitledRepository.UpdateAsync(unmasked! with { Reference = "changed" });

        var stored = await entitledRepository.GetByIdAsync(id);
        Assert.Equal("changed", stored!.Reference);
        Assert.Equal("POL-000012345678", stored.PolicyNumber); // Unchanged
    }

    [Fact]
    public async Task CallerWithMixedCategoryAccess_UpdatesReadablePreservesUnreadable()
    {
        // The most important case: one caller, two categories, different access to each.
        // A claims handler who may read PolicyNumber but not CardNumber attempts an update.
        // PolicyNumber should be updated, CardNumber should be preserved.
        var id = await SeedClaimAsync();
        var mixedRepository = ClaimsFor("view:policy"); // Can read policy, not financial

        var partial = await mixedRepository.GetByIdAsync(id);
        var originalCard = "4111111111111234"; // Masked when read; cannot be read in full

        // Attempt to update both (handler may have logic that touches both fields)
        await mixedRepository.UpdateAsync(partial! with
        {
            PolicyNumber = "POL-HANDLER-UPDATE",
            CardNumber = "9999999999999999"
        });

        var stored = await ClaimsFor("view:policy", "view:financial").GetByIdAsync(id);
        Assert.Equal("POL-HANDLER-UPDATE", stored!.PolicyNumber); // Handler could read it, so update takes effect
        Assert.Equal(originalCard, stored.CardNumber); // Handler could not read it, so preservation applies
    }

    /// <summary>
    /// A privileged caller who echoes back a masked field must still be refused, even though
    /// unprivileged callers are now allowed (preservation instead of throwing). The unprivileged
    /// path preserves because the caller cannot read the field; a privileged caller echoing back
    /// a mask indicates a client bug and must not silently persist the masked form to storage.
    /// </summary>
    [Fact]
    public async Task CallerWithScopeWritingBackTheMaskIsStillRefused()
    {
        var id = await SeedClaimAsync();

        // Capture the masked representation by reading without scopes
        var masked = await ClaimsFor().GetByIdAsync(id);
        var maskedPolicyNumber = masked!.PolicyNumber;

        // Read with full scopes to get the actual value and correct Version
        var entitledRepository = ClaimsFor("view:policy", "view:financial");
        var unmasked = await entitledRepository.GetByIdAsync(id);

        // Privileged caller attempts to update using the masked value (client bug scenario)
        var updateAttempt = unmasked! with { PolicyNumber = maskedPolicyNumber };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => entitledRepository.UpdateAsync(updateAttempt));

        // Verify the error message identifies the field and explains the issue
        Assert.Contains("PolicyNumber", ex.Message);
        Assert.Contains("masked form", ex.Message);

        // Verify the stored value was not changed by the failed update attempt
        var stored = await entitledRepository.GetByIdAsync(id);
        Assert.Equal("POL-000012345678", stored!.PolicyNumber);
    }
}