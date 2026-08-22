using Foundry.Schema.Compiler;
using Foundry.Testing.Coverage;
using Xunit;

namespace Foundry.Testing.Tests;

/// <summary>
/// What the coverage report claims, and what it refuses to claim.
/// </summary>
/// <remarks>
/// The failure mode worth guarding against is not a wrong row. It is a report that says a
/// declaration is covered when nothing asserts it -- the same vacuous green this command exists to
/// find, one level up. So the tests below care most about the report being unable to overstate
/// itself.
/// </remarks>
public class EnforcementCoverageTests
{
    private static SchemaModel Schema(Entity entity) => new()
    {
        Namespace = "Sales.Domain",
        Entities = [entity]
    };

    private static Entity OwnerScoped(bool graphQl = false) => new()
    {
        Name = "Customer",
        OwnerScoped = true,
        GraphQlEnabled = graphQl,
        ApiEnabledMethods = ["GET", "POST"],
        Properties =
        [
            new Property { Name = "Id", Type = "ObjectId", IsKey = true },
            new Property { Name = "Email", Type = "string", Attributes = ["Required"] },
            new Property { Name = "OwnerId", Type = "string", IsOwnerKey = true }
        ]
    };

    [Fact]
    public void AnOwnerScopedEntityIsReportedCoveredOnRest()
    {
        // The generator emits owner-scoping assertions for this shape, so the report must say so.
        var claims = EnforcementCoverage.Analyse(Schema(OwnerScoped()));

        var rest = Assert.Single(claims, c => c.Declaration == "ownerScoped" && c.Egress == "REST");
        Assert.Equal(CoverageState.Asserted, rest.State);
    }

    [Fact]
    public void OneDeclarationIsTrackedSeparatelyOnEachEgressItReaches()
    {
        // One declaration, two egresses, tracked apart. This is the distinction the whole command
        // exists for: masking was correct on REST and leaking over GraphQL for two releases, and a
        // per-entity verdict would have called that entity covered.
        //
        // Both now assert, since the GraphQL emitter covers owner scoping through the resolver.
        // The value of the test is that the two are still counted separately -- if the GraphQL
        // emission is removed, this fails while the REST claim stays green.
        var claims = EnforcementCoverage.Analyse(Schema(OwnerScoped(graphQl: true)));

        var rest = Assert.Single(claims, c => c.Declaration == "ownerScoped" && c.Egress == "REST");
        var gql = Assert.Single(claims, c => c.Declaration == "ownerScoped" && c.Egress == "GraphQL");

        Assert.Equal(CoverageState.Asserted, rest.State);
        Assert.Equal(CoverageState.Asserted, gql.State);
    }

    [Fact]
    public void CoverageIsReadFromTheGeneratorRatherThanAssumed()
    {
        // The load-bearing property.
        //
        // An entity that declares ownerScoped but marks no owner key gets no assertions from the
        // generator -- it emits a comment saying ownership is unverified instead. A report built
        // from a table of what the generator is believed to emit would still call this covered,
        // because the entity declares everything the table looks at. Reading the generator's real
        // output is what makes that impossible.
        var noOwnerKey = OwnerScoped();
        noOwnerKey = noOwnerKey with
        {
            Properties =
            [
                new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                new Property { Name = "Email", Type = "string", Attributes = ["Required"] },
                new Property { Name = "OwnerId", Type = "string", IsOwnerKey = false }
            ]
        };

        var claims = EnforcementCoverage.Analyse(Schema(noOwnerKey));

        var rest = Assert.Single(claims, c => c.Declaration == "ownerScoped" && c.Egress == "REST");
        Assert.Equal(CoverageState.NotAsserted, rest.State);
    }

    [Fact]
    public void ADeclarationThatWasNeverMadeIsNotReportedAsAGap()
    {
        // A report that invents gaps is ignored as quickly as one that hides them.
        var plain = new Entity
        {
            Name = "Customer",
            ApiEnabledMethods = ["GET"],
            Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
        };

        var claims = EnforcementCoverage.Analyse(Schema(plain));

        Assert.DoesNotContain(claims, c => c.Declaration.Contains("ownerScoped"));
        Assert.DoesNotContain(claims, c => c.Egress == "Real-time");
        Assert.DoesNotContain(claims, c => c.Egress == "Kafka outbox");
    }

    [Fact]
    public void RolesOnOneGrantAreOneClaimRatherThanARowEach()
    {
        // Five roles on a channel are alternatives, not five things to cover. A row each inflates
        // the denominator and buries the gaps that are real -- Resourcify's first report read
        // 6 of 64 for that reason, when the honest figure was 3 of 46.
        var realTime = new Entity
        {
            Name = "Customer",
            RealTime = true,
            RealTimeRoles = ["Admin", "PMO", "TeamLead"],
            ApiEnabledMethods = ["GET"],
            Properties = [new Property { Name = "Id", Type = "ObjectId", IsKey = true }]
        };

        var claims = EnforcementCoverage.Analyse(Schema(realTime));

        var claim = Assert.Single(claims, c => c.Egress == "Real-time");
        Assert.Contains("Admin", claim.Declaration);
        Assert.Contains("TeamLead", claim.Declaration);
    }

    [Fact]
    public void TheReportSaysWhatItDoesNotKnow()
    {
        // "Asserted" means an assertion exists, not that the control works. A reader who takes the
        // first for the second has the same false confidence the command was built to remove.
        var rendered = EnforcementCoverage.Render(EnforcementCoverage.Analyse(Schema(OwnerScoped())));

        Assert.Contains("coverage, not correctness", rendered);
        Assert.Contains("running it", rendered);
    }

    [Fact]
    public void AMaskedPropertyIsTrackedPerEgressItReaches()
    {
        // Masking is applied in the repository, which is why it holds on REST for free and did not
        // on GraphQL: the resolver had nothing materialised to mask. Both are listed, so the one
        // that regressed cannot hide behind the one that did not.
        var masked = new Entity
        {
            Name = "Customer",
            GraphQlEnabled = true,
            ApiEnabledMethods = ["GET"],
            Properties =
            [
                new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                new Property { Name = "Email", Type = "string", Attributes = ["MaskEmail"] }
            ]
        };

        var claims = EnforcementCoverage.Analyse(Schema(masked));

        Assert.Contains(claims, c => c.Declaration.Contains("Email") && c.Egress == "REST");
        Assert.Contains(claims, c => c.Declaration.Contains("Email") && c.Egress == "GraphQL");
    }
}
