using System.Text;
using Foundry.Schema.Compiler;
using Foundry.Testing.Generators;

namespace Foundry.Testing.Coverage;

/// <summary>Whether a declaration is actually asserted on an egress it reaches.</summary>
public enum CoverageState
{
    /// <summary>An assertion exists that would fail if the control were removed.</summary>
    Asserted,

    /// <summary>The declaration reaches this egress and nothing asserts it there.</summary>
    NotAsserted
}

/// <summary>One security declaration, on one egress it can be observed through.</summary>
public sealed record EnforcementClaim(
    string Entity,
    string Declaration,
    string Egress,
    CoverageState State,
    string Detail);

/// <summary>
/// Reports which security declarations in a schema are actually asserted, and on which egresses.
/// </summary>
/// <remarks>
/// <para>
/// Every access-control defect found in this project has had one shape: a declaration that is read,
/// validated, and never checked against the running system on one of the paths it reaches. Owner
/// scoping was correct everywhere and asserted nowhere. Masking on GraphQL was fixed after a leak
/// and left without a test, so deleting the fix passed every suite in the repository. Outbox
/// redaction had four unit tests of the mechanism and none of its only caller.
/// </para>
/// <para>
/// So coverage is computed from the generator's real output rather than from a table of what it is
/// believed to emit. A table drifts silently, and a coverage report that overstates itself is worse
/// than none -- it is the same vacuous green it exists to find. If
/// <see cref="AutomatedTestSuiteGenerator"/> stops emitting an assertion, this stops claiming it.
/// </para>
/// <para>
/// It reports coverage, not correctness. "Asserted" means an assertion exists that would fail if
/// the control were removed. Whether it passes is answered by running it.
/// </para>
/// </remarks>
public static class EnforcementCoverage
{
    public static IReadOnlyList<EnforcementClaim> Analyse(SchemaModel schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        // The artifact itself, not a belief about it.
        var suites = AutomatedTestSuiteGenerator.GenerateAllTestSuites(schema);
        var claims = new List<EnforcementClaim>();

        foreach (var entity in schema.Entities ?? new List<Entity>())
        {
            var rest = Suite(suites, $"{entity.Name}RestApiTests.cs");
            var graphQl = Suite(suites, $"{entity.Name}GraphQLTests.cs");
            var realTime = Suite(suites, $"{entity.Name}RealTimeTests.cs");
            var servesRest = (entity.ApiEnabledMethods ?? new List<string>()).Count > 0;

            if (entity.OwnerScoped)
            {
                if (servesRest)
                {
                    claims.Add(Claim(entity, "ownerScoped", "REST",
                        rest.Contains("OwnerScoping_AnotherCallerInTheSameTenantIsDeniedTheRow"),
                        "a second caller in the same tenant is denied a row they do not own",
                        "nothing denies a non-owner here"));
                }

                if (entity.GraphQlEnabled)
                {
                    claims.Add(Claim(entity, "ownerScoped", "GraphQL",
                        graphQl.Contains("OwnerScoping_"),
                        "a non-owner is denied through the resolver",
                        "the resolver reads through Repository.Query(), which carries the owner filter, "
                        + "but nothing asserts it -- and Query() is the path masking leaked through twice"));
                }

                if (entity.OwnerExemptRoles.Count > 0)
                {
                    claims.Add(Claim(entity, $"ownerExemptRoles[{Join(entity.OwnerExemptRoles)}]", "REST",
                        rest.Contains("OwnerScoping_AnExemptRoleStillSeesTheRow"),
                        "a caller holding one of the roles sees rows they do not own",
                        "an exemption that does not exempt is as wrong as a filter that does not filter"));
                }

                if (entity.OwnerReadExemptRoles.Count > 0)
                {
                    claims.Add(Claim(entity, $"ownerReadExemptRoles[{Join(entity.OwnerReadExemptRoles)}]", "REST",
                        rest.Contains("OwnerScoping_AReadExemptRoleSeesTheRow"),
                        "a caller holding one of the read-exempt roles sees rows they do not own",
                        "read-exempt is a narrower grant than ownerExemptRoles and needs its own assertion"));
                }
            }

            if (entity.MultiTenant && servesRest)
            {
                claims.Add(Claim(entity, "multiTenant", "REST",
                    rest.Contains("Tenancy_"),
                    "a caller in another tenant is denied",
                    "no generated assertion crosses a tenant boundary on this egress"));
            }

            foreach (var (name, kind) in Sensitive(entity))
            {
                if (servesRest)
                {
                    claims.Add(Claim(entity, $"{kind}:{name}", "REST",
                        rest.Contains($"Protection_{name}"),
                        "the value is not returned in the clear",
                        "nothing reads this property back to see what a caller receives"));
                }

                if (entity.GraphQlEnabled)
                {
                    claims.Add(Claim(entity, $"{kind}:{name}", "GraphQL",
                        graphQl.Contains($"Protection_{name}"),
                        "the value is not returned in the clear through the resolver",
                        "findings 7 and 8 were exactly this -- protected fields raw over GraphQL while "
                        + "REST protected them, because Query() has nothing materialised to mask"));
                }

                if (entity.KafkaOutboxEnabled)
                {
                    claims.Add(Claim(entity, $"{kind}:{name}", "Kafka outbox",
                        rest.Contains($"Outbox_{name}IsRedacted"),
                        "the value does not reach the outbox payload",
                        "the entity is captured from the caller's command before the repository encrypts "
                        + "it, so this egress cannot inherit the repository's protection"));
                }
            }

            if (entity.KafkaOutboxEnabled)
            {
                claims.Add(Claim(entity, "enableKafkaOutbox", "Kafka outbox",
                    rest.Contains("Outbox_APublishedRowExists"),
                    "a row reaches the outbox for an entity that opted in",
                    "nothing asserts this entity publishes at all, so a silent stop would go unnoticed"));
            }

            if (entity.RealTime)
            {
                if (entity.RealTimeRoles.Count > 0)
                {
                    claims.Add(Claim(entity, $"realTimeRoles[{Join(entity.RealTimeRoles)}]", "Real-time",
                        realTime.Contains("TheChannelWithholdsFromACallerWithoutTheRole"),
                        "a caller holding none of the roles receives none of this entity's mutations",
                        "the emitted real-time suite asserts the channels refuse an anonymous client and "
                        + "that the hub negotiates -- neither reads the stream"));
                }

                if (entity.MultiTenant)
                {
                    claims.Add(Claim(entity, "realTime + multiTenant", "Real-time",
                        realTime.Contains("TheChannelWithholdsAnotherTenantsMutation"),
                        "a caller in another tenant receives none of this entity's mutations",
                        "SSE and WebSockets deliver through the tenant-checking overload of MayObserve"));
                }
            }
        }

        return claims;
    }

    /// <summary>Renders the report, and says plainly what it does not know.</summary>
    public static string Render(IReadOnlyList<EnforcementClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var report = new StringBuilder();
        var gaps = claims.Where(c => c.State == CoverageState.NotAsserted).ToList();
        var asserted = claims.Count - gaps.Count;

        report.AppendLine();
        report.AppendLine($"Enforcement coverage: {asserted} of {claims.Count} declaration/egress pairs asserted.");
        report.AppendLine();

        if (gaps.Count > 0)
        {
            report.AppendLine("NOT ASSERTED -- declared, reaches this egress, and nothing checks it there:");
            report.AppendLine();

            foreach (var group in gaps.GroupBy(g => g.Entity).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"  {group.Key}");
                foreach (var gap in group.OrderBy(g => g.Egress, StringComparer.Ordinal))
                {
                    report.AppendLine($"    {gap.Declaration}  ->  {gap.Egress}");
                    report.AppendLine($"      {gap.Detail}");
                }
                report.AppendLine();
            }
        }

        report.AppendLine("This reports coverage, not correctness. 'Asserted' means an assertion exists that");
        report.AppendLine("would fail if the control were removed. Whether it passes is answered by running it");
        report.AppendLine("against a live application, which this command does not do.");

        return report.ToString();
    }

    private static EnforcementClaim Claim(
        Entity entity, string declaration, string egress, bool asserted, string assertedDetail, string gapDetail)
        => new(entity.Name, declaration, egress,
            asserted ? CoverageState.Asserted : CoverageState.NotAsserted,
            asserted ? assertedDetail : gapDetail);

    /// <summary>
    /// Roles on one grant, rendered as one claim rather than a row each.
    /// </summary>
    /// <remarks>
    /// A row per role reads as though each were a separate egress to cover. They are alternatives
    /// on the same channel -- one assertion covers the grant -- so counting them separately inflates
    /// the denominator and buries the gaps that are real.
    /// </remarks>
    private static string Join(IEnumerable<string> roles) => string.Join(", ", roles);

    private static string Suite(Dictionary<string, string> suites, string file)
        => suites.TryGetValue(file, out var content) ? content : string.Empty;

    /// <summary>Properties carrying a mask or encrypt declaration, paired with which one it is.</summary>
    private static List<(string Name, string Kind)> Sensitive(Entity entity)
        => (entity.Properties ?? new List<Property>())
            .Select(p => (p.Name, Kind: p.Attributes.FirstOrDefault(
                a => a.StartsWith("Mask", StringComparison.Ordinal)
                  || string.Equals(a, "Encrypt", StringComparison.Ordinal))))
            .Where(p => !string.IsNullOrEmpty(p.Kind))
            .Select(p => (p.Name, Kind: p.Kind!))
            .ToList();
}
